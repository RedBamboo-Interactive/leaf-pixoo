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
    private readonly CancellationTokenSource stopping = new();
    private (PixooDevice Device, DisplayPush Push)? pending;
    private Task? worker;
    private DateTimeOffset? lastPush;

    /// <summary>Most recent push failure, cleared by the next successful push.</summary>
    public string? LastError { get; private set; }

    public void Enqueue(PixooDevice device, DisplayPush push)
    {
        push.Loop.Validate();
        lock (gate)
        {
            pending = (device, push);
            if (worker is null || worker.IsCompleted) worker = Task.Run(RunAsync);
        }
    }

    public async Task DrainAsync(CancellationToken ct = default)
    {
        Task? current;
        lock (gate) current = worker;
        if (current is not null) await current.WaitAsync(ct);
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
            var gap = TimeSpan.FromMilliseconds(Math.Max(0, next.Device.MinimumPushGapMs));
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
                await sink.PushAsync(next.Device, next.Push, stopping.Token);
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

    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        Task? current;
        lock (gate) current = worker;
        if (current is not null)
        {
            try { await current; }
            catch (OperationCanceledException) { }
        }
        stopping.Dispose();
    }
}
