namespace Leaf.Plugins.Pixoo;

public interface IFaceRenderer
{
    FrameLoop Render(Guid agentId, FaceState state, string color, int brightness, FaceGesture? gesture = null);
}

public sealed class ProceduralFaceRenderer : IFaceRenderer
{
    public FrameLoop Render(Guid agentId, FaceState state, string color, int brightness, FaceGesture? gesture = null)
    {
        var rgb = ParseColor(color);
        var levels = state switch
        {
            FaceState.Dark => new[] { 0d },
            FaceState.Asleep => new[] { .10, .08 },
            FaceState.Resting => new[] { .45, .48, .52, .57, .62, .66, .62, .57, .52, .48, .45, .45 },
            FaceState.Listening => new[] { .78, .88 },
            FaceState.Thinking => new[] { .58, .74, .90, .74 },
            FaceState.Speaking => new[] { .75, .95, .82, .95 },
            FaceState.Working => new[] { .55, .82, .95, .82 },
            FaceState.Dreaming => new[] { .30, .48, .65, .48 },
            _ => new[] { .5 },
        };
        var duration = state switch
        {
            FaceState.Resting => 700,
            FaceState.Asleep => 1000,
            FaceState.Thinking or FaceState.Working => 280,
            FaceState.Speaking => 180,
            _ => 450,
        };
        if (gesture is not null)
        {
            levels = [.72, .92, .82, .70];
            duration = 160;
        }
        var frames = levels.Select((level, index) => Draw(state, gesture, rgb, brightness, level, index, levels.Length, duration)).ToArray();
        return new FrameLoop(frames).Validate();
    }

    private static RgbFrame Draw(FaceState state, FaceGesture? gesture, (byte R, byte G, byte B) accent,
        int brightness, double pulse, int phase, int phases, int duration)
    {
        var pixels = new byte[RgbFrame.ByteCount];
        if (state == FaceState.Dark) return new RgbFrame(pixels, duration);
        var light = Math.Clamp(brightness, 0, 100) / 100d;
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
        {
            var dx = (x - 31.5) / 45d;
            var dy = (y - 31.5) / 45d;
            var vignette = Math.Max(0, 1 - (dx * dx + dy * dy));
            var drift = .82 + .18 * Math.Sin((x + y + phase * 4) / 13d);
            Put(pixels, x, y, accent, .075 * pulse * vignette * drift * light);
        }

        var asleep = state == FaceState.Asleep;
        var blink = state == FaceState.Resting && phases > 2 && phase == phases - 1;
        var eyeHeight = asleep || blink ? 1 : state == FaceState.Listening ? 8 : 6;
        var eyeWidth = state == FaceState.Thinking ? 7 : 8;
        var shift = state == FaceState.Thinking ? phase - phases / 2 : 0;
        Eye(pixels, 20 + shift, 25, eyeWidth, eyeHeight, accent, pulse * light);
        Eye(pixels, 44 + shift, 25, eyeWidth, eyeHeight, accent, pulse * light);

        if (asleep) Mouth(pixels, 32, 42, 8, 1, accent, .45 * light);
        else if (state == FaceState.Speaking) Mouth(pixels, 32, 42, 8, phase % 2 == 0 ? 2 : 6, accent, pulse * light);
        else if (state == FaceState.Working) Mouth(pixels, 32, 43, 10, 2 + phase % 2, accent, pulse * light);
        else if (state == FaceState.Dreaming) Mouth(pixels, 32, 42, 7, 3, accent, pulse * light);
        else Mouth(pixels, 32, 41, 9, 2, accent, pulse * light);

        if (gesture == FaceGesture.Arrive) Ring(pixels, 32, 32, 27 - phase * 2, accent, .7 * light);
        if (gesture == FaceGesture.React)
        {
            Spark(pixels, 8, 12, accent, light);
            Spark(pixels, 55, 10, accent, light);
        }
        return new RgbFrame(pixels, duration);
    }

    private static void Eye(byte[] p, int cx, int cy, int width, int height, (byte R, byte G, byte B) c, double level)
    {
        for (var y = -height / 2; y <= height / 2; y++)
        for (var x = -width / 2; x <= width / 2; x++)
            if ((x * x) / (double)(width * width) + (y * y) / (double)Math.Max(1, height * height) <= .28) Put(p, cx + x, cy + y, c, level);
    }

    private static void Mouth(byte[] p, int cx, int cy, int width, int height, (byte R, byte G, byte B) c, double level)
    {
        for (var y = -height / 2; y <= height / 2; y++)
        for (var x = -width; x <= width; x++)
            if (Math.Abs(x) + Math.Abs(y) < width + 1 && (height > 3 || Math.Abs(y) <= 1)) Put(p, cx + x, cy + y, c, level);
    }

    private static void Ring(byte[] p, int cx, int cy, int radius, (byte R, byte G, byte B) c, double level)
    {
        for (var y = 0; y < 64; y++) for (var x = 0; x < 64; x++)
            if (Math.Abs(Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - radius) < .8) Put(p, x, y, c, level);
    }

    private static void Spark(byte[] p, int cx, int cy, (byte R, byte G, byte B) c, double level)
    {
        for (var d = -4; d <= 4; d++) { Put(p, cx + d, cy, c, level); Put(p, cx, cy + d, c, level); }
    }

    private static void Put(byte[] p, int x, int y, (byte R, byte G, byte B) c, double level)
    {
        if (x is < 0 or > 63 || y is < 0 or > 63) return;
        var i = (y * 64 + x) * 3;
        p[i] = (byte)Math.Clamp(c.R * level, 0, 255);
        p[i + 1] = (byte)Math.Clamp(c.G * level, 0, 255);
        p[i + 2] = (byte)Math.Clamp(c.B * level, 0, 255);
    }

    private static (byte, byte, byte) ParseColor(string value)
    {
        var text = value.TrimStart('#');
        return text.Length == 6 && int.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var packed)
            ? ((byte)(packed >> 16), (byte)(packed >> 8), (byte)packed)
            : ((byte)124, (byte)92, (byte)255);
    }
}
