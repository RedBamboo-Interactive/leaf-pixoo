using System.Text.Json.Nodes;
using Xunit.Abstractions;

namespace Leaf.Plugins.Pixoo.Tests;

/// <summary>
/// Opt-in diagnostic for private real-Agent fixtures. The files and generated maps remain outside
/// the repository. Set PIXOO_STABILIZATION_FIXTURES and REDLEAF_SCRATCH_DIR to run it.
/// </summary>
public sealed class RealStabilizationFixtureTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Real_wan_clips_keep_motion_inside_the_head_region()
    {
        var fixtureDirectory = Environment.GetEnvironmentVariable("PIXOO_STABILIZATION_FIXTURES");
        if (string.IsNullOrWhiteSpace(fixtureDirectory))
        {
            output.WriteLine("Skipped: PIXOO_STABILIZATION_FIXTURES is not set.");
            return;
        }
        var scratch = Environment.GetEnvironmentVariable("REDLEAF_SCRATCH_DIR");
        if (string.IsNullOrWhiteSpace(scratch))
            throw new InvalidOperationException("REDLEAF_SCRATCH_DIR must be set for private stabilization diagnostics.");
        var review = Path.Combine(scratch, "pixoo-stabilization-review");
        Directory.CreateDirectory(review);

        var still = PortraitPackBuilder.DecodePng(await File.ReadAllBytesAsync(Path.Combine(fixtureDirectory, "base.dat")));
        var currentLookDown = ReadLoop(await File.ReadAllBytesAsync(Path.Combine(fixtureDirectory, "resting-look-down.dat")));
        await WriteWorstMapAsync(review, "resting-look-down-before-changed-8x.png", currentLookDown, still);
        var currentGlance = ReadLoop(await File.ReadAllBytesAsync(Path.Combine(fixtureDirectory, "resting-glance-aside.dat")));
        await WriteWorstMapAsync(review, "resting-glance-aside-before-changed-8x.png", currentGlance, still);
        Report("resting-glance-aside-v5-before", currentGlance, still);

        var decoder = new FfmpegVideoFrameDecoder();
        var settings = new PortraitClipSettings();
        var lookDownDecoded = await decoder.DecodeAsync(
            await File.ReadAllBytesAsync(Path.Combine(fixtureDirectory, "resting-look-down.mp4")), 384, 384, CancellationToken.None);
        var lookDown = PortraitClipProcessor.ProcessMotionLoop(lookDownDecoded, settings, still, lookDownDecoded[0].Pixels);
        await WriteWorstMapAsync(review, "resting-look-down-after-changed-8x.png", lookDown.Loop, still);
        await WriteFrameStripAsync(review, "resting-look-down-after-frames-4x.png", lookDown.Loop);
        Report("resting-look-down", lookDown.Loop, still);

        var speakingDecoded = await decoder.DecodeAsync(
            await File.ReadAllBytesAsync(Path.Combine(fixtureDirectory, "speaking.mp4")), 384, 384, CancellationToken.None);
        var speakingBefore = PortraitClipProcessor.BuildMotionLoop(speakingDecoded, settings, still);
        await WriteWorstMapAsync(review, "speaking-before-changed-8x.png", speakingBefore, still);
        var speaking = PortraitClipProcessor.ProcessMotionLoop(speakingDecoded, settings, still, speakingDecoded[0].Pixels);
        await WriteWorstMapAsync(review, "speaking-after-changed-8x.png", speaking.Loop, still);
        await WriteFrameStripAsync(review, "speaking-after-frames-4x.png", speaking.Loop);
        Report("speaking", speaking.Loop, still);

        Assert.True(lookDown.IdenticalFractionMinimum >= .75,
            $"Look-down minimum identical fraction was {lookDown.IdenticalFractionMinimum:P1}.");
        Assert.All(lookDown.Loop.Frames, frame => Assert.True(OutsideHeadChangedFraction(frame.Pixels, still) <= .01));
        Assert.All(speaking.Loop.Frames, frame => Assert.True(OutsideHeadChangedFraction(frame.Pixels, still) <= .01));
    }

    private void Report(string name, FrameLoop loop, byte[] still)
    {
        var identical = loop.Frames.Select(frame => PortraitPixelStabilizer.IdenticalFraction(frame.Pixels, still)).ToArray();
        var outside = loop.Frames.Select(frame => OutsideHeadChangedFraction(frame.Pixels, still)).ToArray();
        output.WriteLine($"{name} identical: {string.Join(", ", identical.Select(value => value.ToString("P1")))}");
        output.WriteLine($"{name} outside-head changed: {string.Join(", ", outside.Select(value => value.ToString("P2")))}");
        output.WriteLine($"{name} min/median identical: {identical.Min():P1} / {Median(identical):P1}");
    }

    private static double OutsideHeadChangedFraction(byte[] frame, byte[] still)
    {
        // Deliberately generous: includes the complete head and hair but excludes torso and shelves.
        var head = new ImageRect(8, 0, 48, 43);
        var changed = 0;
        var outside = 0;
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
        {
            if (x >= head.X && x < head.Right && y >= head.Y && y < head.Bottom) continue;
            outside++;
            var offset = (y * 64 + x) * 3;
            if (frame[offset] != still[offset] || frame[offset + 1] != still[offset + 1]
                || frame[offset + 2] != still[offset + 2]) changed++;
        }
        return changed / (double)outside;
    }

    private static async Task WriteWorstMapAsync(string directory, string name, FrameLoop loop, byte[] still)
    {
        var worst = loop.Frames.OrderBy(frame => PortraitPixelStabilizer.IdenticalFraction(frame.Pixels, still)).First().Pixels;
        var map = new byte[512 * 512 * 3];
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
        {
            var source = (y * 64 + x) * 3;
            var changed = worst[source] != still[source] || worst[source + 1] != still[source + 1]
                || worst[source + 2] != still[source + 2];
            for (var py = y * 8; py < y * 8 + 8; py++)
            for (var px = x * 8; px < x * 8 + 8; px++)
            {
                var target = (py * 512 + px) * 3;
                map[target] = changed ? (byte)240 : (byte)12;
                map[target + 1] = changed ? (byte)68 : (byte)12;
                map[target + 2] = changed ? (byte)56 : (byte)12;
            }
        }
        await File.WriteAllBytesAsync(Path.Combine(directory, name), PortraitPackBuilder.EncodePng(map, 512, 512));
    }

    private static async Task WriteFrameStripAsync(string directory, string name, FrameLoop loop)
    {
        var selected = new[] { 0, loop.Frames.Count / 3, loop.Frames.Count * 2 / 3, loop.Frames.Count - 1 }
            .Select(index => Math.Clamp(index, 0, loop.Frames.Count - 1)).ToArray();
        var strip = new byte[1024 * 256 * 3];
        for (var panel = 0; panel < selected.Length; panel++)
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
        {
            var source = (y * 64 + x) * 3;
            for (var py = y * 4; py < y * 4 + 4; py++)
            for (var px = panel * 256 + x * 4; px < panel * 256 + x * 4 + 4; px++)
            {
                var target = (py * 1024 + px) * 3;
                loop.Frames[selected[panel]].Pixels.AsSpan(source, 3).CopyTo(strip.AsSpan(target, 3));
            }
        }
        await File.WriteAllBytesAsync(Path.Combine(directory, name), PortraitPackBuilder.EncodePng(strip, 1024, 256));
    }

    private static FrameLoop ReadLoop(byte[] json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("Fixture loop is malformed.");
        var frames = root["frames"] as JsonArray ?? throw new InvalidDataException("Fixture loop has no frames.");
        return new FrameLoop(frames.Select(item =>
        {
            var frame = item as JsonObject ?? throw new InvalidDataException("Fixture frame is malformed.");
            return new RgbFrame(Convert.FromBase64String(frame["pixels"]!.GetValue<string>()),
                frame["durationMs"]!.GetValue<int>());
        }).ToArray()).Validate();
    }

    private static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.OrderBy(value => value).ToArray();
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }
}
