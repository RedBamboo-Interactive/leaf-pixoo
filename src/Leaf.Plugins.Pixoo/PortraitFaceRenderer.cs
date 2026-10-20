namespace Leaf.Plugins.Pixoo;

public interface IPortraitPackCache
{
    PortraitPack? Current(Guid agentId);
}

public sealed class PortraitFaceRenderer(IPortraitPackCache packs, ProceduralFaceRenderer fallback) : IFaceRenderer
{
    public FrameLoop Render(Guid agentId, FaceState state, string color, int brightness, FaceGesture? gesture = null)
    {
        var pack = packs.Current(agentId);
        return pack is null
            ? fallback.Render(agentId, state, color, brightness, gesture)
            : PortraitAnimation.Render(pack, state, color, brightness, gesture);
    }
}

public static class PortraitAnimation
{
    public static FrameLoop Render(PortraitPack pack, FaceState state, string color, int brightness, FaceGesture? gesture = null)
    {
        if (state == FaceState.Dark) return new FrameLoop([new RgbFrame(new byte[RgbFrame.ByteCount], 900)]);
        var accent = ParseColor(color);
        var baseFrame = pack.BasePixels.ToArray();
        TintBackground(baseFrame, pack.FigureMask, accent, .14);
        if (gesture == FaceGesture.Arrive)
        {
            var levels = new[] { 0d, .20, .48, .78, 1d };
            return new FrameLoop(levels.Select(level => new RgbFrame(Scale(baseFrame, level * brightness / 100d), 150)).ToArray()).Validate();
        }
        if (gesture == FaceGesture.React)
        {
            var levels = new[] { 1d, 1.22, 1.08, 1d };
            return new FrameLoop(levels.Select(level => new RgbFrame(Scale(baseFrame, level * brightness / 100d), 140)).ToArray()).Validate();
        }

        var frames = state switch
        {
            FaceState.Resting => Enumerable.Range(0, 12).Select(phase =>
            {
                var pixels = baseFrame.ToArray();
                var blink = phase switch { 8 or 10 => .5, 9 => 1, _ => 0 };
                if (blink > 0) ApplyBlink(pixels, pack.Regions, pack.Landmarks, blink);
                var breathing = .985 + .025 * Math.Sin(phase / 12d * Math.PI * 2);
                return Frame(pixels, brightness * breathing, blink > 0 ? 120 : 700);
            }).ToArray(),
            FaceState.Listening => new[] { 1.06, 1.10 }.Select(level =>
            {
                return Frame(baseFrame, brightness * level, 650);
            }).ToArray(),
            FaceState.Thinking => Enumerable.Range(0, 4).Select(phase =>
            {
                var pixels = baseFrame.ToArray(); ApplyEyeShift(pixels, pack.Regions, phase < 2 ? 1 : 0, -1);
                TintBackground(pixels, pack.FigureMask, accent, .04 + phase * .015);
                return Frame(pixels, brightness, 500);
            }).ToArray(),
            FaceState.Speaking => Enumerable.Range(0, 3).Select(shape =>
            {
                var pixels = baseFrame.ToArray(); ApplyMouthShape(pixels, pack.Regions, shape, pack.Landmarks);
                return Frame(pixels, brightness, 190);
            }).ToArray(),
            FaceState.Working => Enumerable.Range(0, 4).Select(phase =>
            {
                var pixels = baseFrame.ToArray(); ShimmerBackground(pixels, pack.FigureMask, phase);
                return Frame(pixels, brightness, 320);
            }).ToArray(),
            FaceState.Dreaming => Enumerable.Range(0, 4).Select(phase =>
            {
                var pixels = baseFrame.ToArray(); ApplyBlink(pixels, pack.Regions, pack.Landmarks);
                HueDriftBackground(pixels, pack.FigureMask, phase);
                return Frame(pixels, brightness * .62, 900);
            }).ToArray(),
            FaceState.Asleep => Enumerable.Range(0, 2).Select(_ =>
            {
                var pixels = baseFrame.ToArray(); ApplyBlink(pixels, pack.Regions, pack.Landmarks);
                return Frame(pixels, brightness * .24, 1200);
            }).ToArray(),
            _ => [Frame(baseFrame, brightness, 600)],
        };
        return new FrameLoop(frames).Validate();
    }

