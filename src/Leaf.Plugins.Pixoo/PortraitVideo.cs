using System.Collections.Concurrent;
using System.Diagnostics;

namespace Leaf.Plugins.Pixoo;

public sealed record PortraitClipSettings
{
    public int Width { get; init; } = 384;
    public int Height { get; init; } = 384;
    public int SourceFrames { get; init; } = 41;
    public int Steps { get; init; } = 4;
    public int SamplerSplit { get; init; } = 2;
    public int OutputFps { get; init; } = 15;
    public int SampleStride { get; init; } = 2;
    public int FrameDurationMs { get; init; } = 100;
    public int MaximumLoopFrames { get; init; } = 30;
    public int SlowWidth { get; init; } = 768;
    public int SlowHeight { get; init; } = 576;
    public int SlowSteps { get; init; } = 30;
    public int SlowInterpolationMultiplier { get; init; } = 2;
    public int SlowSampleStride { get; init; } = 4;
    public int SlowFrameDurationMs { get; init; } = 100;
    public int MaximumAttemptsPerClip { get; init; } = 2;
    public TimeSpan RetryCooldown { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan AgentFailureCooldown { get; init; } = TimeSpan.FromMinutes(10);
    public int MaximumAlignmentShift { get; init; } = 12;
    public double StaticPixelThreshold { get; init; } = 8;
    public double MotionPixelThreshold { get; init; } = 5;
    public int MinimumMotionComponentPixels { get; init; } = 3;
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
    public static FrameLoop BuildMotionLoop(IReadOnlyList<DecodedVideoFrame> decoded, PortraitClipSettings settings,
        byte[]? referenceStill = null)
        => BuildMotionLoop(decoded, settings.Width, settings.Height,
            new ImageRect(0, 0, settings.Width, settings.Height), settings.SampleStride,
            settings.FrameDurationMs, settings.MaximumLoopFrames, referenceStill);

    public static FrameLoop BuildMotionLoop(IReadOnlyList<DecodedVideoFrame> decoded, int width, int height,
        ImageRect crop, int sampleStride, int frameDurationMs, int maximumFrames, byte[]? referenceStill = null)
    {
        if (decoded.Count < 2) throw new InvalidDataException("A loop needs at least two video frames so the duplicate closing frame can be dropped.");
        var pixels = SampleIndices(decoded.Count, sampleStride, maximumFrames)
            .Select(index =>
            {
                var frame = decoded[index];
                if (frame.Width != width || frame.Height != height)
                    throw new InvalidDataException($"Wan returned {frame.Width}x{frame.Height}; expected {width}x{height}.");
                return PortraitPackBuilder.BoxDownsampleRgb(frame.Pixels, frame.Width, frame.Height, crop);
            }).ToArray();
        if (referenceStill is not null) pixels = PortraitColorCorrector.Correct(pixels, referenceStill).ToArray();
        var frames = pixels.Select(frame => new RgbFrame(frame, frameDurationMs)).ToArray();
        return new FrameLoop(frames).Validate();
    }

    public static PortraitClipProcessingResult ProcessMotionLoop(IReadOnlyList<DecodedVideoFrame> decoded,
        PortraitClipSettings settings, byte[] referenceStill, byte[] sourceReference)
        => ProcessMotionLoop(decoded, settings.Width, settings.Height,
            new ImageRect(0, 0, settings.Width, settings.Height), settings.SampleStride,
            settings.FrameDurationMs, settings.MaximumLoopFrames, referenceStill, sourceReference, settings);

    public static PortraitClipProcessingResult ProcessMotionLoop(IReadOnlyList<DecodedVideoFrame> decoded,
        int width, int height, ImageRect crop, int sampleStride, int frameDurationMs, int maximumFrames,
        byte[] referenceStill, byte[] sourceReference, PortraitClipSettings settings)
    {
        if (decoded.Count < 2) throw new InvalidDataException("A loop needs at least two video frames so the duplicate closing frame can be dropped.");
        if (sourceReference.Length != width * height * 3)
            throw new ArgumentException("Source reference dimensions do not match the decoded clip.", nameof(sourceReference));
        var alignments = new List<FrameAlignment>();
        var reduced = new List<byte[]>();
        foreach (var index in SampleIndices(decoded.Count, sampleStride, maximumFrames))
        {
            var frame = decoded[index];
            if (frame.Width != width || frame.Height != height)
                throw new InvalidDataException($"Wan returned {frame.Width}x{frame.Height}; expected {width}x{height}.");
            var alignment = PortraitFrameAligner.EstimateTranslation(sourceReference, frame.Pixels, width, height,
                settings.MaximumAlignmentShift);
            alignments.Add(alignment);
            var aligned = PortraitFrameAligner.Align(sourceReference, frame.Pixels, width, height, alignment);
            reduced.Add(PortraitPackBuilder.BoxDownsampleRgb(aligned, width, height, crop));
        }
        var corrected = PortraitColorCorrector.TransferColour(reduced, referenceStill);
        return PortraitPixelStabilizer.Stabilize(corrected, referenceStill, frameDurationMs, alignments,
            settings.StaticPixelThreshold, settings.MotionPixelThreshold, settings.MinimumMotionComponentPixels);
    }

    public static FrameLoop BuildStillLoop(byte[] pixels, int durationMs = 100)
        => new FrameLoop([new RgbFrame(pixels.ToArray(), durationMs)]).Validate();

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
        => For(new PortraitClipSpec(PortraitClipPlan.PrimaryKey(state), state, null), outfitDescription);

    public static PortraitClipPrompt For(PortraitClipSpec spec, string? outfitDescription)
    {
        var motion = spec.Key switch
        {
            "resting-look-down" => "The subject rests quietly, looks down softly for a moment, then returns to the exact starting gaze.",
            "resting-glance-aside" => "The subject rests quietly, glances slightly toward the existing scene, then returns to the exact starting gaze.",
            "resting-private-smile" => "The subject rests quietly and gives one small private closed-mouth smile, then returns to the exact starting expression.",
            "resting-breathe-blink" => "The subject rests quietly with slow natural breathing and one natural blink, then returns to the exact starting gaze.",
            "speaking" => "The subject speaks naturally with readable conversational lip and jaw movement and a small, restrained head motion. No exaggerated expression.",
            "listening" => "The subject listens attentively with a small natural nod, steady expression, and closed mouth.",
            "thinking" => "The subject thinks quietly; the eyes drift slightly aside or upward, then return to the starting gaze.",
            "dreaming" => "The subject's eyes close naturally and remain closed while slow calm breathing continues.",
            _ => throw new ArgumentOutOfRangeException(nameof(spec), "Unknown portrait clip specification."),
        };
        var scene = ScenePhrase(outfitDescription);
        var prompt = $"Use the reference image as the exact identity, outfit, scene, pose, and framing. {scene}{motion} The action ends exactly in the starting pose. Static locked-off camera, steady framing, natural light, seamless closed loop.";
        var stateNegative = spec.State switch
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
    public static readonly PortraitClipSpec[] Specs =
    [
        new("resting-look-down", FaceState.Resting, "look down"),
        new("resting-glance-aside", FaceState.Resting, "glance aside"),
        new("resting-private-smile", FaceState.Resting, "private smile"),
        new("resting-breathe-blink", FaceState.Resting, "breath and blink"),
        new("speaking", FaceState.Speaking, null),
        new("listening", FaceState.Listening, null),
        new("thinking", FaceState.Thinking, null),
        new("dreaming", FaceState.Dreaming, null),
    ];

    public static string PrimaryKey(FaceState state) => state switch
    {
        FaceState.Resting => "resting-breathe-blink",
        FaceState.Working => "thinking",
        FaceState.Asleep => "dreaming",
        _ => state.ToString().ToLowerInvariant(),
    };

    public static async Task ExecuteAsync(Func<PortraitClipSpec, int, CancellationToken, Task<bool>> attempt,
        PortraitClipSettings settings, CancellationToken ct)
    {
        foreach (var spec in Specs)
        {
            for (var number = 1; number <= settings.MaximumAttemptsPerClip; number++)
            {
                if (await attempt(spec, number, ct)) break;
                if (number < settings.MaximumAttemptsPerClip && settings.RetryCooldown > TimeSpan.Zero)
                    await Task.Delay(settings.RetryCooldown, ct);
            }
        }
    }
}

public sealed record PortraitClipSpec(string Key, FaceState State, string? Variant);

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
