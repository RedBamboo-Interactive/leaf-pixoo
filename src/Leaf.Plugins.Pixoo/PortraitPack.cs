using System.Security.Cryptography;
using SkiaSharp;

namespace Leaf.Plugins.Pixoo;

public readonly record struct PixelRegion(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;
    public PixelRegion Clamp() => new(
        Math.Clamp(X, 0, 63), Math.Clamp(Y, 0, 63),
        Math.Clamp(Width, 1, 64 - Math.Clamp(X, 0, 63)),
        Math.Clamp(Height, 1, 64 - Math.Clamp(Y, 0, 63)));
}

public sealed record PortraitRegions(
    PixelRegion Face,
    PixelRegion LeftEye,
    PixelRegion RightEye,
    PixelRegion Mouth,
    PixelRegion Torso,
    PixelRegion Subject);

public sealed record PortraitProvenance(
    string SourceAssetId,
    string SourceHash,
    string PipelineVersion,
    string Workflow,
    long Seed,
    DateTimeOffset GeneratedAt,
    int? DetectedGridPitch);

public sealed record PortraitPack(
    byte[] BasePixels,
    byte[] FallbackPixels,
    byte[] FigureMask,
    PortraitRegions Regions,
    PortraitProvenance Provenance,
    int PaletteSize,
    double FaceContrast,
    PortraitColorMatchMetrics ColorMatch);

public sealed record PreparedPortrait(
    byte[] CropPng,
    byte[] FallbackPixels,
    byte[] FigureMask,
    PortraitRegions Regions,
    string SourceHash,
    PortraitColorHints ColorHints);

public sealed record PortraitValidation(bool Valid, IReadOnlyList<string> Errors, int PaletteSize, double FaceContrast);

public static class PortraitSourceFingerprint
{
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static bool RequiresRebuild(string? currentHash, string sourceHash)
        => !string.Equals(currentHash, sourceHash, StringComparison.OrdinalIgnoreCase);
}

public sealed class PortraitPackBuilder
{
    public const int PaletteLimit = 48;
    public const string PipelineVersion = "portrait-v2";

    public PreparedPortrait Prepare(byte[] sourceBytes)
    {
        using var source = SKBitmap.Decode(sourceBytes) ?? throw new InvalidDataException("Source portrait could not be decoded.");
        var face = FindFaceWindow(source);
        var side = (int)Math.Round(Math.Max(face.Width, face.Height) * 2.12);
        side = Math.Clamp(side, Math.Min(source.Width, source.Height) / 2, Math.Min(source.Width, source.Height));
        var centerX = face.Left + face.Width / 2;
        var x = Math.Clamp(centerX - side / 2, 0, source.Width - side);
        var y = Math.Clamp((int)Math.Round(face.Top - face.Height * .30), 0, source.Height - side);
        var cropRect = new SKRectI(x, y, x + side, y + side);
        using var crop = Crop(source, cropRect);
        var cropPng = EncodePng(crop);
        var fallback = Reduce(crop, PaletteLimit, null);
        var mappedFace = Map(face, cropRect);
        var regions = RegionsFromFace(mappedFace);
        var mask = FigureMaskBuilder.Build(regions);
        var hints = PortraitColorMatcher.Describe(fallback, regions, mask);
        return new(cropPng, fallback, mask, regions, PortraitSourceFingerprint.Hash(sourceBytes), hints);
    }

