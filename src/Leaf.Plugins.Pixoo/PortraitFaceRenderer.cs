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
        TintBackground(baseFrame, pack.Regions.Subject, accent, .14);
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
                BrightenRegion(pixels, pack.Regions.Torso, .97 + .035 * Math.Sin(phase / 12d * Math.PI * 2));
                if (phase == 9) ApplyBlink(pixels, pack.Regions);
                return Frame(pixels, brightness, phase == 9 ? 120 : 700);
            }).ToArray(),
            FaceState.Listening => new[] { 1.06, 1.10 }.Select(level =>
            {
                var pixels = baseFrame.ToArray(); BrightenRegion(pixels, pack.Regions.Face, level);
                return Frame(pixels, brightness, 650);
            }).ToArray(),
            FaceState.Thinking => Enumerable.Range(0, 4).Select(phase =>
            {
                var pixels = baseFrame.ToArray(); ApplyEyeShift(pixels, pack.Regions, phase < 2 ? 1 : 0, -1);
                TintBackground(pixels, pack.Regions.Subject, accent, .04 + phase * .015);
                return Frame(pixels, brightness, 500);
            }).ToArray(),
            FaceState.Speaking => Enumerable.Range(0, 3).Select(shape =>
            {
                var pixels = baseFrame.ToArray(); ApplyMouthShape(pixels, pack.Regions, shape);
                return Frame(pixels, brightness, 190);
            }).ToArray(),
            FaceState.Working => Enumerable.Range(0, 4).Select(phase =>
            {
                var pixels = baseFrame.ToArray(); ShimmerBackground(pixels, pack.Regions.Subject, phase);
                return Frame(pixels, brightness, 320);
            }).ToArray(),
            FaceState.Dreaming => Enumerable.Range(0, 4).Select(phase =>
            {
                var pixels = baseFrame.ToArray(); ApplyBlink(pixels, pack.Regions);
                HueDriftBackground(pixels, pack.Regions.Subject, phase);
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
        var region = regions.Mouth;
        var dark = Darkest(pixels, region);
        var centerY = region.Y + region.Height / 2;
        var inset = Math.Max(1, region.Width / 6);
        var opening = shape switch { 0 => 0, 1 => 1, _ => Math.Max(1, region.Height / 3) };
        for (var y = centerY - opening; y <= centerY + opening; y++)
        for (var x = region.X + inset; x < region.Right - inset; x++)
            Put(pixels, x, y, dark);
        if (shape == 2 && centerY + opening + 1 < region.Bottom)
        {
            var light = Lighten(dark, 1.7);
            for (var x = region.X + inset + 1; x < region.Right - inset - 1; x++) Put(pixels, x, centerY + opening + 1, light);
        }
    }

    internal static void ApplyEyeShift(byte[] pixels, PortraitRegions regions, int dx, int dy)
    {
        ShiftRegion(pixels, regions.LeftEye, dx, dy);
        ShiftRegion(pixels, regions.RightEye, dx, dy);
    }

    private static RgbFrame Frame(byte[] pixels, double brightness, int duration)
        => new(Scale(pixels, Math.Clamp(brightness, 0, 100) / 100d), duration);

    private static void BlinkEye(byte[] pixels, PixelRegion eye, PixelRegion face)
    {
        var skin = SampleSurrounding(pixels, eye, face);
        var lid = Darken(skin, .62);
        Fill(pixels, eye, skin);
        var y = eye.Y + eye.Height / 2;
        for (var x = eye.X + 1; x < eye.Right - 1; x++) Put(pixels, x, y, lid);
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

    private static void BrightenRegion(byte[] pixels, PixelRegion region, double factor)
    {
        for (var y = region.Y; y < region.Bottom; y++)
        for (var x = region.X; x < region.Right; x++)
        {
            var i = (y * 64 + x) * 3;
            pixels[i] = Clamp(pixels[i] * factor); pixels[i + 1] = Clamp(pixels[i + 1] * factor); pixels[i + 2] = Clamp(pixels[i + 2] * factor);
        }
    }

    private static void TintBackground(byte[] pixels, PixelRegion subject, (byte R, byte G, byte B) accent, double amount)
    {
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
        {
            if (subject.Contains(x, y)) continue;
            var i = (y * 64 + x) * 3;
            pixels[i] = Mix(pixels[i], accent.R, amount); pixels[i + 1] = Mix(pixels[i + 1], accent.G, amount); pixels[i + 2] = Mix(pixels[i + 2], accent.B, amount);
        }
    }

    private static void ShimmerBackground(byte[] pixels, PixelRegion subject, int phase)
    {
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
        {
            if (subject.Contains(x, y) || (x + y + phase * 3) % 11 > 1) continue;
            var i = (y * 64 + x) * 3;
            pixels[i] = Clamp(pixels[i] * 1.06); pixels[i + 1] = Clamp(pixels[i + 1] * 1.06); pixels[i + 2] = Clamp(pixels[i + 2] * 1.06);
        }
    }

    private static void HueDriftBackground(byte[] pixels, PixelRegion subject, int phase)
    {
        var drift = phase switch { 0 => (6, 0, 8), 1 => (2, 3, 10), 2 => (-2, 6, 8), _ => (3, 2, 6) };
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
        {
            if (subject.Contains(x, y)) continue;
            var i = (y * 64 + x) * 3;
            pixels[i] = Clamp(pixels[i] + drift.Item1); pixels[i + 1] = Clamp(pixels[i + 1] + drift.Item2); pixels[i + 2] = Clamp(pixels[i + 2] + drift.Item3);
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

    private static (byte R, byte G, byte B) Darkest(byte[] pixels, PixelRegion region)
    {
        var best = (R: (byte)80, G: (byte)40, B: (byte)40); var bestL = double.MaxValue;
        for (var y = region.Y; y < region.Bottom; y++)
        for (var x = region.X; x < region.Right; x++)
        {
            var i = (y * 64 + x) * 3; var l = .2126 * pixels[i] + .7152 * pixels[i + 1] + .0722 * pixels[i + 2];
            if (l < bestL) { bestL = l; best = (pixels[i], pixels[i + 1], pixels[i + 2]); }
        }
        return best;
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
    private static (byte R, byte G, byte B) Lighten((byte R, byte G, byte B) value, double factor)
        => (Clamp(value.R * factor), Clamp(value.G * factor), Clamp(value.B * factor));
    private static byte Mix(byte source, byte target, double amount) => Clamp(source * (1 - amount) + target * amount);
    private static byte Clamp(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
}
