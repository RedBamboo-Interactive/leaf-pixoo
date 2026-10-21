using System.Collections.Concurrent;
using System.Diagnostics;

namespace Leaf.Plugins.Pixoo;

public sealed record PortraitClipSettings
{
    public int Width { get; init; } = 768;
    public int Height { get; init; } = 576;
    public int SourceFrames { get; init; } = 41;
    public int InterpolationMultiplier { get; init; } = 2;
    public int Steps { get; init; } = 30;
    public int SampleStride { get; init; } = 4;
    public int FrameDurationMs { get; init; } = 133;
    public int MaximumLoopFrames { get; init; } = 30;
    public int MaximumAttemptsPerClip { get; init; } = 2;
    public TimeSpan RetryCooldown { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan AgentFailureCooldown { get; init; } = TimeSpan.FromMinutes(10);
    public IReadOnlyDictionary<FaceState, int> FirstFrameHoldMs { get; init; } =
        new Dictionary<FaceState, int>
        {
            [FaceState.Resting] = 4000,
            [FaceState.Speaking] = 133,
            [FaceState.Listening] = 4000,
            [FaceState.Thinking] = 4000,
            [FaceState.Dreaming] = 4000,
        };

    public int HoldFor(FaceState state) => FirstFrameHoldMs.GetValueOrDefault(state, FrameDurationMs);
}

public sealed record DecodedVideoFrame(byte[] Pixels, int Width, int Height);

public interface IVideoFrameDecoder
{
    Task<IReadOnlyList<DecodedVideoFrame>> DecodeAsync(byte[] video, int width, int height, CancellationToken ct);
}

public sealed class FfmpegVideoFrameDecoder : IVideoFrameDecoder
{
    public async Task<IReadOnlyList<DecodedVideoFrame>> DecodeAsync(byte[] video, int width, int height, CancellationToken ct)
    {
        var start = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-i", "pipe:0", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1" })
            start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("ffmpeg did not start.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("Wan clip decoding requires ffmpeg on PATH.", ex);
        }

        try
        {
            var errorTask = process.StandardError.ReadToEndAsync(ct);
            var writeTask = Task.Run(async () =>
            {
                await process.StandardInput.BaseStream.WriteAsync(video, ct);
                await process.StandardInput.BaseStream.FlushAsync(ct);
                process.StandardInput.Close();
            }, ct);

            var frames = new List<DecodedVideoFrame>();
            var frameBytes = checked(width * height * 3);
            while (true)
            {
                var frame = new byte[frameBytes];
                var read = await ReadFrameAsync(process.StandardOutput.BaseStream, frame, ct);
                if (read == 0) break;
                if (read != frame.Length) throw new InvalidDataException("ffmpeg returned a truncated RGB frame.");
                frames.Add(new(frame, width, height));
            }
            await writeTask;
            await process.WaitForExitAsync(ct);
            var error = await errorTask;
            if (process.ExitCode != 0)
                throw new InvalidDataException($"ffmpeg could not decode the Wan clip: {Compact(error)}");
            if (frames.Count == 0) throw new InvalidDataException("The Wan clip contained no decodable frames.");
            return frames;
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
        }
    }

    private static async Task<int> ReadFrameAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (count == 0) break;
            read += count;
        }
        return read;
    }

    private static string Compact(string value)
    {
        var compact = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact[..Math.Min(300, compact.Length)];
    }
}

public static class PortraitClipProcessor
{
    public static FrameLoop BuildLoop(IReadOnlyList<DecodedVideoFrame> decoded, VideoPlateGeometry plate,
        FaceState state, PortraitClipSettings settings)
    {
        if (decoded.Count < 2) throw new InvalidDataException("A loop needs at least two video frames so the duplicate closing frame can be dropped.");
        var frames = SampleIndices(decoded.Count, settings.SampleStride, settings.MaximumLoopFrames)
            .Select((index, position) =>
            {
                var frame = decoded[index];
                if (frame.Width != plate.OutputWidth || frame.Height != plate.OutputHeight)
                    throw new InvalidDataException($"Wan returned {frame.Width}x{frame.Height}; expected {plate.OutputWidth}x{plate.OutputHeight}.");
                var pixels = PortraitPackBuilder.BoxDownsampleRgb(frame.Pixels, frame.Width, frame.Height, plate.OutputSquare);
                return new RgbFrame(pixels, position == 0 ? settings.HoldFor(state) : settings.FrameDurationMs);
            }).ToArray();
        return new FrameLoop(frames).Validate();
    }

