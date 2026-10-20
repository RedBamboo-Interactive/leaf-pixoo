using SkiaSharp;

namespace Leaf.Plugins.Pixoo.Tests;

public sealed class PortraitPipelineTests
{
    [Fact]
    public void Grid_detector_finds_regular_pixel_pitch()
    {
        using var bitmap = new SKBitmap(128, 128, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var cellX = x / 8;
            var cellY = y / 8;
            bitmap.SetPixel(x, y, new SKColor(
                (byte)((cellX * 47 + cellY * 11) % 256),
                (byte)((cellX * 19 + cellY * 53) % 256),
                (byte)((cellX * 71 + cellY * 29) % 256)));
        }

        Assert.Equal(8, PixelGridDetector.Detect(bitmap));
    }

    [Fact]
    public void Quantization_is_deterministic_and_bounded_without_dithering()
    {
        var pixels = new byte[RgbFrame.ByteCount];
        for (var i = 0; i < pixels.Length; i += 3)
        {
            pixels[i] = (byte)((i * 17) % 256);
            pixels[i + 1] = (byte)((i * 31) % 256);
            pixels[i + 2] = (byte)((i * 43) % 256);
        }

        var first = PaletteQuantizer.Quantize(pixels, 40);
        var second = PaletteQuantizer.Quantize(pixels, 40);

        Assert.Equal(first, second);
        Assert.InRange(PaletteQuantizer.CountColors(first), 2, 40);
    }

    [Fact]
    public void Regional_colour_transfer_survives_bounded_quantization()
    {
        var regions = Regions();
        var mask = FigureMaskBuilder.Build(regions);
        var reference = Pattern();
        var target = Pattern();
        PaintZone(reference, regions, mask, "Hair", 193, 151, 67);
        PaintZone(target, regions, mask, "Hair", 91, 57, 37);
        PaintZone(reference, regions, mask, "InnerTop", 178, 105, 38);
        PaintZone(target, regions, mask, "InnerTop", 205, 205, 200);

        var matched = PortraitColorMatcher.Match(target, regions, mask, reference, regions, mask);
        matched = PaletteQuantizer.Quantize(matched, PortraitPackBuilder.PaletteLimit);
        matched = PortraitColorMatcher.Match(matched, regions, mask, reference, regions, mask);
        matched = PaletteQuantizer.Quantize(matched, PortraitPackBuilder.PaletteLimit);
        var metrics = PortraitColorMatcher.Measure(matched, regions, mask, reference, regions, mask);

        Assert.True(metrics.HairHueDelta <= 12, $"hair hue delta was {metrics.HairHueDelta}");
        Assert.True(metrics.HairLightnessDelta <= 8, $"hair lightness delta was {metrics.HairLightnessDelta}");
        Assert.True(metrics.GarmentHueDelta <= 15, $"garment hue delta was {metrics.GarmentHueDelta}");
        Assert.True(metrics.GarmentLightnessDelta <= 10, $"garment lightness delta was {metrics.GarmentLightnessDelta}");
        Assert.InRange(PaletteQuantizer.CountColors(matched), 2, PortraitPackBuilder.PaletteLimit);
    }

    [Fact]
    public void Blink_and_speech_only_composite_inside_their_regions()
    {
        var regions = Regions();
        var original = Pattern();
        var blink = original.ToArray();
        PortraitAnimation.ApplyBlink(blink, regions);
        AssertOnlyRegionsChanged(original, blink, regions.LeftEye, regions.RightEye);

        var speak = original.ToArray();
        PortraitAnimation.ApplyMouthShape(speak, regions, 2);
        AssertOnlyRegionsChanged(original, speak, regions.Mouth);
    }

    [Fact]
    public void Source_hash_change_invalidates_the_pack_cache_key()
    {
        var original = PortraitSourceFingerprint.Hash([1, 2, 3]);
        var same = PortraitSourceFingerprint.Hash([1, 2, 3]);
        var changed = PortraitSourceFingerprint.Hash([1, 2, 4]);

        Assert.False(PortraitSourceFingerprint.RequiresRebuild(original, same));
        Assert.True(PortraitSourceFingerprint.RequiresRebuild(original, changed));
        Assert.True(PortraitSourceFingerprint.RequiresRebuild(null, original));
    }

    [Fact]
    public void Every_state_preserves_the_figure_and_has_no_axis_aligned_compositing_seam()
    {
        var regions = Regions();
        var basePixels = Pattern();
        var mask = FigureMaskBuilder.Build(regions);
        var metrics = new PortraitColorMatchMetrics(45, 45, 60, 60, 20, 32, 32, 52, 52, 20, 220, 220, 40, 40, 20);
        var pack = new PortraitPack(basePixels, basePixels, mask, regions,
            new PortraitProvenance("source", "hash", PortraitPackBuilder.PipelineVersion, "test", 1, DateTimeOffset.UnixEpoch, null),
            PaletteQuantizer.CountColors(basePixels), 100, metrics);

        foreach (var state in Enum.GetValues<FaceState>())
        foreach (var frame in PortraitAnimation.Render(pack, state, "#9a7cff", 100).Frames)
        {
            var factor = UniformFactor(basePixels, frame.Pixels, mask, regions);
            for (var y = 0; y < 64; y++)
            for (var x = 0; x < 64; x++)
            {
                if (mask[y * 64 + x] != 255 || regions.LeftEye.Contains(x, y)
                    || regions.RightEye.Contains(x, y) || regions.Mouth.Contains(x, y)) continue;
                var i = (y * 64 + x) * 3;
                for (var channel = 0; channel < 3; channel++)
                    Assert.InRange(Math.Abs(frame.Pixels[i + channel] - Math.Round(basePixels[i + channel] * factor)), 0, 2);
            }
            Assert.True(LongestAxisStep(basePixels, frame.Pixels, factor) < 12,
                $"{state} contains an axis-aligned compositing discontinuity");
        }
    }

