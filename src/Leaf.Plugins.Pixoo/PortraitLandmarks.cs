using SkiaSharp;

namespace Leaf.Plugins.Pixoo;

public readonly record struct PixelPoint(int X, int Y);

public sealed record PortraitLandmarks(
    string Detector,
    int SourceWidth,
    int SourceHeight,
    PixelPoint[] FaceOutline,
    PixelPoint[] LeftEyeOutline,
    PixelPoint[] RightEyeOutline,
    PixelPoint[] UpperLip,
    PixelPoint[] LowerLip)
{
    public PixelPoint[] LipLine => UpperLip.Zip(LowerLip,
        (upper, lower) => new PixelPoint(upper.X, (int)Math.Round((upper.Y + lower.Y) / 2d))).ToArray();

    public PixelRegion LeftEyeBox => Bounds(LeftEyeOutline, 1);
    public PixelRegion RightEyeBox => Bounds(RightEyeOutline, 1);
    public PixelRegion MouthBox => Bounds(UpperLip.Concat(LowerLip), 1);

    public PortraitRegions ApplyTo(PortraitRegions scaffold)
        => scaffold with { LeftEye = LeftEyeBox, RightEye = RightEyeBox, Mouth = MouthBox };

    private static PixelRegion Bounds(IEnumerable<PixelPoint> points, int padding)
    {
        var values = points.ToArray();
        if (values.Length == 0) return new PixelRegion(0, 0, 1, 1);
        var left = values.Min(point => point.X) - padding;
        var top = values.Min(point => point.Y) - padding;
        var right = values.Max(point => point.X) + padding + 1;
        var bottom = values.Max(point => point.Y) + padding + 1;
        return new PixelRegion(left, top, right - left, bottom - top).Clamp();
    }
}

/// <summary>
/// Parses the feature-coloured output of the installed
/// comfyui_controlnet_aux MediaPipe-FaceMeshPreprocessor. No feature position
/// is inferred from the 64x64 portrait.
/// </summary>
public static class FaceMeshLandmarkDetector
{
    public const string DetectorName = "comfyui_controlnet_aux/MediaPipe-FaceMeshPreprocessor";
    private static readonly SKColor FaceColor = new(10, 200, 10);
    private static readonly SKColor MouthColor = new(10, 180, 10);
    private static readonly SKColor LeftEyeColor = new(180, 200, 10);
    private static readonly SKColor LeftIrisColor = new(250, 200, 10);
    private static readonly SKColor RightEyeColor = new(10, 200, 180);
    private static readonly SKColor RightIrisColor = new(10, 200, 250);

    public static PortraitLandmarks Detect(byte[] faceMeshPng)
    {
        using var image = SKBitmap.Decode(faceMeshPng)
            ?? throw new InvalidDataException("Face landmark output could not be decoded.");
        if (image.Width < 512 || image.Height < 512)
            throw new InvalidDataException("Face landmark output must retain the high-resolution redraw geometry.");

        var face = Pixels(image, FaceColor);
        var mouth = Pixels(image, MouthColor);
        var leftEye = Pixels(image, LeftEyeColor, LeftIrisColor);
        var rightEye = Pixels(image, RightEyeColor, RightIrisColor);
        if (face.Count < 100 || leftEye.Count < 20 || rightEye.Count < 20 || mouth.Count < 30)
            throw new InvalidDataException("MediaPipe face landmarks were not found for the redraw.");

        var faceOutline = Hull(MapDistinct(face, image.Width, image.Height));
        var leftOutline = Hull(MapDistinct(leftEye, image.Width, image.Height));
        var rightOutline = Hull(MapDistinct(rightEye, image.Width, image.Height));
        var (upperLip, lowerLip) = LipLines(mouth, image.Width, image.Height);
        if (faceOutline.Length < 3 || leftOutline.Length < 3 || rightOutline.Length < 3
            || upperLip.Length < 3 || lowerLip.Length != upperLip.Length)
            throw new InvalidDataException("MediaPipe face landmarks were incomplete.");

        return new(DetectorName, image.Width, image.Height, faceOutline, leftOutline, rightOutline, upperLip, lowerLip);
    }

    private static List<SKPointI> Pixels(SKBitmap image, params SKColor[] colors)
    {
        var accepted = colors.Select(color => (color.Red, color.Green, color.Blue)).ToHashSet();
        var result = new List<SKPointI>();
        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
        {
            var pixel = image.GetPixel(x, y);
            if (accepted.Contains((pixel.Red, pixel.Green, pixel.Blue))) result.Add(new SKPointI(x, y));
        }
        return result;
    }

    private static PixelPoint[] MapDistinct(IEnumerable<SKPointI> points, int width, int height)
        => points.Select(point => Map(point, width, height)).Distinct().ToArray();

    private static PixelPoint Map(SKPointI point, int width, int height)
        => new(Math.Clamp((int)Math.Round(point.X * 63d / (width - 1)), 0, 63),
            Math.Clamp((int)Math.Round(point.Y * 63d / (height - 1)), 0, 63));

    private static (PixelPoint[] Upper, PixelPoint[] Lower) LipLines(
        IEnumerable<SKPointI> mouth, int width, int height)
    {
        var mapped = mouth.Select(point => Map(point, width, height)).Distinct().ToArray();
        var groups = mapped.GroupBy(point => point.X).OrderBy(group => group.Key).ToArray();
        return (groups.Select(group => new PixelPoint(group.Key, group.Min(point => point.Y))).ToArray(),
            groups.Select(group => new PixelPoint(group.Key, group.Max(point => point.Y))).ToArray());
    }

    private static PixelPoint[] Hull(IEnumerable<PixelPoint> points)
    {
        var sorted = points.Distinct().OrderBy(point => point.X).ThenBy(point => point.Y).ToArray();
        if (sorted.Length <= 2) return sorted;
        static int Cross(PixelPoint origin, PixelPoint a, PixelPoint b)
            => (a.X - origin.X) * (b.Y - origin.Y) - (a.Y - origin.Y) * (b.X - origin.X);
        var lower = new List<PixelPoint>();
        foreach (var point in sorted)
        {
            while (lower.Count >= 2 && Cross(lower[^2], lower[^1], point) <= 0) lower.RemoveAt(lower.Count - 1);
            lower.Add(point);
        }
        var upper = new List<PixelPoint>();
        foreach (var point in sorted.Reverse())
        {
            while (upper.Count >= 2 && Cross(upper[^2], upper[^1], point) <= 0) upper.RemoveAt(upper.Count - 1);
            upper.Add(point);
        }
        return lower.Take(lower.Count - 1).Concat(upper.Take(upper.Count - 1)).ToArray();
    }
}
