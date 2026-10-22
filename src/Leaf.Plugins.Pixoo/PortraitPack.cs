using System.Security.Cryptography;
using SkiaSharp;

namespace Leaf.Plugins.Pixoo;

public readonly record struct ImageRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

public sealed record VideoPlateGeometry(
    ImageRect SourceSquare,
    ImageRect SourcePlate,
    ImageRect OutputSquare,
    int OutputWidth = 768,
    int OutputHeight = 576);

public sealed record PortraitProvenance(
    string SourceAssetId,
    string SourceHash,
    string PipelineVersion,
    DateTimeOffset GeneratedAt);

public sealed record PortraitClipProvenance(
    string Key,
    FaceState State,
    string Workflow,
    long Seed,
    string JobId,
    string VideoAssetId,
    string LoopAssetId,
    string Prompt,
    string Negative,
    string? Note,
    double IdenticalFractionMinimum,
    double IdenticalFractionMedian,
    DateTimeOffset GeneratedAt);

public sealed record PortraitClip(FrameLoop Loop, PortraitClipProvenance Provenance);

public sealed record PortraitPack(
    byte[] BasePixels,
    PortraitProvenance Provenance,
    IReadOnlyDictionary<string, PortraitClip> Clips)
{
    public PortraitPack WithClip(PortraitClip clip)
    {
        var next = Clips.ToDictionary(item => item.Key, item => item.Value);
        next[clip.Provenance.Key] = clip;
        return this with { Clips = next };
    }

    public PortraitClip? Primary(FaceState state)
        => Clips.GetValueOrDefault(PortraitClipPlan.PrimaryKey(state));

    public IReadOnlyList<PortraitClip> IdleVariants()
        => PortraitClipPlan.Specs.Where(spec => spec.State == FaceState.Resting)
            .Select(spec => Clips.GetValueOrDefault(spec.Key))
            .OfType<PortraitClip>()
            .ToArray();
}

public sealed record PreparedPortrait(
    byte[] CropPng,
    byte[] BasePixels,
    ImageRect SourceSquare,
    int SourceWidth,
    int SourceHeight,
    string SourceHash);

public sealed record SlowFallbackPlate(byte[] Png, VideoPlateGeometry Geometry);

public static class PortraitSourceFingerprint
{
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static bool RequiresRebuild(string? currentHash, string sourceHash)
        => !string.Equals(currentHash, sourceHash, StringComparison.OrdinalIgnoreCase);
}

public sealed class PortraitPackBuilder
{
    public const string PipelineVersion = "portrait-gif-v6-lightx2v-similarity-stabilized";

    public PreparedPortrait Prepare(byte[] sourceBytes)
    {
        using var source = SKBitmap.Decode(sourceBytes) ?? throw new InvalidDataException("Source portrait could not be decoded.");
        var face = FindFaceWindow(source);
        var side = (int)Math.Round(Math.Max(face.Width, face.Height) * 2.12);
        side = Math.Clamp(side, Math.Min(source.Width, source.Height) / 2, Math.Min(source.Width, source.Height));
        var centerX = face.Left + face.Width / 2;
        var x = Math.Clamp(centerX - side / 2, 0, source.Width - side);
        var y = Math.Clamp((int)Math.Round(face.Top - face.Height * .30), 0, source.Height - side);
        var square = new ImageRect(x, y, side, side);
        using var crop = Crop(source, square);
        var cropPng = EncodePng(crop);
        return new(cropPng, BoxDownsample(crop, 64, 64), square, source.Width, source.Height,
            PortraitSourceFingerprint.Hash(sourceBytes));
    }

    public SlowFallbackPlate BuildSlowFallbackPlate(byte[] sourceBytes, PreparedPortrait prepared)
    {
        using var source = SKBitmap.Decode(sourceBytes) ?? throw new InvalidDataException("Source portrait could not be decoded.");
        if (source.Width != prepared.SourceWidth || source.Height != prepared.SourceHeight)
            throw new InvalidDataException("Source dimensions changed while preparing the slow fallback plate.");
        if ((int)Math.Round(prepared.SourceSquare.Height * 4d / 3d) <= source.Width)
        {
            var geometry = BuildPlateGeometry(prepared.SourceSquare, source.Width, source.Height);
            using var plate = CropAndResize(source, geometry.SourcePlate, geometry.OutputWidth, geometry.OutputHeight);
            return new(EncodePng(plate), geometry);
        }

        // The fast path has no landscape constraint. If its square cannot be widened inside a narrow source,
        // letterbox only the exceptional slow fallback; the mapped square is cropped back out after decoding.
        using var crop = Crop(source, prepared.SourceSquare);
        using var letterboxed = new SKBitmap(768, 576, SKColorType.Rgba8888, SKAlphaType.Opaque);
        letterboxed.Erase(SKColors.Black);
        using (var canvas = new SKCanvas(letterboxed))
        {
            canvas.DrawBitmap(crop, new SKRect(96, 0, 672, 576));
            canvas.Flush();
        }
        var fallbackGeometry = new VideoPlateGeometry(prepared.SourceSquare, prepared.SourceSquare,
            new ImageRect(96, 0, 576, 576));
        return new(EncodePng(letterboxed), fallbackGeometry);
    }

