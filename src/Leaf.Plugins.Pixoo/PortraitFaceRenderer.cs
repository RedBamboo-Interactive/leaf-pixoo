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
                if (phase == 9) ApplyBlink(pixels, pack.Regions);
                var breathing = .985 + .025 * Math.Sin(phase / 12d * Math.PI * 2);
                return Frame(pixels, brightness * breathing, phase == 9 ? 120 : 700);
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
                var pixels = baseFrame.ToArray(); ApplyMouthShape(pixels, pack.Regions, shape);
                return Frame(pixels, brightness, 190);
            }).ToArray(),
            FaceState.Working => Enumerable.Range(0, 4).Select(phase =>
            {
                var pixels = baseFrame.ToArray(); ShimmerBackground(pixels, pack.FigureMask, phase);
                return Frame(pixels, brightness, 320);
            }).ToArray(),
            FaceState.Dreaming => Enumerable.Range(0, 4).Select(phase =>
            {
                var pixels = baseFrame.ToArray(); ApplyBlink(pixels, pack.Regions);
                HueDriftBackground(pixels, pack.FigureMask, phase);
                return Frame(pixels, brightness * .62, 900);
            }).ToArray(),
            FaceState.Asleep => Enumerable.Range(0, 2).Select(_ =>
            {
                var pixels = baseFrame.ToArray(); ApplyBlink(pixels, pack.Regions);
                return Frame(pixels, brightness * .24, 1200);
            }).ToArray(),
            _ => [Frame(baseFrame, brightness, 600)],
        };
        return new FrameLoop(frames).Validate();
    }

    internal static void ApplyBlink(byte[] pixels, PortraitRegions regions)
    {
        BlinkEye(pixels, regions.LeftEye, regions.Face);
        BlinkEye(pixels, regions.RightEye, regions.Face);
    }

    internal static void ApplyMouthShape(byte[] pixels, PortraitRegions regions, int shape)
    {
        // The closed speaking frame is the accepted portrait byte-for-byte.
        if (shape <= 0) return;
        var region = regions.Mouth;
        var core = MouthCore(pixels, region);
        var (interior, lip) = MouthColors(pixels, core);
        var centerX = core.X + core.Width / 2;
        var centerY = core.Y + core.Height / 2;
        var height = Math.Min(region.Height, shape == 1 ? 1 : 2);
        var width = Math.Min(region.Width, shape == 1 ? 4 : 6);
        var top = Math.Clamp(centerY, region.Y, region.Bottom - height);
        for (var y = top; y < top + height; y++)
        {
            var row = y - top;
            var rowWidth = height == 2 && row == 1 ? Math.Max(4, width - 2) : width;
            var left = Math.Clamp(centerX - rowWidth / 2, region.X, region.Right - rowWidth);
            for (var x = left; x < left + rowWidth; x++)
                Put(pixels, x, y, x == left || x == left + rowWidth - 1 ? lip : interior);
        }
    }

    private static PixelRegion MouthCore(byte[] pixels, PixelRegion search)
    {
        var candidates = new List<(int X, int Y, double Score)>();
        for (var y = search.Y; y < search.Bottom; y++)
        for (var x = search.X; x < search.Right; x++)
        {
            var i = (y * 64 + x) * 3;
            var r = pixels[i]; var g = pixels[i + 1]; var b = pixels[i + 2];
            var luminance = .2126 * r + .7152 * g + .0722 * b;
            var warmth = r - (g + b) / 2d;
            if (r >= g && r >= b && warmth > 4)
                candidates.Add((x, y, luminance - warmth * .7));
        }
        var selected = candidates.OrderBy(item => item.Score).Take(Math.Max(1, Math.Min(12, candidates.Count / 3))).ToArray();
        var centerX = selected.Length == 0 ? search.X + search.Width / 2 : (int)Math.Round(selected.Average(item => item.X));
        var centerY = selected.Length == 0 ? search.Y + search.Height / 2 : (int)Math.Round(selected.Average(item => item.Y));
        return new PixelRegion(Math.Clamp(centerX - 3, search.X, Math.Max(search.X, search.Right - 6)),
            Math.Clamp(centerY - 1, search.Y, Math.Max(search.Y, search.Bottom - 2)), Math.Min(6, search.Width), Math.Min(2, search.Height));
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

    private static void BlinkEye(byte[] pixels, PixelRegion eye, PixelRegion face)
    {
        var core = EyeCore(pixels, eye);
        var skin = SampleSurrounding(pixels, core, face);
        var lid = Darken(skin, .62);
        Fill(pixels, core, skin);
        var y = core.Y + core.Height / 2;
        for (var x = core.X; x < core.Right; x++) Put(pixels, x, y, lid);
    }

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

    private static (byte R, byte G, byte B) SampleSurrounding(byte[] pixels, PixelRegion region, PixelRegion face)
    {
        long r = 0, g = 0, b = 0, count = 0;
        for (var y = Math.Max(face.Y, region.Y - 2); y < Math.Min(face.Bottom, region.Bottom + 2); y++)
        for (var x = Math.Max(face.X, region.X - 2); x < Math.Min(face.Right, region.Right + 2); x++)
        {
            if (region.Contains(x, y)) continue;
            var i = (y * 64 + x) * 3; r += pixels[i]; g += pixels[i + 1]; b += pixels[i + 2]; count++;
        }
        return count == 0 ? ((byte)128, (byte)96, (byte)80) : ((byte)(r / count), (byte)(g / count), (byte)(b / count));
    }

    private static void Fill(byte[] pixels, PixelRegion region, (byte R, byte G, byte B) color)
    {
        for (var y = region.Y; y < region.Bottom; y++) for (var x = region.X; x < region.Right; x++) Put(pixels, x, y, color);
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
