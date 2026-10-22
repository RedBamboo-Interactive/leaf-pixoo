namespace Leaf.Plugins.Pixoo;

public interface IPushClock
{
    DateTimeOffset UtcNow { get; }
    Task Delay(TimeSpan duration, CancellationToken ct);
}

public sealed class SystemPushClock : IPushClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public Task Delay(TimeSpan duration, CancellationToken ct) => Task.Delay(duration, ct);
}

/// <summary>One serialized, latest-wins queue for one physical or emulated device.</summary>
public sealed class DevicePushQueue(IDisplaySink sink, IPushClock clock) : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly CancellationTokenSource stopping = new();
    private (PixooDevice Device, DisplayPush Push)? pending;
    private Task? worker;
    private DateTimeOffset? lastPush;
    private CancellationTokenSource? maintenance;

    /// <summary>Most recent push failure, cleared by the next successful push.</summary>
    public string? LastError { get; private set; }

    internal const int HardwareMinimumGapMs = 5000;

    public void Enqueue(PixooDevice device, DisplayPush push)
    {
        push.Loop.Validate();
        lock (gate)
        {
            pending = (device, push);
            maintenance?.Cancel();
            if (worker is null || worker.IsCompleted) worker = Task.Run(RunAsync);
        }
    }

    public async Task DrainAsync(CancellationToken ct = default)
    {
        Task? current;
        lock (gate) current = worker;
        if (current is not null) await current.WaitAsync(ct);
    }

    /// <summary>
    /// Runs a lightweight device operation only while the push queue is idle and outside its push
    /// floor. A newly enqueued state cancels the operation and gets priority. Call <paramref
    /// name="markSwap"/> immediately before a maintenance operation that swaps the program so the
    /// following push also observes the hardware floor.
    /// </summary>
    public async Task<bool> TryRunMaintenanceAsync(PixooDevice device,
        Func<Action, CancellationToken, Task> operation, CancellationToken ct = default)
    {
        CancellationTokenSource linked;
        lock (gate)
        {
            if (pending is not null || worker is { IsCompleted: false } || maintenance is not null)
                return false;
            var quietWindow = PushGap(device);
            if (lastPush is { } previous && clock.UtcNow - previous < quietWindow)
                return false;
            linked = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token, ct);
            maintenance = linked;
        }

        try
        {
            await serial.WaitAsync(linked.Token);
            try
            {
                var marked = 0;
                void MarkSwap()
                {
                    if (Interlocked.Exchange(ref marked, 1) == 0)
                    {
                        lock (gate) lastPush = clock.UtcNow;
                    }
                }
                await operation(MarkSwap, linked.Token);
                return true;
            }
            finally { serial.Release(); }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { return false; }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(maintenance, linked)) maintenance = null;
            }
            linked.Dispose();
        }
    }

    private async Task RunAsync()
    {
        while (!stopping.IsCancellationRequested)
        {
            (PixooDevice Device, DisplayPush Push) next;
            lock (gate)
            {
                if (pending is null) { worker = null; return; }
                next = pending.Value;
                pending = null;
            }
            // Real Pixoos download a GIF per push; swapping faster than a few seconds rebooted the device.
            var gap = PushGap(next.Device);
            if (lastPush is { } previous)
            {
                var remaining = gap - (clock.UtcNow - previous);
                if (remaining > TimeSpan.Zero) await clock.Delay(remaining, stopping.Token);
            }
            // Coalesce again after waiting: a newer state supersedes the one that caused the wait.
            lock (gate)
            {
                if (pending is { } newer) { next = newer; pending = null; }
            }
            try
            {
                await serial.WaitAsync(stopping.Token);
                try { await sink.PushAsync(next.Device, next.Push, stopping.Token); }
                finally { serial.Release(); }
                LastError = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stopping.IsCancellationRequested)
            {
                // A failed push must not kill the worker silently; the next state change retries.
                LastError = $"{DateTimeOffset.UtcNow:O} {ex.GetType().Name}: {ex.Message}";
            }
            lastPush = clock.UtcNow;
        }
    }

    private static TimeSpan PushGap(PixooDevice device)
    {
        var floor = device.Kind == DeviceKind.Pixoo64 && device.HardwareEnabled ? HardwareMinimumGapMs : 0;
        return TimeSpan.FromMilliseconds(Math.Max(floor, Math.Max(0, device.MinimumPushGapMs)));
    }

    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        lock (gate) maintenance?.Cancel();
        Task? current;
        lock (gate) current = worker;
        if (current is not null)
        {
            try { await current; }
            catch (OperationCanceledException) { }
        }
        stopping.Dispose();
        // A maintenance caller observes cancellation and releases this gate asynchronously. Let the
        // semaphore be collected with the queue rather than racing that finally block during unload.
    }
}
