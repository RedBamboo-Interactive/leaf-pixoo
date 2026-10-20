using System.Collections.Concurrent;

namespace Leaf.Plugins.Pixoo.Tests;

public sealed class PushQueueTests
{
    [Fact]
    public async Task Queue_is_serial_paced_and_coalesces_to_latest_state()
    {
        var sink = new RecordingSink();
        var clock = new ControlledClock();
        await using var queue = new DevicePushQueue(sink, clock);
        var device = Device();
        queue.Enqueue(device, Push(FaceState.Resting));
        await sink.WaitForCount(1);

        queue.Enqueue(device, Push(FaceState.Thinking));
        await clock.WaitForDelay();
        queue.Enqueue(device, Push(FaceState.Speaking));
        clock.ReleaseDelay(TimeSpan.FromSeconds(1));
        await queue.DrainAsync();

        Assert.Equal([FaceState.Resting, FaceState.Speaking], sink.States);
        Assert.Equal(1, clock.DelayCount);
        Assert.True(sink.MaximumConcurrency <= 1);
    }

    private static PixooDevice Device() => new(Guid.NewGuid(), "test", DeviceKind.Emulator, null, 80, Guid.NewGuid(), "#44ccaa", 1000);
    private static DisplayPush Push(FaceState state) => new(new([new(new byte[RgbFrame.ByteCount], 100)]), state, null, "test", DateTimeOffset.UtcNow);

    private sealed class RecordingSink : IDisplaySink
    {
        private readonly ConcurrentQueue<FaceState> states = new();
        private int active;
        public int MaximumConcurrency { get; private set; }
        public FaceState[] States => states.ToArray();
        public async Task PushAsync(PixooDevice device, DisplayPush push, CancellationToken ct = default)
        {
            var now = Interlocked.Increment(ref active);
            MaximumConcurrency = Math.Max(MaximumConcurrency, now);
            states.Enqueue(push.State);
            await Task.Yield();
            Interlocked.Decrement(ref active);
        }
        public async Task WaitForCount(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (states.Count < count) await Task.Delay(1, timeout.Token);
        }
    }

    private sealed class ControlledClock : IPushClock
    {
        private readonly TaskCompletionSource delayed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        public int DelayCount { get; private set; }
        public async Task Delay(TimeSpan duration, CancellationToken ct)
        {
            DelayCount++;
            delayed.TrySetResult();
            await release.Task.WaitAsync(ct);
        }
        public Task WaitForDelay() => delayed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        public void ReleaseDelay(TimeSpan advance) { UtcNow += advance; release.TrySetResult(); }
    }
}
