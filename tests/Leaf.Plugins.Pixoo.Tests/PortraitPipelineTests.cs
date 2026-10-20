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
}