    internal static int[] SampleIndices(int frameCount, int stride, int maximumFrames)
    {
        if (frameCount < 2) return [];
        if (stride < 1) throw new ArgumentOutOfRangeException(nameof(stride));
        if (maximumFrames is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(maximumFrames));
        return Enumerable.Range(0, frameCount - 1)
            .Where(index => index % stride == 0)
            .Take(maximumFrames)
            .ToArray();
    }
}

public sealed record PortraitClipPrompt(string Prompt, string Negative);

public static class PortraitClipPrompts
{
    private const string Negative = "exaggerated expression, head turning away, camera movement, zoom, pan, cut, morphing face, identity change, outfit change, extra fingers, flicker, text, watermark";

    public static PortraitClipPrompt For(FaceState state, string? outfitDescription)
    {
        var motion = state switch
        {
            FaceState.Resting => "The subject is resting and present. Slow natural breathing, shoulders rising and falling gently, one natural blink, gaze soft and direct.",
            FaceState.Speaking => "The subject speaks naturally with readable conversational lip and jaw movement and a small, restrained head motion. No exaggerated expression.",
            FaceState.Listening => "The subject listens attentively with a small natural nod, steady expression, and closed mouth.",
            FaceState.Thinking => "The subject thinks quietly; the eyes drift slightly aside or upward, then return to the starting gaze.",
            FaceState.Dreaming => "The subject's eyes close naturally, remain closed through the middle while slow calm breathing continues, then return gently to the starting gaze.",
            _ => throw new ArgumentOutOfRangeException(nameof(state), "Only primary generated clip states have prompts."),
        };
        var scene = ScenePhrase(outfitDescription);
        var prompt = $"Use the reference image as the exact identity, outfit, scene, pose, and framing. {scene}{motion} The action ends exactly in the starting pose. Static locked-off camera, steady framing, natural light, seamless closed loop.";
        var stateNegative = state switch
        {
            FaceState.Resting or FaceState.Listening or FaceState.Thinking or FaceState.Dreaming
                => "talking, speaking, mouth opening, singing, big smile, laughing, " + Negative,
            FaceState.Speaking => "singing, shouting, big smile, laughing, extreme mouth opening, " + Negative,
            _ => Negative,
        };
        return new(prompt, stateNegative);
    }

    private static string ScenePhrase(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return string.Empty;
        var compact = string.Join(' ', description.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        compact = compact[..Math.Min(220, compact.Length)].TrimEnd('.', ',', ';', ':');
        return $"Preserve only these explicitly described outfit and scene details: {compact}. ";
    }
}

internal static class PortraitClipPlan
{
    public static readonly FaceState[] GenerationOrder =
        [FaceState.Resting, FaceState.Speaking, FaceState.Listening, FaceState.Thinking, FaceState.Dreaming];

    public static async Task ExecuteAsync(Func<FaceState, int, CancellationToken, Task<bool>> attempt,
        PortraitClipSettings settings, CancellationToken ct)
    {
        foreach (var state in GenerationOrder)
        {
            for (var number = 1; number <= settings.MaximumAttemptsPerClip; number++)
            {
                if (await attempt(state, number, ct)) break;
                if (number < settings.MaximumAttemptsPerClip && settings.RetryCooldown > TimeSpan.Zero)
                    await Task.Delay(settings.RetryCooldown, ct);
            }
        }
    }
}

internal sealed class AgentSingleFlight<T>
{
    private readonly ConcurrentDictionary<Guid, Lazy<Task<T>>> active = new();

    public Task<T> RunAsync(Guid agentId, Func<Task<T>> factory)
    {
        Lazy<Task<T>>? selected = null;
        selected = active.GetOrAdd(agentId, key => new Lazy<Task<T>>(async () =>
        {
            try { return await factory(); }
            finally { active.TryRemove(key, out var _); }
        }, LazyThreadSafetyMode.ExecutionAndPublication));
        return selected.Value;
    }
}
