using System.Security.Cryptography;
using System.Text;

namespace Leaf.Plugins.Pixoo;

public static class PortraitColorCorrector
{
    public static IReadOnlyList<byte[]> Correct(
        IReadOnlyList<byte[]> frames,
        byte[] still,
        int fadeFrames = 4,
        double smoothing = .35)
    {
        if (frames.Count == 0) throw new ArgumentException("At least one clip frame is required.", nameof(frames));
        if (still.Length != RgbFrame.ByteCount || frames.Any(frame => frame.Length != still.Length))
            throw new ArgumentException("Colour correction requires 64x64 RGB frames.");
        if (fadeFrames < 1) throw new ArgumentOutOfRangeException(nameof(fadeFrames));
        var target = Statistics(still);
        LabStatistics? running = null;
        var corrected = new List<byte[]>(frames.Count);
        foreach (var frame in frames)
        {
            var measured = Statistics(frame);
            running = running is null ? measured : LabStatistics.Lerp(running.Value, measured, smoothing);
            corrected.Add(Transfer(frame, running.Value, target));
        }

        var fade = Math.Min(fadeFrames, corrected.Count);
        for (var index = 0; index < fade; index++)
        {
            var weight = 1d - index / (double)fade;
            corrected[index] = Blend(corrected[index], still, weight);
            var end = corrected.Count - 1 - index;
            corrected[end] = Blend(corrected[end], still, weight);
        }
        corrected[0] = still.ToArray();
        corrected[^1] = still.ToArray();
        return corrected;
    }

    internal static double MeanAbsoluteDifference(byte[] left, byte[] right)
        => left.Zip(right, (a, b) => Math.Abs(a - b)).Average();

    internal static double Mean(byte[] pixels) => pixels.Average(value => (double)value);

    private static byte[] Transfer(byte[] pixels, LabStatistics source, LabStatistics target)
    {
        var result = new byte[pixels.Length];
        for (var index = 0; index < pixels.Length; index += 3)
        {
            var lab = ToLab(pixels[index], pixels[index + 1], pixels[index + 2]);
            var adjusted = new Lab(
                target.Mean.L + (lab.L - source.Mean.L) * SafeRatio(target.Std.L, source.Std.L),
                target.Mean.A + (lab.A - source.Mean.A) * SafeRatio(target.Std.A, source.Std.A),
                target.Mean.B + (lab.B - source.Mean.B) * SafeRatio(target.Std.B, source.Std.B));
            var rgb = ToRgb(adjusted);
            result[index] = rgb.R;
            result[index + 1] = rgb.G;
            result[index + 2] = rgb.B;
        }
        return result;
    }

    private static double SafeRatio(double target, double source) => Math.Clamp(target / Math.Max(source, 1.5), .25, 4);

    private static byte[] Blend(byte[] frame, byte[] still, double stillWeight)
    {
        var result = new byte[frame.Length];
        for (var index = 0; index < result.Length; index++)
            result[index] = (byte)Math.Clamp(Math.Round(frame[index] * (1 - stillWeight) + still[index] * stillWeight), 0, 255);
        return result;
    }

    private static LabStatistics Statistics(byte[] pixels)
    {
        double l = 0, a = 0, b = 0, l2 = 0, a2 = 0, b2 = 0;
        var count = pixels.Length / 3;
        for (var index = 0; index < pixels.Length; index += 3)
        {
            var value = ToLab(pixels[index], pixels[index + 1], pixels[index + 2]);
            l += value.L; a += value.A; b += value.B;
            l2 += value.L * value.L; a2 += value.A * value.A; b2 += value.B * value.B;
        }
        var mean = new Lab(l / count, a / count, b / count);
        return new(mean, new(
            Math.Sqrt(Math.Max(0, l2 / count - mean.L * mean.L)),
            Math.Sqrt(Math.Max(0, a2 / count - mean.A * mean.A)),
            Math.Sqrt(Math.Max(0, b2 / count - mean.B * mean.B))));
    }

