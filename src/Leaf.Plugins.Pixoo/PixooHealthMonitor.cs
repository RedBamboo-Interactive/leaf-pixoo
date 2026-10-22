using System.Collections.Concurrent;

namespace Leaf.Plugins.Pixoo;

public sealed record PixooHealthTarget(PixooDevice Device, DevicePushQueue Queue);

/// <summary>Low-rate, serialized health checks for explicitly enabled physical Pixoos.</summary>
public sealed class PixooHealthMonitor(
    IPushClock clock,
    PixooPlaybackSettings settings,
    IPixooHealthTransport transport,
    DisplayPlaybackStatusStore statuses,
    CurrentDisplayStore currentDisplays) : IAsyncDisposable
{
    private sealed class State
    {
        public bool Seen;
        public bool Offline;
        public bool RecoveryPending;
        public bool ScreenOffSuppressed;
        public int? LastIndex;
        public int ConsecutiveFailures;
        public int RecoveryAttemptsWithoutHealthyProbe;
        public DateTimeOffset? NextRecoveryAt;
        public DateTimeOffset? LastRecoveryAt;
    }

    private readonly ConcurrentDictionary<Guid, State> devices = new();
    private readonly CancellationTokenSource stopping = new();
    private Task? worker;

    public void Start(Func<CancellationToken, Task<IReadOnlyList<PixooHealthTarget>>> targets)
    {
        if (worker is not null) return;
        worker = Task.Run(() => RunAsync(targets, stopping.Token), CancellationToken.None);
    }

    internal async Task RunAsync(Func<CancellationToken, Task<IReadOnlyList<PixooHealthTarget>>> targets,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await clock.Delay(settings.HealthInterval, ct);
            IReadOnlyList<PixooHealthTarget> current;
            try { current = await targets(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch { continue; }
            await RunCycleAsync(current, ct);
        }
    }

    internal async Task RunCycleAsync(IEnumerable<PixooHealthTarget> targets, CancellationToken ct = default)
    {
        foreach (var target in targets)
        {
            if (target.Device.Kind != DeviceKind.Pixoo64 || !target.Device.HardwareEnabled) continue;
            try { await ProbeAsync(target, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* One bad device must not stop health checks for the others. */ }
        }
    }

    private async Task ProbeAsync(PixooHealthTarget target, CancellationToken ct)
    {
        var device = target.Device;
        var state = devices.GetOrAdd(device.Id, _ => new());
        await target.Queue.TryRunMaintenanceAsync(device, async (markSwap, maintenanceCt) =>
        {
            var probedAt = clock.UtcNow;
            PixooIndexProbe probe;
            try { probe = await transport.GetIndexAsync(device, maintenanceCt); }
            catch (OperationCanceledException) when (maintenanceCt.IsCancellationRequested) { throw; }
            catch
            {
                MarkOffline(device.Id, state, probedAt);
                return;
            }

            var cameBack = state.Offline;
            var indexTransition = state.LastIndex != probe.SelectIndex;
            var firstProbe = !state.Seen;
            state.Seen = true;
            state.Offline = false;
            state.ConsecutiveFailures = 0;
            if (cameBack) currentDisplays.RecordTransition(device.Id, "health.online", probedAt);

            var mismatch = probe.SelectIndex != 4;
            if (!mismatch && !cameBack)
            {
                state.RecoveryPending = false;
                state.ScreenOffSuppressed = false;
                state.RecoveryAttemptsWithoutHealthyProbe = 0;
                state.NextRecoveryAt = null;
            }

            // GetAllConf is deliberately transition-only. It keeps a user-requested screen-off from
            // being mistaken for a reboot without adding a second request to every minute's probe.
            if (cameBack || mismatch && (firstProbe || indexTransition))
            {
                try
                {
                    state.ScreenOffSuppressed = await transport.GetLightSwitchAsync(device, maintenanceCt) == 0;
                }
                catch (OperationCanceledException) when (maintenanceCt.IsCancellationRequested) { throw; }
                catch
                {
                    MarkOffline(device.Id, state, probedAt);
                    state.LastIndex = probe.SelectIndex;
                    return;
                }
            }

            state.LastIndex = probe.SelectIndex;
            var current = currentDisplays.Get(device.Id);
            var recoverable = current is { Publication: not null }
                && current.Push.State != FaceState.Dark
                && !state.ScreenOffSuppressed;
            if (recoverable && (mismatch || cameBack)) state.RecoveryPending = true;

            if (!state.RecoveryPending || !recoverable
                || state.NextRecoveryAt is { } next && probedAt < next)
            {
                statuses.Health(device.Id, Health("online", state, probedAt));
                return;
            }

            try
            {
                await transport.RedeliverAsync(device, current!, probedAt, markSwap, maintenanceCt);
                state.RecoveryPending = false;
                state.LastRecoveryAt = clock.UtcNow;
                state.NextRecoveryAt = clock.UtcNow + RecoveryBackoff(state);
                state.RecoveryAttemptsWithoutHealthyProbe++;
                statuses.Health(device.Id, Health("recovered", state, probedAt));
                currentDisplays.RecordTransition(device.Id, "health.recovered", state.LastRecoveryAt.Value);
            }
            catch (OperationCanceledException) when (maintenanceCt.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                state.NextRecoveryAt = clock.UtcNow + RecoveryBackoff(state);
                state.RecoveryAttemptsWithoutHealthyProbe++;
                statuses.DeliveryProblem(device.Id, $"Pixoo health recovery failed: {ex.Message}");
                statuses.Health(device.Id, Health("online", state, probedAt));
            }
        }, ct);
    }

    private void MarkOffline(Guid deviceId, State state, DateTimeOffset probedAt)
    {
        var transition = !state.Offline;
        state.Seen = true;
        state.Offline = true;
        state.ConsecutiveFailures++;
        if (transition) currentDisplays.RecordTransition(deviceId, "health.offline", probedAt);
        statuses.Health(deviceId, Health("offline", state, probedAt));
    }

    private static DisplayHealthStatus Health(string value, State state, DateTimeOffset probedAt)
        => new(value, probedAt, state.LastRecoveryAt, state.ConsecutiveFailures);

    private TimeSpan RecoveryBackoff(State state)
    {
        var multiplier = Math.Pow(2, Math.Min(10, state.RecoveryAttemptsWithoutHealthyProbe));
        var ticks = Math.Min(settings.HealthRecoveryMaximumBackoff.Ticks,
            (long)(settings.HealthRecoveryInitialBackoff.Ticks * multiplier));
        return TimeSpan.FromTicks(ticks);
    }

    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        if (worker is not null)
        {
            try { await worker; }
            catch (OperationCanceledException) { }
        }
        stopping.Dispose();
    }
}
