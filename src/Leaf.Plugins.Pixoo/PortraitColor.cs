namespace Leaf.Plugins.Pixoo;

public sealed record PortraitColorHints(string Hair, string Jacket, string InnerTop);

public sealed record PortraitColorMatchMetrics(
    double HairHue, double ReferenceHairHue, double HairLightness, double ReferenceHairLightness, int HairSamples,
    double GarmentHue, double ReferenceGarmentHue, double GarmentLightness, double ReferenceGarmentLightness, int GarmentSamples,
    double JacketHue, double ReferenceJacketHue, double JacketLightness, double ReferenceJacketLightness, int JacketSamples,
    bool GuardApplied = false, string[]? GuardRegions = null)
{
    public double HairHueDelta => HueDistance(HairHue, ReferenceHairHue);
    public double HairLightnessDelta => Math.Abs(HairLightness - ReferenceHairLightness);
    public double GarmentHueDelta => HueDistance(GarmentHue, ReferenceGarmentHue);
    public double GarmentLightnessDelta => Math.Abs(GarmentLightness - ReferenceGarmentLightness);
    public double JacketHueDelta => HueDistance(JacketHue, ReferenceJacketHue);
    public double JacketLightnessDelta => Math.Abs(JacketLightness - ReferenceJacketLightness);

    public IEnumerable<string> Errors()
    {
        if (HairSamples >= 6 && (HairHueDelta > 12 || HairLightnessDelta > 10))
            yield return $"hair colour mismatch (hue Δ{HairHueDelta:0.0}°, L* Δ{HairLightnessDelta:0.0})";
        if (GarmentSamples >= 6 && (GarmentHueDelta > 15 || GarmentLightnessDelta > 10))
            yield return $"inner garment colour mismatch (hue Δ{GarmentHueDelta:0.0}°, L* Δ{GarmentLightnessDelta:0.0})";
        if (JacketSamples >= 6 && (JacketHueDelta > 15 || JacketLightnessDelta > 10))
            yield return $"outer garment colour mismatch (hue Δ{JacketHueDelta:0.0}°, L* Δ{JacketLightnessDelta:0.0})";
    }

    private static double HueDistance(double a, double b)
    {
        var difference = Math.Abs(a - b) % 360;
        return Math.Min(difference, 360 - difference);
    }
}

public sealed record PortraitColorGuardResult(byte[] Pixels, PortraitColorMatchMetrics Metrics);

public static class FigureMaskBuilder
{
    public static byte[] Build(PortraitRegions regions)
    {
        var mask = new byte[64 * 64];
        var face = regions.Face;
        var centerX = face.X + face.Width / 2d;
        var centerY = face.Y + face.Height / 2d;
        var radiusX = Math.Max(8, face.Width * 1.12);
        var radiusY = Math.Max(10, face.Height * .82);
        var torsoTop = Math.Max(0, face.Bottom - face.Height * .10);
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
        {
            var headDistance = Math.Sqrt(Math.Pow((x + .5 - centerX) / radiusX, 2)
                + Math.Pow((y + .5 - centerY) / radiusY, 2));
            var head = Feather(headDistance, 1.04, 1.18);
            var torso = 0d;
            if (y + .5 >= torsoTop)
            {
                var progress = Math.Clamp((y + .5 - torsoTop) / Math.Max(1, 64 - torsoTop), 0, 1);
                var halfWidth = Lerp(face.Width * .72, 37, progress);
                var edgeDistance = Math.Abs(x + .5 - centerX) - halfWidth;
                torso = edgeDistance <= 0 ? 1 : edgeDistance >= 3 ? 0 : 1 - edgeDistance / 3;
                if (y + .5 < torsoTop + 3) torso *= (y + .5 - torsoTop) / 3;
            }
            mask[y * 64 + x] = (byte)Math.Round(Math.Clamp(Math.Max(head, torso), 0, 1) * 255);
        }
        return mask;
    }

    private static double Feather(double value, double solid, double transparent)
        => value <= solid ? 1 : value >= transparent ? 0 : 1 - (value - solid) / (transparent - solid);
    private static double Lerp(double a, double b, double amount) => a + (b - a) * amount;
}

public static class PortraitColorMatcher
{
    private enum Zone { Hair, Skin, InnerTop, Jacket, Background }
    private readonly record struct Lab(double L, double A, double B);
    private readonly record struct Statistics(Lab Mean, int Count);
    private readonly record struct ColorSummary(double Hue, double Lightness, double Saturation, int Count, byte R, byte G, byte B);

