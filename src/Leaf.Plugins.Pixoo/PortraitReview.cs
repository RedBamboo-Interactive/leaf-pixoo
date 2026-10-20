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
        if (pack.Landmarks is not null)
        {
            files.Add(new("regions-overlay-8x.png", RegionsOverlay(pack)));
            files.Add(new("blink-eye-crop-16x.png", BlinkEyeContactSheet(pack, 16)));
        }
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

    private static byte[] RegionsOverlay(PortraitPack pack)
    {
        const int scale = 8;
        using var source = Bitmap(pack.BasePixels);
        using var sheet = source.Resize(new SKSizeI(64 * scale, 64 * scale),
            new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None))
            ?? throw new InvalidDataException("Landmark overlay resize failed.");
        using var canvas = new SKCanvas(sheet);
        using var left = new SKPaint { Color = new SKColor(255, 214, 10), StrokeWidth = 2, IsAntialias = false, Style = SKPaintStyle.Stroke };
        using var right = new SKPaint { Color = new SKColor(10, 225, 245), StrokeWidth = 2, IsAntialias = false, Style = SKPaintStyle.Stroke };
        using var mouth = new SKPaint { Color = new SKColor(38, 235, 96), StrokeWidth = 2, IsAntialias = false, Style = SKPaintStyle.Stroke };
        DrawPolygon(canvas, pack.Landmarks!.LeftEyeOutline, left, scale);
        DrawPolygon(canvas, pack.Landmarks.RightEyeOutline, right, scale);
        DrawLine(canvas, pack.Landmarks.UpperLip, mouth, scale);
        DrawLine(canvas, pack.Landmarks.LowerLip, mouth, scale);
        DrawBox(canvas, pack.Regions.LeftEye, left, scale);
        DrawBox(canvas, pack.Regions.RightEye, right, scale);
        DrawBox(canvas, pack.Regions.Mouth, mouth, scale);
        canvas.Flush();
        return Encode(sheet);
    }

    private static byte[] BlinkEyeContactSheet(PortraitPack pack, int scale)
    {
        var open = pack.BasePixels.ToArray();
        var half = pack.BasePixels.ToArray();
        var closed = pack.BasePixels.ToArray();
        PortraitAnimation.ApplyBlink(half, pack.Regions, pack.Landmarks, .5);
        PortraitAnimation.ApplyBlink(closed, pack.Regions, pack.Landmarks);
        var eyes = new[] { pack.Regions.LeftEye, pack.Regions.RightEye };
        var tileWidth = eyes.Max(eye => eye.Width + 2) * scale;
        var tileHeight = eyes.Max(eye => eye.Height + 2) * scale;
        using var sheet = new SKBitmap(tileWidth * 10, tileHeight, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(sheet);
        canvas.Clear(SKColors.Black);
        var index = 0;
        foreach (var eye in eyes)
        foreach (var pixels in new[] { open, half, closed, half, open })
        {
            using var crop = Crop(pixels, eye, 1);
            using var enlarged = crop.Resize(new SKSizeI(crop.Width * scale, crop.Height * scale),
                new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None))
                ?? throw new InvalidDataException("Eye review resize failed.");
            canvas.DrawBitmap(enlarged, index++ * tileWidth, 0);
        }
        canvas.Flush();
        return Encode(sheet);
    }

    private static SKBitmap Crop(byte[] pixels, PixelRegion region, int padding)
    {
        var x0 = Math.Max(0, region.X - padding); var y0 = Math.Max(0, region.Y - padding);
        var x1 = Math.Min(64, region.Right + padding); var y1 = Math.Min(64, region.Bottom + padding);
        var crop = new SKBitmap(x1 - x0, y1 - y0, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (var y = y0; y < y1; y++)
        for (var x = x0; x < x1; x++)
        {
            var i = (y * 64 + x) * 3;
            crop.SetPixel(x - x0, y - y0, new SKColor(pixels[i], pixels[i + 1], pixels[i + 2]));
        }
        return crop;
    }

    private static void DrawPolygon(SKCanvas canvas, PixelPoint[] points, SKPaint paint, int scale)
    {
        if (points.Length < 2) return;
        using var path = new SKPath();
        path.MoveTo((points[0].X + .5f) * scale, (points[0].Y + .5f) * scale);
        foreach (var point in points.Skip(1)) path.LineTo((point.X + .5f) * scale, (point.Y + .5f) * scale);
        path.Close();
        canvas.DrawPath(path, paint);
    }

    private static void DrawLine(SKCanvas canvas, PixelPoint[] points, SKPaint paint, int scale)
    {
        if (points.Length < 2) return;
        using var path = new SKPath();
        path.MoveTo((points[0].X + .5f) * scale, (points[0].Y + .5f) * scale);
        foreach (var point in points.Skip(1)) path.LineTo((point.X + .5f) * scale, (point.Y + .5f) * scale);
        canvas.DrawPath(path, paint);
    }

    private static void DrawBox(SKCanvas canvas, PixelRegion region, SKPaint paint, int scale)
        => canvas.DrawRect(region.X * scale + 1, region.Y * scale + 1,
            region.Width * scale - 2, region.Height * scale - 2, paint);

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