    public PortraitPack BuildBase(PreparedPortrait prepared, string sourceAssetId, DateTimeOffset generatedAt)
    {
        if (prepared.BasePixels.Length != RgbFrame.ByteCount)
            throw new InvalidDataException("Faithful portrait reduction is not exactly 64x64 RGB.");
        return new(prepared.BasePixels,
            new(sourceAssetId, prepared.SourceHash, PipelineVersion, generatedAt),
            new Dictionary<string, PortraitClip>());
    }

    internal static VideoPlateGeometry BuildPlateGeometry(ImageRect square, int sourceWidth, int sourceHeight)
    {
        if (square.Width != square.Height || square.Width <= 0 || square.X < 0 || square.Y < 0
            || square.Right > sourceWidth || square.Bottom > sourceHeight)
            throw new ArgumentOutOfRangeException(nameof(square), "The bust crop must be a square inside the source image.");
        var plateWidth = (int)Math.Round(square.Height * 4d / 3d);
        if (plateWidth > sourceWidth)
            throw new InvalidDataException("The square bust crop cannot be widened to 4:3 inside the source image.");
        var centerX = square.X + square.Width / 2d;
        var plateX = Math.Clamp((int)Math.Round(centerX - plateWidth / 2d), 0, sourceWidth - plateWidth);
        var sourcePlate = new ImageRect(plateX, square.Y, plateWidth, square.Height);
        const int outputWidth = 768;
        const int outputHeight = 576;
        var mapped = new ImageRect(
            (int)Math.Round((square.X - sourcePlate.X) * outputWidth / (double)sourcePlate.Width),
            (int)Math.Round((square.Y - sourcePlate.Y) * outputHeight / (double)sourcePlate.Height),
            (int)Math.Round(square.Width * outputWidth / (double)sourcePlate.Width),
            (int)Math.Round(square.Height * outputHeight / (double)sourcePlate.Height));
        mapped = new ImageRect(
            Math.Clamp(mapped.X, 0, outputWidth - 1),
            Math.Clamp(mapped.Y, 0, outputHeight - 1),
            Math.Clamp(mapped.Width, 1, outputWidth - Math.Clamp(mapped.X, 0, outputWidth - 1)),
            Math.Clamp(mapped.Height, 1, outputHeight - Math.Clamp(mapped.Y, 0, outputHeight - 1)));
        return new(square, sourcePlate, mapped, outputWidth, outputHeight);
    }

    internal static byte[] BoxReduce(byte[] encodedImage)
    {
        using var source = SKBitmap.Decode(encodedImage) ?? throw new InvalidDataException("Portrait could not be decoded.");
        return BoxDownsample(source, 64, 64);
    }

    internal static byte[] DecodeAndBoxResize(byte[] encodedImage, int width, int height)
    {
        using var source = SKBitmap.Decode(encodedImage) ?? throw new InvalidDataException("Portrait could not be decoded.");
        return BoxDownsample(source, width, height);
    }

    internal static byte[] BoxDownsampleRgb(byte[] pixels, int width, int height, ImageRect crop,
        int outputWidth = 64, int outputHeight = 64)
    {
        if (pixels.Length != width * height * 3) throw new ArgumentException("RGB byte count does not match dimensions.", nameof(pixels));
        if (crop.X < 0 || crop.Y < 0 || crop.Right > width || crop.Bottom > height)
            throw new ArgumentOutOfRangeException(nameof(crop));
        var output = new byte[outputWidth * outputHeight * 3];
        for (var dy = 0; dy < outputHeight; dy++)
        for (var dx = 0; dx < outputWidth; dx++)
        {
            var x0 = crop.X + dx * crop.Width / outputWidth;
            var x1 = crop.X + Math.Max(dx * crop.Width / outputWidth + 1, (dx + 1) * crop.Width / outputWidth);
            var y0 = crop.Y + dy * crop.Height / outputHeight;
            var y1 = crop.Y + Math.Max(dy * crop.Height / outputHeight + 1, (dy + 1) * crop.Height / outputHeight);
            long red = 0, green = 0, blue = 0, count = 0;
            for (var sy = y0; sy < y1; sy++)
            for (var sx = x0; sx < x1; sx++)
            {
                var index = (sy * width + sx) * 3;
                red += pixels[index]; green += pixels[index + 1]; blue += pixels[index + 2]; count++;
            }
            var target = (dy * outputWidth + dx) * 3;
            output[target] = (byte)((red + count / 2) / count);
            output[target + 1] = (byte)((green + count / 2) / count);
            output[target + 2] = (byte)((blue + count / 2) / count);
        }
        return output;
    }