    public PortraitPack Build(byte[] redrawBytes, PreparedPortrait prepared, string sourceAssetId,
        string workflow, long seed, DateTimeOffset generatedAt)
    {
        using var redraw = SKBitmap.Decode(redrawBytes) ?? throw new InvalidDataException("Redrawn portrait could not be decoded.");
        var pitch = PixelGridDetector.Detect(redraw);
        var rawPixels = ReduceRaw(redraw, pitch);
        var redrawFace = Map(FindFaceWindow(redraw), new SKRectI(0, 0, redraw.Width, redraw.Height));
        var regions = RegionsFromFace(redrawFace);
        var mask = FigureMaskBuilder.Build(regions);
        var matched = PortraitColorMatcher.Match(rawPixels, regions, mask,
            prepared.FallbackPixels, prepared.Regions, prepared.FigureMask);
        var basePixels = PaletteQuantizer.Quantize(matched, PaletteLimit);
        // Median-cut operates globally, so its palette merge can move a small
        // semantic region after the first transfer.  Re-transfer the quantized
        // pixels once and re-quantize; this keeps the palette bounded while
        // making the final (not merely pre-quantization) regional means match.
        matched = PortraitColorMatcher.Match(basePixels, regions, mask,
            prepared.FallbackPixels, prepared.Regions, prepared.FigureMask);
        basePixels = PaletteQuantizer.Quantize(matched, PaletteLimit);
        var colorMatch = PortraitColorMatcher.Measure(basePixels, regions, mask,
            prepared.FallbackPixels, prepared.Regions, prepared.FigureMask);
        var validation = Validate(basePixels, regions);
        var colorErrors = colorMatch.Errors().ToArray();
        if (colorErrors.Length > 0)
            validation = validation with { Valid = false, Errors = validation.Errors.Concat(colorErrors).ToArray() };
        if (!validation.Valid) throw new InvalidDataException(string.Join("; ", validation.Errors));
        return new(basePixels, prepared.FallbackPixels, mask, regions,
            new(sourceAssetId, prepared.SourceHash, PipelineVersion, workflow, seed, generatedAt, pitch),
            validation.PaletteSize, validation.FaceContrast, colorMatch);
    }

    public PortraitPack BuildFallback(PreparedPortrait prepared, string sourceAssetId, DateTimeOffset generatedAt)
    {
        var validation = Validate(prepared.FallbackPixels, prepared.Regions);
        if (!validation.Valid) throw new InvalidDataException("Faithful portrait fallback failed validation: " + string.Join("; ", validation.Errors));
        var metrics = PortraitColorMatcher.Measure(prepared.FallbackPixels, prepared.Regions, prepared.FigureMask,
            prepared.FallbackPixels, prepared.Regions, prepared.FigureMask);
        return new(prepared.FallbackPixels, prepared.FallbackPixels, prepared.FigureMask, prepared.Regions,
            new(sourceAssetId, prepared.SourceHash, PipelineVersion, "faithful-b", 0, generatedAt, null),
            validation.PaletteSize, validation.FaceContrast, metrics);
    }

    public PortraitValidation Validate(byte[] pixels, PortraitRegions regions)
    {
        var errors = new List<string>();
        if (pixels.Length != RgbFrame.ByteCount) errors.Add("base frame is not exactly 64x64 RGB");
        if (pixels.Length != RgbFrame.ByteCount) return new(false, errors, 0, 0);
        var palette = PaletteQuantizer.CountColors(pixels);
        if (palette > PaletteLimit) errors.Add($"palette has {palette} colours (limit {PaletteLimit})");
        foreach (var (name, region) in new[] { ("face", regions.Face), ("left eye", regions.LeftEye), ("right eye", regions.RightEye), ("mouth", regions.Mouth) })
            if (region.Width < 2 || region.Height < 2 || region.X < 0 || region.Y < 0 || region.Right > 64 || region.Bottom > 64)
                errors.Add($"{name} region was not found");
        if (regions.LeftEye.Height < 2 || regions.RightEye.Height < 2) errors.Add("eyes are smaller than two pixels high");
        var faceContrast = Contrast(pixels, regions.Face);
        if (faceContrast < 24) errors.Add($"face contrast is too low ({faceContrast:0.0})");
        var leftEyeContrast = Contrast(pixels, regions.LeftEye);
        var rightEyeContrast = Contrast(pixels, regions.RightEye);
        if (leftEyeContrast < 7 || rightEyeContrast < 7)
            errors.Add($"eye regions have insufficient contrast (left {leftEyeContrast:0.0} at {regions.LeftEye}, right {rightEyeContrast:0.0} at {regions.RightEye}, face {regions.Face})");
        var mouthContrast = Contrast(pixels, regions.Mouth);
        if (mouthContrast < 7) errors.Add($"mouth region has insufficient contrast ({mouthContrast:0.0})");
        return new(errors.Count == 0, errors, palette, faceContrast);
    }

