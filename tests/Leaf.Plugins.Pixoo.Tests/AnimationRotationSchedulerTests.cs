using System.Collections.Concurrent;

namespace Leaf.Plugins.Pixoo.Tests;

public sealed class AnimationRotationSchedulerTests
{
    [Fact]
    public async Task Resting_state_rotates_complete_composite_without_still_reuploads()
    {
        var clock = new StepClock();
        var sink = new RecordingSink();
        var settings = new PixooPlaybackSettings
        {
            RotationMinimum = TimeSpan.FromMinutes(10),
            RotationMaximum = TimeSpan.FromMinutes(20),
        };
        using var scheduler = new AnimationRotationScheduler(clock, new FixedRandom(TimeSpan.FromMinutes(15)), settings);
        await using var queue = new DevicePushQueue(sink, clock);

        scheduler.Replace(Device(), FaceState.Resting, null, "state.changed", RestingProgram(), queue);
        await sink.WaitForCount(1);
        Assert.Equal((byte)1, sink.Pushes[0].Loop.Frames[0].Pixels[0]);

        var gap = await clock.NextDelay();
        Assert.Equal(TimeSpan.FromMinutes(15), gap.Duration);
        gap.Complete();
        await sink.WaitForCount(2);
        Assert.Equal("animation.rotate", sink.Pushes[1].Cause);
        Assert.Equal((byte)2, sink.Pushes[1].Loop.Frames[0].Pixels[0]);
    }

    [Fact]
    public async Task Real_state_change_cancels_pending_rotation()
    {
        var clock = new StepClock();
        var sink = new RecordingSink();
        using var scheduler = new AnimationRotationScheduler(clock, new FixedRandom(TimeSpan.FromMinutes(15)), new());
        await using var queue = new DevicePushQueue(sink, clock);
        var device = Device();

        scheduler.Replace(device, FaceState.Resting, null, "resting", RestingProgram(), queue);
        await sink.WaitForCount(1);
        var cancelledGap = await clock.NextDelay();
        scheduler.Replace(device, FaceState.Speaking, null, "speaking", ContinuousProgram(), queue);
        await queue.DrainAsync();
        await cancelledGap.WaitForCancellation();

        Assert.Equal(2, sink.Pushes.Count);
        Assert.True(cancelledGap.Cancelled);
        Assert.Equal(FaceState.Speaking, sink.Pushes[^1].State);
        Assert.Equal((byte)3, sink.Pushes[^1].Loop.Frames[0].Pixels[0]);
    }

    [Fact]
    public async Task Rotation_obeys_the_device_queue_minimum_gap()
    {
        var clock = new StepClock();
        var sink = new RecordingSink();
        var settings = new PixooPlaybackSettings
        {
            RotationMinimum = TimeSpan.FromMilliseconds(100),
            RotationMaximum = TimeSpan.FromMilliseconds(100),
        };
        using var scheduler = new AnimationRotationScheduler(clock, new FixedRandom(TimeSpan.FromMilliseconds(100)), settings);
        await using var queue = new DevicePushQueue(sink, clock);

        scheduler.Replace(Device(minimumPushGapMs: 1000), FaceState.Resting, null, "resting", RestingProgram(), queue);
        await sink.WaitForCount(1);
        (await clock.NextDelay()).Complete();

        var pacing = await clock.NextDelay();
        Assert.Equal(TimeSpan.FromMilliseconds(900), pacing.Duration);
        Assert.Single(sink.Pushes);
        pacing.Complete();
        await sink.WaitForCount(2);
        Assert.Equal("animation.rotate", sink.Pushes[1].Cause);
    }

    [Fact]
    public void Default_composite_rotations_are_ten_to_twenty_minutes()
    {
        var settings = new PixooPlaybackSettings();
        Assert.Equal(TimeSpan.FromMinutes(10), settings.RotationMinimum);
        Assert.Equal(TimeSpan.FromMinutes(20), settings.RotationMaximum);
    }

    private static PixooDevice Device(int minimumPushGapMs = 0)
        => new(Guid.NewGuid(), "test", DeviceKind.Emulator, null, 100, Guid.NewGuid(), "#ffffff", minimumPushGapMs);

    private static DisplayProgram RestingProgram()
        => new(Loop(0), Loop(1), DisplayPlaybackMode.Continuous, [Loop(2), Loop(3)]);

    private static DisplayProgram ContinuousProgram()
        => new(Loop(1), Loop(3), DisplayPlaybackMode.Continuous);

    private static FrameLoop Loop(byte value) => new([Frame(value, 1000)]);
    private static RgbFrame Frame(byte value, int duration) => new(Enumerable.Repeat(value, RgbFrame.ByteCount).ToArray(), duration);

    private sealed class FixedRandom(TimeSpan value) : IRotationRandom
    {
        public TimeSpan Next(TimeSpan minimum, TimeSpan maximum)
        {
            Assert.InRange(value, minimum, maximum);
            return value;
        }
    }

    private sealed class RecordingSink : IDisplaySink
    {
        private readonly ConcurrentQueue<DisplayPush> pushes = new();
        public IReadOnlyList<DisplayPush> Pushes => pushes.ToArray();

        public Task PushAsync(PixooDevice device, DisplayPush push, CancellationToken ct = default)
        {
            pushes.Enqueue(push);
            return Task.CompletedTask;
        }

        public async Task WaitForCount(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (pushes.Count < count) await Task.Delay(1, timeout.Token);
        }
    }

    private sealed class StepClock : IPushClock
    {
        private readonly ConcurrentQueue<DelayStep> delays = new();
        private readonly SemaphoreSlim available = new(0);
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        public async Task Delay(TimeSpan duration, CancellationToken ct)
        {
            var step = new DelayStep(duration, () => UtcNow += duration);
            using var registration = ct.Register(step.MarkCancelled);
            delays.Enqueue(step);
            available.Release();
            try { await step.Completion.Task.WaitAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                step.MarkCancelled();
                throw;
            }
        }

        public async Task<DelayStep> NextDelay()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await available.WaitAsync(timeout.Token);
            Assert.True(delays.TryDequeue(out var step));
            return step!;
        }
    }

    private sealed class DelayStep(TimeSpan duration, Action advance)
    {
        private readonly TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TimeSpan Duration { get; } = duration;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }
        public void Complete() { advance(); Completion.TrySetResult(); }
        public void MarkCancelled() { Cancelled = true; cancelled.TrySetResult(); }
        public Task WaitForCancellation() => cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
