using System.Collections.Concurrent;

namespace Leaf.Plugins.Pixoo.Tests;

public sealed class MotionReplaySchedulerTests
{
    [Fact]
    public async Task Held_state_shows_still_then_exactly_one_motion_cycle_then_still()
    {
        var clock = new StepClock();
        var sink = new RecordingSink();
        var settings = new PortraitClipSettings { MotionPlaybackMargin = TimeSpan.FromMilliseconds(500) };
        using var scheduler = new MotionReplayScheduler(clock, new FixedRandom(TimeSpan.FromSeconds(20)), settings);
        await using var queue = new DevicePushQueue(sink, clock);

        scheduler.Replace(Device(), FaceState.Resting, null, "state.changed", HeldProgram(), queue);
        await sink.WaitForCount(1);
        Assert.Equal((byte)1, sink.Pushes[0].Loop.Frames[0].Pixels[0]);

        var gap = await clock.NextDelay();
        Assert.Equal(TimeSpan.FromSeconds(20), gap.Duration);
        gap.Complete();
        await sink.WaitForCount(2);
        Assert.Equal("motion.replay", sink.Pushes[1].Cause);
        Assert.Equal(2, sink.Pushes[1].Loop.Frames.Count);

        var playback = await clock.NextDelay();
        Assert.Equal(TimeSpan.FromMilliseconds(700), playback.Duration);
        playback.Complete();
        await sink.WaitForCount(3);
        Assert.Equal("motion.still", sink.Pushes[2].Cause);
        Assert.Single(sink.Pushes[2].Loop.Frames);
    }

    [Fact]
    public async Task Real_state_change_cancels_pending_held_replay()
    {
        var clock = new StepClock();
        var sink = new RecordingSink();
        using var scheduler = new MotionReplayScheduler(clock, new FixedRandom(TimeSpan.FromSeconds(20)), new());
        await using var queue = new DevicePushQueue(sink, clock);
        var device = Device();

        scheduler.Replace(device, FaceState.Resting, null, "resting", HeldProgram(), queue);
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
    public async Task Replay_upload_obeys_the_device_queue_minimum_gap()
    {
        var clock = new StepClock();
        var sink = new RecordingSink();
        var settings = new PortraitClipSettings
        {
            ReplayGaps = new Dictionary<FaceState, MotionReplayRange>
            {
                [FaceState.Resting] = new(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100)),
            },
        };
        using var scheduler = new MotionReplayScheduler(clock, new FixedRandom(TimeSpan.FromMilliseconds(100)), settings);
        await using var queue = new DevicePushQueue(sink, clock);

        scheduler.Replace(Device(minimumPushGapMs: 1000), FaceState.Resting, null, "resting", HeldProgram(), queue);
        await sink.WaitForCount(1);
        (await clock.NextDelay()).Complete();

        var pacing = await clock.NextDelay();
        Assert.Equal(TimeSpan.FromMilliseconds(900), pacing.Duration);
        Assert.Single(sink.Pushes);
        pacing.Complete();
        await sink.WaitForCount(2);
        Assert.Equal("motion.replay", sink.Pushes[1].Cause);
    }

    [Fact]
    public void Default_held_state_gaps_are_randomised_between_fifteen_and_thirty_seconds()
    {
        var settings = new PortraitClipSettings();
        foreach (var state in new[] { FaceState.Resting, FaceState.Listening, FaceState.Thinking, FaceState.Dreaming, FaceState.Asleep })
            Assert.Equal(MotionReplayRange.Default, settings.ReplayGapFor(state));
        Assert.Null(settings.ReplayGapFor(FaceState.Speaking));
    }

    private static PixooDevice Device(int minimumPushGapMs = 0)
        => new(Guid.NewGuid(), "test", DeviceKind.Emulator, null, 100, Guid.NewGuid(), "#ffffff", minimumPushGapMs);

    private static DisplayProgram HeldProgram()
        => new(Loop(1), new FrameLoop([Frame(2, 100), Frame(2, 100)]), DisplayPlaybackMode.Held);

    private static DisplayProgram ContinuousProgram()
        => new(Loop(1), Loop(3), DisplayPlaybackMode.Continuous);

    private static FrameLoop Loop(byte value) => new([Frame(value, 1000)]);
    private static RgbFrame Frame(byte value, int duration) => new(Enumerable.Repeat(value, RgbFrame.ByteCount).ToArray(), duration);

    private sealed class FixedRandom(TimeSpan value) : IMotionGapRandom
    {
        public TimeSpan Next(MotionReplayRange range)
        {
            Assert.InRange(value, range.Minimum, range.Maximum);
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