    public static PortraitColorGuardResult ApplyGuard(byte[] target, PortraitRegions targetRegions, byte[] targetMask,
        byte[] reference, PortraitRegions referenceRegions, byte[] referenceMask)
    {
        var before = Measure(target, targetRegions, targetMask, reference, referenceRegions, referenceMask);
        var shifts = new List<Zone>();
        if (before.HairSamples >= 6 && (before.HairHueDelta > 12 || before.HairLightnessDelta > 10)) shifts.Add(Zone.Hair);
        if (before.GarmentSamples >= 6 && (before.GarmentHueDelta > 15 || before.GarmentLightnessDelta > 10)) shifts.Add(Zone.InnerTop);
        if (before.JacketSamples >= 6 && (before.JacketHueDelta > 15 || before.JacketLightnessDelta > 10)) shifts.Add(Zone.Jacket);
        if (shifts.Count == 0) return new(target.ToArray(), before);

        var output = target.ToArray();
        foreach (var zone in shifts)
        {
            var targetIndices = Indices(target, targetRegions, targetMask, zone).ToArray();
            var referenceIndices = Indices(reference, referenceRegions, referenceMask, zone).ToArray();
            if (targetIndices.Length < 4 || referenceIndices.Length < 4) continue;
            var from = Stats(target, targetIndices);
            var to = Stats(reference, referenceIndices);
            foreach (var pixel in targetIndices)
            {
                var lab = ToLab(target, pixel);
                var adjusted = new Lab(
                    lab.L + to.Mean.L - from.Mean.L,
                    lab.A + to.Mean.A - from.Mean.A,
                    lab.B + to.Mean.B - from.Mean.B);
                FromLab(adjusted, output, pixel);
            }
        }
        var after = Measure(output, targetRegions, targetMask, reference, referenceRegions, referenceMask)
            with { GuardApplied = true, GuardRegions = shifts.Select(zone => zone.ToString()).ToArray() };
        return new(output, after);
    }

    public static byte[] CalmBackground(byte[] pixels, byte[] figureMask, double amount)
    {
        if (pixels.Length != RgbFrame.ByteCount || figureMask.Length != 64 * 64)
            throw new ArgumentException("Portrait and figure mask dimensions must be 64x64.");
        amount = Math.Clamp(amount, 0, .5);
        if (amount <= 0) return pixels.ToArray();
        var background = Enumerable.Range(0, 64 * 64).Where(index => figureMask[index] == 0).ToArray();
        var meanL = background.Length == 0 ? 50 : background.Average(index => ToLab(pixels, index).L);
        var output = pixels.ToArray();
        for (var pixel = 0; pixel < 64 * 64; pixel++)
        {
            var strength = amount * (1 - figureMask[pixel] / 255d);
            if (strength <= 0) continue;
            var lab = ToLab(pixels, pixel);
            FromLab(new Lab(
                meanL + (lab.L - meanL) * (1 - strength),
                lab.A * (1 - strength),
                lab.B * (1 - strength)), output, pixel);
        }
        return output;
    }

    public static PortraitColorMatchMetrics Measure(byte[] value, PortraitRegions regions, byte[] mask,
        byte[] reference, PortraitRegions referenceRegions, byte[] referenceMask)
    {
        var hair = Summary(value, Indices(value, regions, mask, Zone.Hair));
        var refHair = Summary(reference, Indices(reference, referenceRegions, referenceMask, Zone.Hair));
        var top = Summary(value, Indices(value, regions, mask, Zone.InnerTop));
        var refTop = Summary(reference, Indices(reference, referenceRegions, referenceMask, Zone.InnerTop));
        var jacket = Summary(value, Indices(value, regions, mask, Zone.Jacket));
        var refJacket = Summary(reference, Indices(reference, referenceRegions, referenceMask, Zone.Jacket));
        return new(hair.Hue, refHair.Hue, hair.Lightness, refHair.Lightness, Math.Min(hair.Count, refHair.Count),
            top.Hue, refTop.Hue, top.Lightness, refTop.Lightness, Math.Min(top.Count, refTop.Count),
            jacket.Hue, refJacket.Hue, jacket.Lightness, refJacket.Lightness, Math.Min(jacket.Count, refJacket.Count));
    }

    public static PortraitColorHints Describe(byte[] reference, PortraitRegions regions, byte[] mask)
    {
        var hair = Summary(reference, Indices(reference, regions, mask, Zone.Hair));
        var jacket = Summary(reference, Indices(reference, regions, mask, Zone.Jacket));
        var top = Summary(reference, Indices(reference, regions, mask, Zone.InnerTop));
        return new(Describe(hair, hair: true), Describe(jacket, hair: false), Describe(top, hair: false));
    }

