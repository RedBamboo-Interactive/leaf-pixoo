using SkiaSharp;

namespace Leaf.Plugins.Pixoo;

public sealed record PortraitReviewArtifact(string FileName, byte[] Bytes);

public static class PortraitReview
{
    public static IReadOnlyList<PortraitReviewArtifact> Create(PortraitPack pack, byte[] sourceCropPng, string accent)
    {
        var files = new List<PortraitReviewArtifact>
        {
            new("base-64.png", PortraitPackBuilder.EncodePng(pack.BasePixels)),
            new("fallback-64.png", PortraitPackBuilder.EncodePng(pack.FallbackPixels)),
            new("figure-mask-64.png", PortraitPackBuilder.EncodeMaskPng(pack.FigureMask)),
            new("colour-regions-64.png", PortraitPackBuilder.EncodePng(PortraitColorMatcher.VisualizeRegions(pack.BasePixels, pack.Regions, pack.FigureMask))),
            new("source-crop-and-base-8x.png", Comparison(sourceCropPng, pack.BasePixels)),
        };
        foreach (var state in Enum.GetValues<FaceState>())
        {
            var loop = PortraitAnimation.Render(pack, state, accent, 100);
            files.Add(new($"{Slug(state)}-native.png", ContactSheet(loop, 1)));
            files.Add(new($"{Slug(state)}-8x.png", ContactSheet(loop, 8)));
        }
        files.Add(new("speaking-mouth-crop-16x.png", MouthContactSheet(
            PortraitAnimation.Render(pack, FaceState.Speaking, accent, 100), pack.Regions.Mouth, 16)));
        foreach (var gesture in Enum.GetValues<FaceGesture>())
        {
            var loop = PortraitAnimation.Render(pack, FaceState.Resting, accent, 100, gesture);
            var name = gesture.ToString().ToLowerInvariant();
            files.Add(new($"gesture-{name}-native.png", ContactSheet(loop, 1)));
            files.Add(new($"gesture-{name}-8x.png", ContactSheet(loop, 8)));
        }
        return files;
    }

    public static byte[] ContactSheet(FrameLoop loop, int scale)
    {
        if (scale is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(scale));
        var tile = 64 * scale;
        using var sheet = new SKBitmap(tile * loop.Frames.Count, tile, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(sheet);
        canvas.Clear(SKColors.Black);
        for (var index = 0; index < loop.Frames.Count; index++)
        {
            using var frame = Bitmap(loop.Frames[index].Pixels);
            using var scaled = scale == 1 ? frame.Copy() : frame.Resize(new SKSizeI(tile, tile),
                new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None))
                ?? throw new InvalidDataException("Review frame resize failed.");
            canvas.DrawBitmap(scaled, index * tile, 0);
        }
        canvas.Flush();
        return Encode(sheet);
    }

    public static byte[] MouthContactSheet(FrameLoop loop, PixelRegion mouth, int scale)
    {
        var padding = 1;
        var x0 = Math.Max(0, mouth.X - padding); var y0 = Math.Max(0, mouth.Y - padding);
        var x1 = Math.Min(64, mouth.Right + padding); var y1 = Math.Min(64, mouth.Bottom + padding);
        var width = x1 - x0; var height = y1 - y0;
        using var sheet = new SKBitmap(width * scale * loop.Frames.Count, height * scale, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(sheet);
        canvas.Clear(SKColors.Black);
        for (var frameIndex = 0; frameIndex < loop.Frames.Count; frameIndex++)
        {
            using var crop = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var source = ((y0 + y) * 64 + x0 + x) * 3;
                var pixels = loop.Frames[frameIndex].Pixels;
                crop.SetPixel(x, y, new SKColor(pixels[source], pixels[source + 1], pixels[source + 2]));
            }
            using var enlarged = crop.Resize(new SKSizeI(width * scale, height * scale),
                new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None))
                ?? throw new InvalidDataException("Mouth review resize failed.");
            canvas.DrawBitmap(enlarged, frameIndex * width * scale, 0);
        }
        canvas.Flush();
        return Encode(sheet);
    }

    private static byte[] Comparison(byte[] cropPng, byte[] basePixels)
    {
        using var crop = SKBitmap.Decode(cropPng) ?? throw new InvalidDataException("Review crop could not be decoded.");
        using var portrait = Bitmap(basePixels);
        using var sheet = new SKBitmap(1024, 512, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(sheet);
        canvas.Clear(new SKColor(18, 18, 22));
        using var cropScaled = crop.Resize(new SKSizeI(512, 512), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
            ?? throw new InvalidDataException("Review crop resize failed.");
        using var portraitScaled = portrait.Resize(new SKSizeI(512, 512), new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None))
            ?? throw new InvalidDataException("Review portrait resize failed.");
        canvas.DrawBitmap(cropScaled, 0, 0);
        canvas.DrawBitmap(portraitScaled, 512, 0);
        canvas.Flush();
        return Encode(sheet);
    }

    private static SKBitmap Bitmap(byte[] rgb)
    {
        if (rgb.Length != RgbFrame.ByteCount) throw new InvalidDataException("Review frame must be 64x64 RGB.");
        var bitmap = new SKBitmap(64, 64, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
        {
            var i = (y * 64 + x) * 3;
            bitmap.SetPixel(x, y, new SKColor(rgb[i], rgb[i + 1], rgb[i + 2]));
        }
        return bitmap;
    }

    private static byte[] Encode(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidDataException("Review PNG encoding failed.");
        return data.ToArray();
    }

    private static string Slug(FaceState state) => state.ToString().ToLowerInvariant();
}