    public static byte[] EncodePng(byte[] rgb, int width = 64, int height = 64)
    {
        if (rgb.Length != width * height * 3) throw new ArgumentException("RGB byte count does not match dimensions.", nameof(rgb));
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var index = (y * width + x) * 3;
            bitmap.SetPixel(x, y, new SKColor(rgb[index], rgb[index + 1], rgb[index + 2]));
        }
        return EncodePng(bitmap);
    }

    public static byte[] DecodePng(byte[] png)
    {
        using var image = SKBitmap.Decode(png) ?? throw new InvalidDataException("Portrait asset could not be decoded.");
        if (image.Width != 64 || image.Height != 64) throw new InvalidDataException("Portrait asset must be exactly 64x64.");
        return ToRgb(image);
    }

    private static byte[] ToRgb(SKBitmap image)
    {
        var result = new byte[image.Width * image.Height * 3];
        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
        {
            var pixel = image.GetPixel(x, y);
            var index = (y * image.Width + x) * 3;
            var alpha = pixel.Alpha / 255d;
            result[index] = (byte)Math.Round(pixel.Red * alpha);
            result[index + 1] = (byte)Math.Round(pixel.Green * alpha);
            result[index + 2] = (byte)Math.Round(pixel.Blue * alpha);
        }
        return result;
    }

    private static byte[] EncodePng(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidDataException("Portrait PNG encoding failed.");
        return data.ToArray();
    }

    private static SKRectI FindFaceWindow(SKBitmap source)
    {
        const int scanSize = 128;
        using var scan = source.Resize(new SKSizeI(scanSize, scanSize), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None))
            ?? throw new InvalidDataException("Source portrait resize failed.");
        var skin = new bool[scanSize * scanSize];
        for (var y = 0; y < scanSize; y++)
        for (var x = 0; x < scanSize; x++) skin[y * scanSize + x] = IsSkin(scan.GetPixel(x, y));
        var integral = new int[(scanSize + 1) * (scanSize + 1)];
        for (var y = 0; y < scanSize; y++)
        for (var x = 0; x < scanSize; x++)
            integral[(y + 1) * (scanSize + 1) + x + 1] = (skin[y * scanSize + x] ? 1 : 0)
                + integral[y * (scanSize + 1) + x + 1] + integral[(y + 1) * (scanSize + 1) + x] - integral[y * (scanSize + 1) + x];

        var best = (Score: 0d, X: 36, Y: 12, W: 56, H: 62);
        for (var width = 32; width <= 56; width += 4)
        {
            var height = (int)Math.Round(width * 1.14);
            for (var y = 4; y + height < 82; y += 3)
            for (var x = 12; x + width < 116; x += 3)
            {
                var count = Sum(integral, scanSize + 1, x, y, width, height);
                var density = count / (double)(width * height);
                var centerPenalty = Math.Abs(x + width / 2d - 64) / 128d;
                var verticalPenalty = y / 420d;
                var sizePreference = width / (double)scanSize * .35;
                var score = density + sizePreference - centerPenalty * .18 - verticalPenalty;
                if (score > best.Score) best = (score, x, y, width, height);
            }
        }
        if (best.Score < .12) best = (.12, 36, 12, 56, 62);
        var scaleX = source.Width / (double)scanSize;
        var scaleY = source.Height / (double)scanSize;
        var resultX = (int)Math.Round(best.X * scaleX);
        var resultY = (int)Math.Round(best.Y * scaleY);
        var resultWidth = (int)Math.Round(best.W * scaleX);
        var resultHeight = (int)Math.Round(best.H * scaleY);
        return new SKRectI(resultX, resultY, resultX + resultWidth, resultY + resultHeight);
    }

    private static bool IsSkin(SKColor pixel)
    {
        var red = pixel.Red; var green = pixel.Green; var blue = pixel.Blue;
        var cb = 128 - .168736 * red - .331264 * green + .5 * blue;
        var cr = 128 + .5 * red - .418688 * green - .081312 * blue;
        return red > 55 && Math.Max(red, Math.Max(green, blue)) - Math.Min(red, Math.Min(green, blue)) > 10
            && cb is >= 72 and <= 138 && cr is >= 128 and <= 182;
    }

    private static int Sum(int[] integral, int stride, int x, int y, int width, int height)
        => integral[(y + height) * stride + x + width] - integral[y * stride + x + width]
           - integral[(y + height) * stride + x] + integral[y * stride + x];

    private static SKBitmap Crop(SKBitmap source, ImageRect rect)
    {
        var output = new SKBitmap(rect.Width, rect.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(output);
        canvas.DrawBitmap(source, new SKRectI(rect.X, rect.Y, rect.Right, rect.Bottom), new SKRect(0, 0, rect.Width, rect.Height));
        canvas.Flush();
        return output;
    }

    private static SKBitmap CropAndResize(SKBitmap source, ImageRect rect, int width, int height)
    {
        var output = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(output);
        canvas.DrawBitmap(source, new SKRect(rect.X, rect.Y, rect.Right, rect.Bottom), new SKRect(0, 0, width, height));
        canvas.Flush();
        return output;
    }

    private static byte[] BoxDownsample(SKBitmap source, int width, int height)
        => BoxDownsampleRgb(ToRgb(source), source.Width, source.Height, new(0, 0, source.Width, source.Height), width, height);
}