    public static byte[] VisualizeRegions(byte[] pixels, PortraitRegions regions, byte[] mask)
    {
        var output = new byte[RgbFrame.ByteCount];
        void Paint(Zone zone, byte r, byte g, byte b)
        {
            foreach (var pixel in Indices(pixels, regions, mask, zone))
            {
                var i = pixel * 3; output[i] = r; output[i + 1] = g; output[i + 2] = b;
            }
        }
        Paint(Zone.Background, 92, 72, 150);
        Paint(Zone.Jacket, 45, 110, 210);
        Paint(Zone.InnerTop, 230, 145, 35);
        Paint(Zone.Skin, 235, 160, 130);
        Paint(Zone.Hair, 245, 205, 65);
        return output;
    }

    internal static IReadOnlyList<int> RegionIndices(byte[] pixels, PortraitRegions regions, byte[] mask, string zone)
        => Indices(pixels, regions, mask, Enum.Parse<Zone>(zone, ignoreCase: true)).ToArray();

    private static IEnumerable<int> Indices(byte[] pixels, PortraitRegions regions, byte[] mask, Zone zone)
    {
        var face = regions.Face;
        var centerX = face.X + face.Width / 2d;
        var candidates = new List<int>();
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
        {
            var pixel = y * 64 + x;
            var figure = mask[pixel] >= 180;
            var skin = IsSkin(pixels, pixel);
            var hairArea = x >= face.X - face.Width * .70 && x < face.Right + face.Width * .25
                && y >= face.Y - face.Height * .30 && y < face.Y + face.Height * .72;
            var innerFace = x >= face.X + face.Width * .08 && x < face.Right - face.Width * .08
                && y >= face.Y + face.Height * .20 && y < face.Bottom;
            var hairSilhouette = hairArea && (x < face.X + face.Width * .42 || y < face.Y + face.Height * .20) && !innerFace;
            // The visible top sits between the open jacket lapels.  Its warm
            // chroma distinguishes amber/rust fabric from both blue denim and
            // skin, while the broad chest window accommodates three-quarter
            // poses without imposing a rectangular colour operation.
            var topWindow = y >= face.Bottom + face.Height * .35
                && Math.Abs(x + .5 - centerX) <= Math.Max(5, face.Width * .58);
            var topArea = topWindow && (IsWarmGarment(pixels, pixel)
                || (!skin && !IsBlueGarment(pixels, pixel)));
            var include = zone switch
            {
                Zone.Hair => figure && hairSilhouette,
                Zone.Skin => figure && skin && !hairSilhouette && !topArea && y < face.Bottom + face.Height * .45,
                Zone.InnerTop => figure && topArea,
                Zone.Jacket => figure && y >= face.Bottom - 3 && !skin && !topArea,
                Zone.Background => mask[pixel] <= 48,
                _ => false,
            };
            if (include) candidates.Add(pixel);
        }
        return candidates;
    }

    private static Statistics Stats(byte[] pixels, IReadOnlyList<int> indices)
    {
        var labs = indices.Select(index => ToLab(pixels, index)).ToArray();
        var mean = new Lab(labs.Average(x => x.L), labs.Average(x => x.A), labs.Average(x => x.B));
        return new(mean, labs.Length);
    }

    private static ColorSummary Summary(byte[] pixels, IEnumerable<int> values)
    {
        var indices = values.ToArray();
        if (indices.Length == 0) return new(0, 0, 0, 0, 0, 0, 0);
        double sin = 0, cos = 0, saturation = 0, lightness = 0, weight = 0;
        long red = 0, green = 0, blue = 0;
        foreach (var index in indices)
        {
            var i = index * 3; var r = pixels[i] / 255d; var g = pixels[i + 1] / 255d; var b = pixels[i + 2] / 255d;
            var (hue, sat) = Hue(r, g, b);
            var lab = ToLab(pixels, index);
            var hueWeight = Math.Max(.03, sat);
            sin += Math.Sin(hue * Math.PI / 180) * hueWeight; cos += Math.Cos(hue * Math.PI / 180) * hueWeight;
            weight += hueWeight; saturation += sat; lightness += lab.L;
            red += pixels[i]; green += pixels[i + 1]; blue += pixels[i + 2];
        }
        var meanHue = (Math.Atan2(sin / weight, cos / weight) * 180 / Math.PI + 360) % 360;
        return new(meanHue, lightness / indices.Length, saturation / indices.Length, indices.Length,
            (byte)(red / indices.Length), (byte)(green / indices.Length), (byte)(blue / indices.Length));
    }