    public static byte[] EncodePng(byte[] rgb, int width = 64, int height = 64)
    {
        if (rgb.Length != width * height * 3) throw new ArgumentException("RGB byte count does not match dimensions.", nameof(rgb));
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 3;
            bitmap.SetPixel(x, y, new SKColor(rgb[i], rgb[i + 1], rgb[i + 2]));
        }
        return EncodePng(bitmap);
    }

    public static byte[] DecodePng(byte[] png)
    {
        using var image = SKBitmap.Decode(png) ?? throw new InvalidDataException("Portrait asset could not be decoded.");
        if (image.Width != 64 || image.Height != 64) throw new InvalidDataException("Portrait asset must be exactly 64x64.");
        return ToRgb(image);
    }

    public static byte[] EncodeMaskPng(byte[] mask)
    {
        if (mask.Length != 64 * 64) throw new ArgumentException("Figure mask must be exactly 64x64.", nameof(mask));
        var rgb = new byte[RgbFrame.ByteCount];
        for (var i = 0; i < mask.Length; i++) rgb[i * 3] = rgb[i * 3 + 1] = rgb[i * 3 + 2] = mask[i];
        return EncodePng(rgb);
    }

    public static byte[] DecodeMaskPng(byte[] png)
    {
        var rgb = DecodePng(png);
        var mask = new byte[64 * 64];
        for (var i = 0; i < mask.Length; i++) mask[i] = rgb[i * 3];
        return mask;
    }

    private static byte[] Reduce(SKBitmap source, int colors, int? pitch)
        => PaletteQuantizer.Quantize(ReduceRaw(source, pitch), colors);

    private static byte[] ReduceRaw(SKBitmap source, int? pitch)
    {
        SKBitmap? alignedCrop = null;
        var aligned = source;
        if (pitch is > 1)
        {
            var width = source.Width - source.Width % pitch.Value;
            var height = source.Height - source.Height % pitch.Value;
            if (width >= 64 && height >= 64)
            {
                var x = (source.Width - width) / 2;
                var y = (source.Height - height) / 2;
                alignedCrop = Crop(source, new SKRectI(x, y, x + width, y + height));
                aligned = alignedCrop;
            }
        }
        try { return BoxDownsample(aligned, 64, 64); }
        finally { alignedCrop?.Dispose(); }
    }

    private static byte[] ToRgb(SKBitmap image)
    {
        var result = new byte[image.Width * image.Height * 3];
        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
        {
            var pixel = image.GetPixel(x, y);
            var i = (y * image.Width + x) * 3;
            var alpha = pixel.Alpha / 255d;
            result[i] = (byte)Math.Round(pixel.Red * alpha);
            result[i + 1] = (byte)Math.Round(pixel.Green * alpha);
            result[i + 2] = (byte)Math.Round(pixel.Blue * alpha);
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

    private static bool IsSkin(SKColor p)
    {
        var r = p.Red; var g = p.Green; var b = p.Blue;
        var cb = 128 - .168736 * r - .331264 * g + .5 * b;
        var cr = 128 + .5 * r - .418688 * g - .081312 * b;
        return r > 55 && Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > 10
            && cb is >= 72 and <= 138 && cr is >= 128 and <= 182;
    }

    private static int Sum(int[] integral, int stride, int x, int y, int width, int height)
        => integral[(y + height) * stride + x + width] - integral[y * stride + x + width]
           - integral[(y + height) * stride + x] + integral[y * stride + x];

    private static PixelRegion Map(SKRectI face, SKRectI crop)
    {
        var scale = 64d / crop.Width;
        return new PixelRegion(
            (int)Math.Round((face.Left - crop.Left) * scale), (int)Math.Round((face.Top - crop.Top) * scale),
            Math.Max(8, (int)Math.Round(face.Width * scale)), Math.Max(10, (int)Math.Round(face.Height * scale))).Clamp();
    }

    private static SKBitmap Crop(SKBitmap source, SKRectI rect)
    {
        var output = new SKBitmap(rect.Width, rect.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(output);
        canvas.DrawBitmap(source, rect, new SKRect(0, 0, rect.Width, rect.Height));
        canvas.Flush();
        return output;
    }

    private static byte[] BoxDownsample(SKBitmap source, int width, int height)
    {
        var output = new byte[width * height * 3];
        for (var dy = 0; dy < height; dy++)
        for (var dx = 0; dx < width; dx++)
        {
            var x0 = dx * source.Width / width;
            var x1 = Math.Max(x0 + 1, (dx + 1) * source.Width / width);
            var y0 = dy * source.Height / height;
            var y1 = Math.Max(y0 + 1, (dy + 1) * source.Height / height);
            long r = 0, g = 0, b = 0, count = 0;
            for (var sy = y0; sy < y1; sy++)
            for (var sx = x0; sx < x1; sx++)
            {
                var pixel = source.GetPixel(sx, sy);
                r += pixel.Red; g += pixel.Green; b += pixel.Blue; count++;
            }
            var i = (dy * width + dx) * 3;
            output[i] = (byte)(r / count);
            output[i + 1] = (byte)(g / count);
            output[i + 2] = (byte)(b / count);
        }
        return output;
    }

    private static PortraitRegions RegionsFromFace(PixelRegion face)
    {
        PixelRegion Rel(double x, double y, double w, double h) => new PixelRegion(
            face.X + (int)Math.Round(face.Width * x), face.Y + (int)Math.Round(face.Height * y),
            Math.Max(2, (int)Math.Round(face.Width * w)), Math.Max(2, (int)Math.Round(face.Height * h))).Clamp();
        var subjectX = Math.Max(0, face.X - face.Width / 2);
        var subjectWidth = Math.Min(64 - subjectX, face.Width * 2);
        var subject = new PixelRegion(subjectX, Math.Max(0, face.Y - face.Height / 4), subjectWidth, 64 - Math.Max(0, face.Y - face.Height / 4)).Clamp();
        return new PortraitRegions(face, Rel(.18, .34, .34, .18), Rel(.56, .34, .34, .18), Rel(.28, .65, .44, .22),
            new PixelRegion(subject.X, Math.Min(63, face.Bottom - 2), subject.Width, Math.Max(1, 64 - Math.Min(63, face.Bottom - 2))).Clamp(), subject);
    }

    private static double Contrast(byte[] pixels, PixelRegion region)
    {
        var min = 255d; var max = 0d;
        for (var y = region.Y; y < region.Bottom; y++)
        for (var x = region.X; x < region.Right; x++)
        {
            var i = (y * 64 + x) * 3;
            var l = .2126 * pixels[i] + .7152 * pixels[i + 1] + .0722 * pixels[i + 2];
            min = Math.Min(min, l); max = Math.Max(max, l);
        }
        return max - min;
    }
}

public static class PixelGridDetector
{
    public static int? Detect(SKBitmap image)
    {
        if (image.Width < 64 || image.Height < 64) return null;
        var best = (Pitch: 0, Score: 0d);
        var maxPitch = Math.Min(32, Math.Min(image.Width, image.Height) / 4);
        for (var pitch = 2; pitch <= maxPitch; pitch++)
        {
            if (image.Width % pitch != 0 || image.Height % pitch != 0) continue;
            double boundary = 0, total = 0;
            for (var y = 0; y < image.Height; y += 2)
            {
                for (var x = 1; x < image.Width; x++)
                {
                    var e = Distance(image.GetPixel(x - 1, y), image.GetPixel(x, y)); total += e;
                    if (x % pitch == 0) boundary += e;
                }
            }
            if (total <= 0) continue;
            var share = boundary / total;
            var expected = 1d / pitch;
            var score = (share - expected) / Math.Max(.001, 1 - expected);
            // Divisors of the true grid pitch see the same boundaries. Prefer the
            // largest pitch at an equal score so an 8 px grid is not reported as 2.
            if (score > best.Score + .0001 || (Math.Abs(score - best.Score) <= .0001 && pitch > best.Pitch))
                best = (pitch, score);
        }
        return best.Score >= .42 ? best.Pitch : null;
    }

    private static double Distance(SKColor a, SKColor b)
        => Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue);
}

public static class PaletteQuantizer
{
    private sealed record Entry(byte R, byte G, byte B, int Count);
    private sealed class Box(List<Entry> entries)
    {
        public List<Entry> Entries { get; } = entries;
        public int Count => Entries.Sum(item => item.Count);
        public int Range => Math.Max(Entries.Max(x => x.R) - Entries.Min(x => x.R),
            Math.Max(Entries.Max(x => x.G) - Entries.Min(x => x.G), Entries.Max(x => x.B) - Entries.Min(x => x.B)));
    }

    public static byte[] Quantize(byte[] rgb, int maxColors)
    {
        if (rgb.Length % 3 != 0) throw new ArgumentException("RGB data must contain complete pixels.");
        if (maxColors is < 2 or > 256) throw new ArgumentOutOfRangeException(nameof(maxColors));
        var histogram = new Dictionary<int, int>();
        for (var i = 0; i < rgb.Length; i += 3)
        {
            var key = rgb[i] << 16 | rgb[i + 1] << 8 | rgb[i + 2];
            histogram[key] = histogram.GetValueOrDefault(key) + 1;
        }
        if (histogram.Count <= maxColors) return rgb.ToArray();
        var boxes = new List<Box> { new(histogram.Select(pair => new Entry((byte)(pair.Key >> 16), (byte)(pair.Key >> 8), (byte)pair.Key, pair.Value)).ToList()) };
        while (boxes.Count < maxColors)
        {
            var box = boxes.Where(item => item.Entries.Count > 1).OrderByDescending(item => item.Range * (long)item.Count).FirstOrDefault();
            if (box is null) break;
            boxes.Remove(box);
            var rRange = box.Entries.Max(x => x.R) - box.Entries.Min(x => x.R);
            var gRange = box.Entries.Max(x => x.G) - box.Entries.Min(x => x.G);
            var bRange = box.Entries.Max(x => x.B) - box.Entries.Min(x => x.B);
            var ordered = (rRange >= gRange && rRange >= bRange ? box.Entries.OrderBy(x => x.R)
                : gRange >= bRange ? box.Entries.OrderBy(x => x.G) : box.Entries.OrderBy(x => x.B)).ToList();
            var half = box.Count / 2; var running = 0; var split = 1;
            for (; split < ordered.Count; split++) { running += ordered[split - 1].Count; if (running >= half) break; }
            boxes.Add(new(ordered[..split])); boxes.Add(new(ordered[split..]));
        }
        var palette = boxes.Select(box =>
        {
            var count = Math.Max(1, box.Count);
            return ((byte)(box.Entries.Sum(x => x.R * (long)x.Count) / count),
                (byte)(box.Entries.Sum(x => x.G * (long)x.Count) / count),
                (byte)(box.Entries.Sum(x => x.B * (long)x.Count) / count));
        }).ToArray();
        var output = new byte[rgb.Length];
        for (var i = 0; i < rgb.Length; i += 3)
        {
            var best = palette.MinBy(c => Square(rgb[i] - c.Item1) + Square(rgb[i + 1] - c.Item2) + Square(rgb[i + 2] - c.Item3));
            output[i] = best.Item1; output[i + 1] = best.Item2; output[i + 2] = best.Item3;
        }
        return output;
    }

    public static int CountColors(byte[] rgb)
    {
        var colors = new HashSet<int>();
        for (var i = 0; i + 2 < rgb.Length; i += 3) colors.Add(rgb[i] << 16 | rgb[i + 1] << 8 | rgb[i + 2]);
        return colors.Count;
    }

    private static int Square(int value) => value * value;
}