    [Fact]
    public void Speaking_shapes_have_one_three_and_five_pixel_openings()
    {
        var regions = Regions() with { Mouth = new PixelRegion(24, 30, 16, 8) };
        var pixels = Pattern();
        var pack = new PortraitPack(pixels, pixels, FigureMaskBuilder.Build(regions), regions,
            new PortraitProvenance("source", "hash", PortraitPackBuilder.PipelineVersion, "test", 1, DateTimeOffset.UnixEpoch, null),
            PaletteQuantizer.CountColors(pixels), 100,
            new PortraitColorMatchMetrics(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
        var frames = PortraitAnimation.Render(pack, FaceState.Speaking, "#9a7cff", 100).Frames;
        var changedRows = frames.Select(frame => Enumerable.Range(regions.Mouth.Y, regions.Mouth.Height)
            .Count(y => Enumerable.Range(regions.Mouth.X, regions.Mouth.Width).Any(x => PixelChanged(pixels, frame.Pixels, x, y)))).ToArray();

        Assert.Equal([1, 3, 5], changedRows);
        Assert.True(changedRows[1] - changedRows[0] >= 2);
        Assert.True(changedRows[2] - changedRows[1] >= 2);
    }

    private static PortraitRegions Regions() => new(
        new(16, 8, 32, 32), new(20, 18, 10, 6), new(34, 18, 10, 6),
        new(25, 31, 14, 7), new(12, 38, 40, 26), new(10, 4, 44, 60));

    private static byte[] Pattern()
    {
        var pixels = new byte[RgbFrame.ByteCount];
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
        {
            var i = (y * 64 + x) * 3;
            pixels[i] = (byte)(40 + x * 2);
            pixels[i + 1] = (byte)(30 + y * 2);
            pixels[i + 2] = (byte)(20 + (x + y));
        }
        return pixels;
    }

    private static void AssertOnlyRegionsChanged(byte[] before, byte[] after, params PixelRegion[] allowed)
    {
        Assert.NotEqual(before, after);
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
        {
            if (allowed.Any(region => region.Contains(x, y))) continue;
            var i = (y * 64 + x) * 3;
            Assert.Equal(before[i], after[i]);
            Assert.Equal(before[i + 1], after[i + 1]);
            Assert.Equal(before[i + 2], after[i + 2]);
        }
    }

    private static double UniformFactor(byte[] before, byte[] after, byte[] mask, PortraitRegions regions)
    {
        var ratios = new List<double>();
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
        {
            if (mask[y * 64 + x] != 255 || regions.LeftEye.Contains(x, y)
                || regions.RightEye.Contains(x, y) || regions.Mouth.Contains(x, y)) continue;
            var i = (y * 64 + x) * 3;
            for (var channel = 0; channel < 3; channel++)
                if (before[i + channel] is > 20 and < 220) ratios.Add(after[i + channel] / (double)before[i + channel]);
        }
        ratios.Sort();
        return ratios.Count == 0 ? 1 : ratios[ratios.Count / 2];
    }

    private static int LongestAxisStep(byte[] before, byte[] after, double factor)
    {
        double Delta(int x, int y)
        {
            var i = (y * 64 + x) * 3;
            return Enumerable.Range(0, 3).Average(channel => Math.Abs(after[i + channel] - Math.Round(before[i + channel] * factor)));
        }
        var longest = 0;
        for (var x = 1; x < 64; x++)
        {
            var run = 0;
            for (var y = 0; y < 64; y++) { run = Math.Abs(Delta(x, y) - Delta(x - 1, y)) > 14 ? run + 1 : 0; longest = Math.Max(longest, run); }
        }
        for (var y = 1; y < 64; y++)
        {
            var run = 0;
            for (var x = 0; x < 64; x++) { run = Math.Abs(Delta(x, y) - Delta(x, y - 1)) > 14 ? run + 1 : 0; longest = Math.Max(longest, run); }
        }
        return longest;
    }

    private static bool PixelChanged(byte[] before, byte[] after, int x, int y)
    {
        var i = (y * 64 + x) * 3;
        return before[i] != after[i] || before[i + 1] != after[i + 1] || before[i + 2] != after[i + 2];
    }

    private static void PaintZone(byte[] pixels, PortraitRegions regions, byte[] mask, string zone, byte r, byte g, byte b)
    {
        foreach (var pixel in PortraitColorMatcher.RegionIndices(pixels, regions, mask, zone))
        {
            var i = pixel * 3;
            pixels[i] = r; pixels[i + 1] = g; pixels[i + 2] = b;
        }
    }
}