    private static string Describe(ColorSummary color, bool hair)
    {
        var tone = color.Lightness switch { >= 78 => "light", < 35 => "dark", _ => "medium" };
        var name = color.Saturation < .10 ? "neutral" : color.Hue switch
        {
            < 18 or >= 345 => "red",
            < 42 => "amber",
            < 68 => "golden",
            < 155 => "green",
            < 205 => "teal",
            < 255 => "blue",
            < 300 => "violet",
            _ => "rose",
        };
        if (hair && name is ("amber" or "golden")) return "warm golden blonde";
        return $"{tone} {name} (RGB {color.R}, {color.G}, {color.B})";
    }

    private static bool IsSkin(byte[] pixels, int pixel)
    {
        var i = pixel * 3; var r = pixels[i]; var g = pixels[i + 1]; var b = pixels[i + 2];
        var cb = 128 - .168736 * r - .331264 * g + .5 * b;
        var cr = 128 + .5 * r - .418688 * g - .081312 * b;
        return r > 55 && Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > 10
            && cb is >= 72 and <= 138 && cr is >= 128 and <= 182;
    }

    private static bool IsWarmGarment(byte[] pixels, int pixel)
    {
        var i = pixel * 3;
        var (hue, saturation) = Hue(pixels[i] / 255d, pixels[i + 1] / 255d, pixels[i + 2] / 255d);
        return hue is >= 12 and <= 72 && saturation >= .22 && pixels[i] >= pixels[i + 2] * 1.15;
    }

    private static bool IsBlueGarment(byte[] pixels, int pixel)
    {
        var i = pixel * 3;
        var (hue, saturation) = Hue(pixels[i] / 255d, pixels[i + 1] / 255d, pixels[i + 2] / 255d);
        return hue is >= 175 and <= 255 && saturation >= .14;
    }

    private static (double Hue, double Saturation) Hue(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b)); var min = Math.Min(r, Math.Min(g, b)); var delta = max - min;
        if (delta < .0001) return (0, 0);
        var hue = max == r ? 60 * (((g - b) / delta) % 6) : max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
        if (hue < 0) hue += 360;
        return (hue, max <= 0 ? 0 : delta / max);
    }

    private static Lab ToLab(byte[] pixels, int pixel)
    {
        var i = pixel * 3;
        var r = Linear(pixels[i] / 255d); var g = Linear(pixels[i + 1] / 255d); var b = Linear(pixels[i + 2] / 255d);
        var x = (r * .4124564 + g * .3575761 + b * .1804375) / .95047;
        var y = r * .2126729 + g * .7151522 + b * .0721750;
        var z = (r * .0193339 + g * .1191920 + b * .9503041) / 1.08883;
        var fx = LabCurve(x); var fy = LabCurve(y); var fz = LabCurve(z);
        return new(116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    private static void FromLab(Lab lab, byte[] pixels, int pixel)
    {
        var fy = (Math.Clamp(lab.L, 0, 100) + 16) / 116;
        var fx = fy + Math.Clamp(lab.A, -128, 127) / 500;
        var fz = fy - Math.Clamp(lab.B, -128, 127) / 200;
        var x = .95047 * InverseLabCurve(fx); var y = InverseLabCurve(fy); var z = 1.08883 * InverseLabCurve(fz);
        var r = 3.2404542 * x - 1.5371385 * y - .4985314 * z;
        var g = -.9692660 * x + 1.8760108 * y + .0415560 * z;
        var b = .0556434 * x - .2040259 * y + 1.0572252 * z;
        var i = pixel * 3;
        pixels[i] = ToByte(r); pixels[i + 1] = ToByte(g); pixels[i + 2] = ToByte(b);
    }

    private static double Linear(double value) => value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
    private static double LabCurve(double value) => value > .008856 ? Math.Cbrt(value) : 7.787 * value + 16d / 116;
    private static double InverseLabCurve(double value)
    {
        var cube = value * value * value;
        return cube > .008856 ? cube : (value - 16d / 116) / 7.787;
    }
    private static byte ToByte(double linear)
    {
        linear = Math.Clamp(linear, 0, 1);
        var value = linear <= .0031308 ? 12.92 * linear : 1.055 * Math.Pow(linear, 1 / 2.4) - .055;
        return (byte)Math.Round(value * 255);
    }
}