    internal static void ApplyBlink(byte[] pixels, PortraitRegions regions, PortraitLandmarks? landmarks = null,
        double closure = 1)
    {
        closure = Math.Clamp(closure, 0, 1);
        if (closure <= 0) return;
        BlinkEye(pixels, landmarks?.LeftEyeOutline ?? RectangleOutline(regions.LeftEye), regions.Face, closure);
        BlinkEye(pixels, landmarks?.RightEyeOutline ?? RectangleOutline(regions.RightEye), regions.Face, closure);
    }

    internal static void ApplyMouthShape(byte[] pixels, PortraitRegions regions, int shape,
        PortraitLandmarks? landmarks = null)
    {
        // The closed speaking frame is the accepted portrait byte-for-byte.
        if (shape <= 0) return;
        var region = regions.Mouth;
        var (interior, lip) = MouthColors(pixels, region);
        var lipLine = landmarks?.LipLine is { Length: > 0 } detected
            ? detected : [new PixelPoint(region.X + region.Width / 2, region.Y + region.Height / 2)];
        var centerX = (int)Math.Round(lipLine.Average(point => point.X));
        var height = Math.Min(region.Height, shape == 1 ? 1 : 2);
        var width = Math.Min(region.Width, shape == 1 ? 4 : 6);
        var left = Math.Clamp(centerX - width / 2, region.X, region.Right - width);
        for (var x = left; x < left + width; x++)
        {
            var line = lipLine.MinBy(point => Math.Abs(point.X - x));
            var centerY = Math.Clamp(line.Y, region.Y, region.Bottom - height);
            for (var row = 0; row < height; row++)
            {
                if (height == 2 && row == 1 && (x == left || x == left + width - 1)) continue;
                Put(pixels, x, centerY + row, x == left || x == left + width - 1 ? lip : interior);
            }
        }
    }

    private static ((byte R, byte G, byte B) Interior, (byte R, byte G, byte B) Lip) MouthColors(byte[] pixels, PixelRegion region)
    {
        var colors = new List<(byte R, byte G, byte B, double L)>();
        for (var y = region.Y; y < region.Bottom; y++)
        for (var x = region.X; x < region.Right; x++)
        {
            var i = (y * 64 + x) * 3;
            var r = pixels[i]; var g = pixels[i + 1]; var b = pixels[i + 2];
            if (r >= g && r >= b) colors.Add((r, g, b, .2126 * r + .7152 * g + .0722 * b));
        }
        var sample = colors.OrderBy(color => color.L).Take(Math.Max(1, colors.Count / 2)).ToArray();
        if (sample.Length == 0) return ((72, 34, 31), (118, 61, 52));
        // Darken a sampled lip tone, but keep a readable warm red-brown instead of black.
        var meanR = sample.Average(color => color.R);
        var meanG = sample.Average(color => color.G);
        var meanB = sample.Average(color => color.B);
        var interior = ((byte)Math.Clamp(Math.Round(meanR * .62), 52, 112),
            (byte)Math.Clamp(Math.Round(sample.Average(color => color.G) * .52), 24, 68),
            (byte)Math.Clamp(Math.Round(sample.Average(color => color.B) * .52), 20, 62));
        var lip = ((byte)Math.Clamp(Math.Round(meanR), 72, 164),
            (byte)Math.Clamp(Math.Round(meanG), 30, 100),
            (byte)Math.Clamp(Math.Round(meanB), 28, 92));
        return (interior, lip);
    }

    internal static void ApplyEyeShift(byte[] pixels, PortraitRegions regions, int dx, int dy)
    {
        ShiftRegion(pixels, EyeCore(pixels, regions.LeftEye), dx, dy);
        ShiftRegion(pixels, EyeCore(pixels, regions.RightEye), dx, dy);
    }

    private static RgbFrame Frame(byte[] pixels, double brightness, int duration)
        => new(Scale(pixels, Math.Clamp(brightness, 0, 100) / 100d), duration);

