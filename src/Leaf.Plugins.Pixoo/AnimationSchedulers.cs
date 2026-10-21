using System.Collections.Concurrent;

namespace Leaf.Plugins.Pixoo;

public interface IRotationRandom
{
    TimeSpan Next(TimeSpan minimum, TimeSpan maximum);
}

public sealed class SystemRotationRandom : IRotationRandom
{
    public TimeSpan Next(TimeSpan minimum, TimeSpan maximum)
    {
        if (minimum <= TimeSpan.Zero || maximum < minimum) throw new ArgumentOutOfRangeException(nameof(minimum));
        return TimeSpan.FromMilliseconds(Random.Shared.NextInt64(
            (long)minimum.TotalMilliseconds, (long)maximum.TotalMilliseconds + 1));
    }
}

/// <summary>
/// Rotates complete long-form resting GIFs infrequently. It never replays a motion/still pair:
/// pauses live inside each GIF, and every real state change cancels the pending rotation.
/// </summary>
public sealed class AnimationRotationScheduler(
    IPushClock clock,
    IRotationRandom random,
    PixooPlaybackSettings settings) : IDisposable
{
    private sealed class Rotation(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task? Task { get; set; }
    }

    private readonly ConcurrentDictionary<Guid, Rotation> active = new();

    public void Replace(PixooDevice device, FaceState state, FaceGesture? gesture, string cause,
        DisplayProgram program, DevicePushQueue queue)
    {
        program.Validate();
        Cancel(device.Id);
        queue.Enqueue(device, new(program.InitialLoop, state, gesture, cause, clock.UtcNow, AnimationName(state, 0)));
        if (state != FaceState.Resting || program.Alternatives is not { Count: > 0 }) return;

        var rotation = new Rotation(new CancellationTokenSource());
        active[device.Id] = rotation;
        rotation.Task = RunAsync(device, state, program.Alternatives, queue, rotation);
    }

    public void Cancel(Guid deviceId)
    {
        if (active.TryRemove(deviceId, out var rotation)) rotation.Cancellation.Cancel();
    }

    private async Task RunAsync(PixooDevice device, FaceState state, IReadOnlyList<FrameLoop> alternatives,
        DevicePushQueue queue, Rotation rotation)
    {
        var ct = rotation.Cancellation.Token;
        try
        {
            await queue.DrainAsync(ct);
            var index = 0;
            while (true)
            {
                await clock.Delay(random.Next(settings.RotationMinimum, settings.RotationMaximum), ct);
                ct.ThrowIfCancellationRequested();
                var loop = alternatives[index++ % alternatives.Count];
                queue.Enqueue(device, new(loop, state, null, "animation.rotate", clock.UtcNow,
                    AnimationName(state, index)));
                await queue.DrainAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            if (active.TryGetValue(device.Id, out var current) && ReferenceEquals(current, rotation))
                active.TryRemove(device.Id, out _);
            rotation.Cancellation.Dispose();
        }
    }

    private static string AnimationName(FaceState state, int variant)
        => state.ToString().ToLowerInvariant() + (variant == 0 ? string.Empty : $"-{variant + 1}");

    public void Dispose()
    {
        foreach (var deviceId in active.Keys) Cancel(deviceId);
    }
}

public sealed class AmbientStateScheduler(PixooPlaybackSettings settings) : IDisposable
{
    private readonly CancellationTokenSource stopping = new();
    private Task? worker;

    public void Start(Func<FaceState, CancellationToken, Task> apply)
    {
        worker ??= Task.Run(async () =>
        {
            try
            {
                while (!stopping.IsCancellationRequested)
                {
                    try { await apply(AmbientStateSelector.Select(DateTimeOffset.Now, settings), stopping.Token); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { }
                    await Task.Delay(TimeSpan.FromMinutes(1), stopping.Token);
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        }, stopping.Token);
    }

    public void Dispose()
    {
        stopping.Cancel();
        stopping.Dispose();
    }
}