    private static Lab ToLab(byte red, byte green, byte blue)
    {
        static double Linear(byte value)
        {
            var channel = value / 255d;
            return channel <= .04045 ? channel / 12.92 : Math.Pow((channel + .055) / 1.055, 2.4);
        }
        var r = Linear(red); var g = Linear(green); var b = Linear(blue);
        var x = (r * .4124564 + g * .3575761 + b * .1804375) / .95047;
        var y = r * .2126729 + g * .7151522 + b * .072175;
        var z = (r * .0193339 + g * .119192 + b * .9503041) / 1.08883;
        static double Pivot(double value) => value > .008856 ? Math.Cbrt(value) : 7.787 * value + 16d / 116;
        var fx = Pivot(x); var fy = Pivot(y); var fz = Pivot(z);
        return new(116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    private static Rgb ToRgb(Lab lab)
    {
        var fy = (lab.L + 16) / 116;
        var fx = lab.A / 500 + fy;
        var fz = fy - lab.B / 200;
        static double Inverse(double value)
        {
            var cube = value * value * value;
            return cube > .008856 ? cube : (value - 16d / 116) / 7.787;
        }
        var x = .95047 * Inverse(fx);
        var y = Inverse(fy);
        var z = 1.08883 * Inverse(fz);
        // Inverse of the sRGB (D65) RGB->XYZ matrix used in ToLab, row by row.
        var r = x * 3.2404542 + y * -1.5371385 + z * -.4985314;
        var g = x * -.969266 + y * 1.8760108 + z * .041556;
        var b = x * .0556434 + y * -.2040259 + z * 1.0572252;
        static byte Gamma(double value)
        {
            var channel = value <= .0031308 ? 12.92 * value : 1.055 * Math.Pow(Math.Max(0, value), 1 / 2.4) - .055;
            return (byte)Math.Clamp(Math.Round(channel * 255), 0, 255);
        }
        return new(Gamma(r), Gamma(g), Gamma(b));
    }

    private readonly record struct Lab(double L, double A, double B);
    private readonly record struct Rgb(byte R, byte G, byte B);
    private readonly record struct LabStatistics(Lab Mean, Lab Std)
    {
        public static LabStatistics Lerp(LabStatistics previous, LabStatistics current, double amount)
            => new(Lerp(previous.Mean, current.Mean, amount), Lerp(previous.Std, current.Std, amount));
        private static Lab Lerp(Lab left, Lab right, double amount) => new(
            left.L + (right.L - left.L) * amount,
            left.A + (right.A - left.A) * amount,
            left.B + (right.B - left.B) * amount);
    }
}

public sealed record CompositeSettings
{
    public int FrameDurationMs { get; init; } = 100;
    public TimeSpan PauseMinimum { get; init; } = TimeSpan.FromSeconds(8);
    public TimeSpan PauseMaximum { get; init; } = TimeSpan.FromSeconds(25);
    public TimeSpan CompositeMinimum { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan CompositeMaximum { get; init; } = TimeSpan.FromSeconds(120);
    public int RestingCompositeCount { get; init; } = 3;
}

public static class PortraitCompositeBuilder
{
    public static IReadOnlyList<FrameLoop> BuildResting(
        byte[] still,
        IReadOnlyList<FrameLoop> idleClips,
        string sourceHash,
        CompositeSettings? settings = null)
    {
        settings ??= new();
        if (idleClips.Count == 0) return [Still(still, settings.FrameDurationMs)];
        return Enumerable.Range(0, settings.RestingCompositeCount)
            .Select(index => BuildOne(still, idleClips, Seed(sourceHash, index), settings))
            .ToArray();
    }

    public static FrameLoop WithPause(byte[] still, FrameLoop motion, TimeSpan pause, int frameDurationMs = 100)
    {
        var frames = Pause(still, pause, frameDurationMs)
            .Concat(Normalize(motion, frameDurationMs))
            .ToArray();
        return new FrameLoop(frames).Validate();
    }

    public static FrameLoop Repeat(FrameLoop motion, TimeSpan duration, int frameDurationMs = 100)
    {
        motion.Validate();
        var normalized = Normalize(motion, frameDurationMs).ToArray();
        var target = Math.Max(1, Frames(duration, frameDurationMs));
        return new FrameLoop(Enumerable.Range(0, target)
            .Select(index => new RgbFrame(normalized[index % normalized.Length].Pixels.ToArray(), frameDurationMs))
            .ToArray()).Validate();
    }

    private static FrameLoop BuildOne(byte[] still, IReadOnlyList<FrameLoop> clips, int seed, CompositeSettings settings)
    {
        var random = new Random(seed);
        var minimumFrames = Frames(settings.CompositeMinimum, settings.FrameDurationMs);
        var maximumFrames = Frames(settings.CompositeMaximum, settings.FrameDurationMs);
        var target = random.Next(minimumFrames, maximumFrames + 1);
        var frames = new List<RgbFrame>(target);
        var clipIndex = random.Next(clips.Count);
        while (frames.Count < target)
        {
            var pauseSeconds = random.NextDouble()
                * (settings.PauseMaximum - settings.PauseMinimum).TotalSeconds
                + settings.PauseMinimum.TotalSeconds;
            frames.AddRange(Pause(still, TimeSpan.FromSeconds(pauseSeconds), settings.FrameDurationMs));
            var clip = clips[clipIndex++ % clips.Count];
            frames.AddRange(Normalize(clip, settings.FrameDurationMs));
        }
        if (frames.Count > target) frames.RemoveRange(target, frames.Count - target);
        frames[0] = new(still, settings.FrameDurationMs);
        frames[^1] = new(still, settings.FrameDurationMs);
        return new FrameLoop(frames).Validate();
    }

    private static IEnumerable<RgbFrame> Pause(byte[] still, TimeSpan duration, int frameDurationMs)
        => Enumerable.Range(0, Math.Max(1, Frames(duration, frameDurationMs)))
            .Select(_ => new RgbFrame(still, frameDurationMs));

    private static IEnumerable<RgbFrame> Normalize(FrameLoop loop, int frameDurationMs)
        => loop.Frames.Select(frame => new RgbFrame(frame.Pixels.ToArray(), frameDurationMs));

    private static FrameLoop Still(byte[] pixels, int durationMs) => new([new(pixels.ToArray(), durationMs)]);
    private static int Frames(TimeSpan duration, int frameDurationMs) => (int)Math.Round(duration.TotalMilliseconds / frameDurationMs);
    private static int Seed(string sourceHash, int variant)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sourceHash + ":composite:" + variant));
        return BitConverter.ToInt32(bytes, 0) & int.MaxValue;
    }
}

public static class AmbientStateSelector
{
    public static FaceState Select(DateTimeOffset local, PixooPlaybackSettings settings)
    {
        var time = TimeOnly.FromDateTime(local.DateTime);
        if (Contains(time, settings.DreamingStart, settings.DreamingEnd)) return FaceState.Dreaming;
        if (Contains(time, settings.QuietStart, settings.QuietEnd)) return FaceState.Asleep;
        return FaceState.Resting;
    }

    internal static bool Contains(TimeOnly value, TimeOnly start, TimeOnly end)
        => start <= end ? value >= start && value < end : value >= start || value < end;
}