    private static void BlinkEye(byte[] pixels, PixelPoint[] outline, PixelRegion face, double closure)
    {
        var columns = RasterizePolygon(outline).GroupBy(point => point.X).OrderBy(column => column.Key).ToArray();
        if (columns.Length == 0) return;
        var skin = SampleAboveEye(pixels, columns, face);
        var lash = Darken(skin, .52);
        foreach (var column in columns)
        {
            var top = column.Min(point => point.Y);
            var bottom = column.Max(point => point.Y);
            var height = bottom - top + 1;
            var covered = Math.Clamp((int)Math.Ceiling(height * closure), 1, height);
            var lidEdge = top + covered - 1;
            for (var y = top; y <= lidEdge; y++) Put(pixels, column.Key, y, skin);
            Put(pixels, column.Key, lidEdge, lash);
        }
    }

    internal static IReadOnlyList<PixelPoint> RasterizePolygon(PixelPoint[] outline)
    {
        if (outline.Length < 3) return [];
        var minX = Math.Max(0, outline.Min(point => point.X));
        var maxX = Math.Min(63, outline.Max(point => point.X));
        var minY = Math.Max(0, outline.Min(point => point.Y));
        var maxY = Math.Min(63, outline.Max(point => point.Y));
        var pixels = new List<PixelPoint>();
        for (var y = minY; y <= maxY; y++)
        for (var x = minX; x <= maxX; x++)
            if (OnBoundary(outline, x, y) || InsidePolygon(outline, x, y)) pixels.Add(new(x, y));
        return pixels;
    }

    private static bool OnBoundary(PixelPoint[] polygon, int x, int y)
    {
        for (var index = 0; index < polygon.Length; index++)
        {
            var a = polygon[index];
            var b = polygon[(index + 1) % polygon.Length];
            var cross = (long)(x - a.X) * (b.Y - a.Y) - (long)(y - a.Y) * (b.X - a.X);
            if (cross == 0 && x >= Math.Min(a.X, b.X) && x <= Math.Max(a.X, b.X)
                && y >= Math.Min(a.Y, b.Y) && y <= Math.Max(a.Y, b.Y)) return true;
        }
        return false;
    }

    private static bool InsidePolygon(PixelPoint[] polygon, int x, int y)
    {
        var inside = false;
        for (int index = 0, previous = polygon.Length - 1; index < polygon.Length; previous = index++)
        {
            var a = polygon[index];
            var b = polygon[previous];
            if ((a.Y > y) == (b.Y > y)) continue;
            var crossing = (b.X - a.X) * (y - a.Y) / (double)(b.Y - a.Y) + a.X;
            if (x < crossing) inside = !inside;
        }
        return inside;
    }

    private static PixelPoint[] RectangleOutline(PixelRegion region) =>
    [
        new(region.X, region.Y), new(region.Right - 1, region.Y),
        new(region.Right - 1, region.Bottom - 1), new(region.X, region.Bottom - 1),
    ];

    private static PixelRegion EyeCore(byte[] pixels, PixelRegion search)
    {
        var darkest = (X: search.X + search.Width / 2, Y: search.Y + search.Height / 2, Luminance: double.MaxValue);
        for (var y = search.Y; y < search.Bottom; y++)
        for (var x = search.X; x < search.Right; x++)
        {
            var i = (y * 64 + x) * 3;
            var luminance = .2126 * pixels[i] + .7152 * pixels[i + 1] + .0722 * pixels[i + 2];
            if (luminance < darkest.Luminance) darkest = (x, y, luminance);
        }
        var width = Math.Clamp(search.Width / 2, 3, 5);
        var height = Math.Clamp(search.Height / 2, 2, 3);
        return new PixelRegion(
            Math.Clamp(darkest.X - width / 2, search.X, search.Right - width),
            Math.Clamp(darkest.Y - height / 2, search.Y, search.Bottom - height),
            width, height);
    }

    private static void ShiftRegion(byte[] pixels, PixelRegion region, int dx, int dy)
    {
        var copy = pixels.ToArray();
        for (var y = region.Y; y < region.Bottom; y++)
        for (var x = region.X; x < region.Right; x++)
        {
            var sx = Math.Clamp(x - dx, region.X, region.Right - 1);
            var sy = Math.Clamp(y - dy, region.Y, region.Bottom - 1);
            var source = (sy * 64 + sx) * 3; var target = (y * 64 + x) * 3;
            pixels[target] = copy[source]; pixels[target + 1] = copy[source + 1]; pixels[target + 2] = copy[source + 2];
        }
    }

