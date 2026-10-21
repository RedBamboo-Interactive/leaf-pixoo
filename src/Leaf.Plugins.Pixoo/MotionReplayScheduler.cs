using System.Collections.Concurrent;

namespace Leaf.Plugins.Pixoo;

public interface IMotionGapRandom
{
    TimeSpan Next(MotionReplayRange range);
}

public sealed class SystemMotionGapRandom : IMotionGapRandom
{
    public TimeSpan Next(MotionReplayRange range)
    {
        range.Validate();
        var minimum = (long)range.Minimum.TotalMilliseconds;
        var maximum = (long)range.Maximum.TotalMilliseconds;
        return TimeSpan.FromMilliseconds(Random.Shared.NextInt64(minimum, maximum + 1));
    }
}

/// <summary>
/// Drives held portrait states without relying on per-frame holds, which the Pixoo ignores.
/// Every upload still passes through the device's serialized latest-wins queue.
/// </summary>
public sealed class MotionReplayScheduler(
    IPushClock clock,
    IMotionGapRandom random,
    PortraitClipSettings settings) : IDisposable
{
    private sealed class Replay(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task? Task { get; set; }
    }

    private readonly ConcurrentDictionary<Guid, Replay> active = new();

    public void Replace(PixooDevice device, FaceState state, FaceGesture? gesture, string cause,
        DisplayProgram program, DevicePushQueue queue)
    {
        program.Validate();
        Cancel(device.Id);
        queue.Enqueue(device, new(program.InitialLoop, state, gesture, cause, clock.UtcNow));

        var range = program.Mode == DisplayPlaybackMode.Held && program.Motion is not null
            ? settings.ReplayGapFor(state)
            : null;
        if (range is null) return;

        var replay = new Replay(new CancellationTokenSource());
        active[device.Id] = replay;
        replay.Task = RunAsync(device, state, program.Still, program.Motion!, range, queue, replay);
    }

    public void Cancel(Guid deviceId)
    {
        if (active.TryRemove(deviceId, out var replay)) replay.Cancellation.Cancel();
    }

    private async Task RunAsync(PixooDevice device, FaceState state, FrameLoop still, FrameLoop motion,
        MotionReplayRange range, DevicePushQueue queue, Replay replay)
    {
        var ct = replay.Cancellation.Token;
        try
        {
            // Start the quiet gap only after the still has actually reached the sink.
            await queue.DrainAsync(ct);
            while (true)
            {
                await clock.Delay(random.Next(range), ct);
                ct.ThrowIfCancellationRequested();
                queue.Enqueue(device, new(motion, state, null, "motion.replay", clock.UtcNow));
                await queue.DrainAsync(ct);

                var playback = TimeSpan.FromMilliseconds(motion.Frames.Sum(frame => frame.DurationMs))
                    + settings.MotionPlaybackMargin;
                await clock.Delay(playback, ct);
                ct.ThrowIfCancellationRequested();
                queue.Enqueue(device, new(still, state, null, "motion.still", clock.UtcNow));
                await queue.DrainAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            if (active.TryGetValue(device.Id, out var current) && ReferenceEquals(current, replay))
                active.TryRemove(device.Id, out _);
            replay.Cancellation.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var deviceId in active.Keys) Cancel(deviceId);
    }
}