    private static void TintBackground(byte[] pixels, byte[] figureMask, (byte R, byte G, byte B) accent, double amount)
    {
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
        {
            var background = 1 - figureMask[y * 64 + x] / 255d;
            if (background <= 0) continue;
            var i = (y * 64 + x) * 3;
            pixels[i] = Mix(pixels[i], accent.R, amount * background); pixels[i + 1] = Mix(pixels[i + 1], accent.G, amount * background); pixels[i + 2] = Mix(pixels[i + 2], accent.B, amount * background);
        }
    }

    private static void ShimmerBackground(byte[] pixels, byte[] figureMask, int phase)
    {
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
        {
            var background = 1 - figureMask[y * 64 + x] / 255d;
            if (background <= 0 || (x + y + phase * 3) % 11 > 1) continue;
            var i = (y * 64 + x) * 3;
            var factor = 1 + .06 * background;
            pixels[i] = Clamp(pixels[i] * factor); pixels[i + 1] = Clamp(pixels[i + 1] * factor); pixels[i + 2] = Clamp(pixels[i + 2] * factor);
        }
    }

    private static void HueDriftBackground(byte[] pixels, byte[] figureMask, int phase)
    {
        var drift = phase switch { 0 => (6, 0, 8), 1 => (2, 3, 10), 2 => (-2, 6, 8), _ => (3, 2, 6) };
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
        {
            var background = 1 - figureMask[y * 64 + x] / 255d;
            if (background <= 0) continue;
            var i = (y * 64 + x) * 3;
            pixels[i] = Clamp(pixels[i] + drift.Item1 * background); pixels[i + 1] = Clamp(pixels[i + 1] + drift.Item2 * background); pixels[i + 2] = Clamp(pixels[i + 2] + drift.Item3 * background);
        }
    }

    private static (byte R, byte G, byte B) SampleAboveEye(byte[] pixels,
        IEnumerable<IGrouping<int, PixelPoint>> columns, PixelRegion face)
    {
        var samples = new List<(byte R, byte G, byte B, double Luminance)>();
        foreach (var column in columns)
        {
            var top = column.Min(point => point.Y);
            for (var y = Math.Max(face.Y, top - 2); y < top; y++)
            {
                if (!face.Contains(column.Key, y)) continue;
                var i = (y * 64 + column.Key) * 3;
                var r = pixels[i]; var g = pixels[i + 1]; var b = pixels[i + 2];
                samples.Add((r, g, b, .2126 * r + .7152 * g + .0722 * b));
            }
        }
        if (samples.Count == 0) return (128, 96, 80);
        var skin = samples.OrderByDescending(sample => sample.Luminance)
            .Take(Math.Max(1, (int)Math.Ceiling(samples.Count * .67))).ToArray();
        return ((byte)Math.Round(skin.Average(sample => sample.R)),
            (byte)Math.Round(skin.Average(sample => sample.G)),
            (byte)Math.Round(skin.Average(sample => sample.B)));
    }

    private static void Put(byte[] pixels, int x, int y, (byte R, byte G, byte B) color)
    {
        var i = (y * 64 + x) * 3; pixels[i] = color.R; pixels[i + 1] = color.G; pixels[i + 2] = color.B;
    }

    private static byte[] Scale(byte[] pixels, double factor)
    {
        var result = new byte[pixels.Length];
        for (var i = 0; i < pixels.Length; i++) result[i] = Clamp(pixels[i] * factor);
        return result;
    }

    private static (byte R, byte G, byte B) ParseColor(string value)
    {
        var text = value.TrimStart('#');
        return text.Length == 6 && int.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var packed)
            ? ((byte)(packed >> 16), (byte)(packed >> 8), (byte)packed) : ((byte)124, (byte)92, (byte)255);
    }

    private static (byte R, byte G, byte B) Darken((byte R, byte G, byte B) value, double factor)
        => (Clamp(value.R * factor), Clamp(value.G * factor), Clamp(value.B * factor));
    private static byte Mix(byte source, byte target, double amount) => Clamp(source * (1 - amount) + target * amount);
    private static byte Clamp(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
}
