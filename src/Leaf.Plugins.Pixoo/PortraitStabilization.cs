namespace Leaf.Plugins.Pixoo;

public readonly record struct FrameAlignment(
    double ShiftX,
    double ShiftY,
    double Scale,
    double RotationDegrees,
    double Confidence,
    bool Reliable);

public sealed record PortraitClipProcessingResult(
    FrameLoop Loop,
    double FrozenBackgroundFraction,
    IReadOnlyList<FrameAlignment> Alignments);

internal sealed record PortraitSubjectMatte(
    IReadOnlyList<double> Alpha,
    double FrozenBackgroundFraction,
    bool UsedFallback);

/// <summary>
/// Estimates the global similarity transform of generated content relative to the source still.
/// A coarse-to-fine search compares clipped luminance-gradient magnitudes, making the fit
/// insensitive to global colour drift and robust to a locally moving subject region.
/// </summary>
public static class PortraitFrameAligner
{
    public static FrameAlignment EstimateTranslation(byte[] still, byte[] frame, int width, int height,
        int maximumShift = 12)
    {
        Validate(still, frame, width, height);
        if (maximumShift < 0) throw new ArgumentOutOfRangeException(nameof(maximumShift));
        var reference = GradientMagnitude(still, width, height);
        var generated = GradientMagnitude(frame, width, height);
        var identity = Score(reference, generated, width, height, 0, 0, 1);
        var best = new Candidate(0, 0, 1, identity);

        for (var scaleStep = -3; scaleStep <= 3; scaleStep++)
        {
            var scale = 1 + scaleStep * .01;
            for (var shiftY = -maximumShift; shiftY <= maximumShift; shiftY += 3)
            for (var shiftX = -maximumShift; shiftX <= maximumShift; shiftX += 3)
                best = Better(best, new(shiftX, shiftY, scale,
                    Score(reference, generated, width, height, shiftX, shiftY, scale)));
        }

        foreach (var (translationStep, scaleStep) in new[]
                 { (1.5, .005), (.75, .0025), (.35, .001), (.15, .0005) })
        {
            var centre = best;
            for (var sy = -1; sy <= 1; sy++)
            for (var sx = -1; sx <= 1; sx++)
            for (var ss = -1; ss <= 1; ss++)
            {
                var shiftX = Math.Clamp(centre.ShiftX + sx * translationStep, -maximumShift, maximumShift);
                var shiftY = Math.Clamp(centre.ShiftY + sy * translationStep, -maximumShift, maximumShift);
                var scale = Math.Clamp(centre.Scale + ss * scaleStep, .97, 1.03);
                best = Better(best, new(shiftX, shiftY, scale,
                    Score(reference, generated, width, height, shiftX, shiftY, scale)));
            }
        }

        var texture = reference.Average();
        var confidence = identity <= .001
            ? (Math.Abs(best.ShiftX) < .01 && Math.Abs(best.ShiftY) < .01 && Math.Abs(best.Scale - 1) < .0001 ? 1 : 0)
            : Math.Clamp((identity - best.Score) / identity, 0, 1);
        var isIdentity = Math.Abs(best.ShiftX) < .075 && Math.Abs(best.ShiftY) < .075 && Math.Abs(best.Scale - 1) < .00025;
        var reliable = texture >= 1 && (isIdentity || confidence >= .01);
        return reliable
            ? new(best.ShiftX, best.ShiftY, best.Scale, 0, confidence, true)
            : new(0, 0, 1, 0, confidence, false);
    }

    public static byte[] Align(byte[] still, byte[] frame, int width, int height, FrameAlignment alignment)
    {
        Validate(still, frame, width, height);
        if (!alignment.Reliable) return frame.ToArray();
        if (Math.Abs(alignment.ShiftX) < .001 && Math.Abs(alignment.ShiftY) < .001
            && Math.Abs(alignment.Scale - 1) < .00001 && Math.Abs(alignment.RotationDegrees) < .001)
            return frame.ToArray();
        var result = still.ToArray();
        var centreX = (width - 1) / 2d;
        var centreY = (height - 1) / 2d;
        var radians = alignment.RotationDegrees * Math.PI / 180;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var relativeX = (x - centreX) * alignment.Scale;
            var relativeY = (y - centreY) * alignment.Scale;
            var sourceX = centreX + relativeX * cosine - relativeY * sine + alignment.ShiftX;
            var sourceY = centreY + relativeX * sine + relativeY * cosine + alignment.ShiftY;
            if (sourceX < 0 || sourceX >= width - 1 || sourceY < 0 || sourceY >= height - 1) continue;
            var target = (y * width + x) * 3;
            for (var channel = 0; channel < 3; channel++)
                result[target + channel] = (byte)Math.Clamp(Math.Round(Sample(frame, width, height, sourceX, sourceY, channel)), 0, 255);
        }
        return result;
    }

    private static Candidate Better(Candidate current, Candidate candidate)
    {
        if (candidate.Score < current.Score - .0001) return candidate;
        if (Math.Abs(candidate.Score - current.Score) >= .0001) return current;
        var currentDistance = Math.Abs(current.ShiftX) + Math.Abs(current.ShiftY) + Math.Abs(current.Scale - 1) * 100;
        var candidateDistance = Math.Abs(candidate.ShiftX) + Math.Abs(candidate.ShiftY) + Math.Abs(candidate.Scale - 1) * 100;
        return candidateDistance < currentDistance ? candidate : current;
    }

    private static double Score(double[] reference, double[] frame, int width, int height,
        double shiftX, double shiftY, double scale)
    {
        const int stride = 6;
        const int margin = 18;
        var centreX = (width - 1) / 2d;
        var centreY = (height - 1) / 2d;
        double score = 0;
        var count = 0;
        for (var y = margin; y < height - margin; y += stride)
        for (var x = margin; x < width - margin; x += stride)
        {
            var sourceX = centreX + (x - centreX) * scale + shiftX;
            var sourceY = centreY + (y - centreY) * scale + shiftY;
            if (sourceX < 1 || sourceX >= width - 2 || sourceY < 1 || sourceY >= height - 2) continue;
            var difference = Math.Abs(reference[y * width + x] - Sample(frame, width, height, sourceX, sourceY));
            score += Math.Min(35, difference);
            count++;
        }
        return score / Math.Max(1, count);
    }

    private static double[] GradientMagnitude(byte[] pixels, int width, int height)
    {
        var luma = new double[width * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++) luma[y * width + x] = Luma(pixels, width, x, y);
        var result = new double[luma.Length];
        for (var y = 1; y < height - 1; y++)
        for (var x = 1; x < width - 1; x++)
        {
            var horizontal = luma[y * width + x + 1] - luma[y * width + x - 1];
            var vertical = luma[(y + 1) * width + x] - luma[(y - 1) * width + x];
            result[y * width + x] = Math.Sqrt(horizontal * horizontal + vertical * vertical);
        }
        return result;
    }

    private static double Luma(byte[] pixels, int width, int x, int y)
    {
        var index = (y * width + x) * 3;
        return pixels[index] * .2126 + pixels[index + 1] * .7152 + pixels[index + 2] * .0722;
    }

    private static double Sample(double[] values, int width, int height, double x, double y)
    {
        var x0 = Math.Clamp((int)Math.Floor(x), 0, width - 1);
        var y0 = Math.Clamp((int)Math.Floor(y), 0, height - 1);
        var x1 = Math.Min(width - 1, x0 + 1);
        var y1 = Math.Min(height - 1, y0 + 1);
        var fx = x - x0;
        var fy = y - y0;
        return values[y0 * width + x0] * (1 - fx) * (1 - fy)
            + values[y0 * width + x1] * fx * (1 - fy)
            + values[y1 * width + x0] * (1 - fx) * fy
            + values[y1 * width + x1] * fx * fy;
    }

    private static double Sample(byte[] values, int width, int height, double x, double y, int channel)
    {
        var x0 = Math.Clamp((int)Math.Floor(x), 0, width - 1);
        var y0 = Math.Clamp((int)Math.Floor(y), 0, height - 1);
        var x1 = Math.Min(width - 1, x0 + 1);
        var y1 = Math.Min(height - 1, y0 + 1);
        var fx = x - x0;
        var fy = y - y0;
        double At(int px, int py) => values[(py * width + px) * 3 + channel];
        return At(x0, y0) * (1 - fx) * (1 - fy)
            + At(x1, y0) * fx * (1 - fy)
            + At(x0, y1) * (1 - fx) * fy
            + At(x1, y1) * fx * fy;
    }

    private static void Validate(byte[] still, byte[] frame, int width, int height)
    {
        var expected = checked(width * height * 3);
        if (still.Length != expected || frame.Length != expected)
            throw new ArgumentException("Alignment RGB byte count does not match its dimensions.");
        if (width < 32 || height < 32) throw new ArgumentOutOfRangeException(nameof(width));
    }

    private readonly record struct Candidate(double ShiftX, double ShiftY, double Scale, double Score);
}

public static class PortraitPixelStabilizer
{
    public static PortraitClipProcessingResult Stabilize(
        IReadOnlyList<byte[]> frames,
        byte[] still,
        int durationMs,
        IReadOnlyList<FrameAlignment> alignments,
        double subjectMotionThreshold = 14,
        int subjectDilationPixels = 6,
        int featherPixels = 4)
    {
        if (frames.Count == 0) throw new ArgumentException("At least one frame is required.", nameof(frames));
        if (still.Length != RgbFrame.ByteCount || frames.Any(frame => frame.Length != still.Length))
            throw new ArgumentException("Pixel stabilization requires 64x64 RGB frames.");
        const int side = 64;
        var matte = BuildSubjectMatte(frames, still, subjectMotionThreshold, subjectDilationPixels, featherPixels);
        var stabilized = new List<byte[]>(frames.Count);
        foreach (var frame in frames)
        {
            var output = still.ToArray();
            for (var pixel = 0; pixel < side * side; pixel++)
            {
                var alpha = matte.Alpha[pixel];
                if (alpha <= 0) continue;
                var offset = pixel * 3;
                if (alpha >= 1)
                {
                    frame.AsSpan(offset, 3).CopyTo(output.AsSpan(offset, 3));
                }
                else
                {
                    for (var channel = 0; channel < 3; channel++)
                        output[offset + channel] = (byte)Math.Clamp(Math.Round(
                            frame[offset + channel] * alpha + still[offset + channel] * (1 - alpha)), 0, 255);
                }
            }
            stabilized.Add(output);
        }

        var closed = PortraitColorCorrector.CloseSeam(stabilized, still);
        var loop = new FrameLoop(closed.Select(frame => new RgbFrame(frame, durationMs)).ToArray()).Validate();
        return new(loop, matte.FrozenBackgroundFraction, alignments);
    }

    internal static double IdenticalFraction(byte[] frame, byte[] still)
    {
        var identical = 0;
        for (var index = 0; index < frame.Length; index += 3)
            if (frame[index] == still[index] && frame[index + 1] == still[index + 1] && frame[index + 2] == still[index + 2]) identical++;
        return identical / 4096d;
    }

    internal static PortraitSubjectMatte BuildSubjectMatte(IReadOnlyList<byte[]> frames, byte[] still,
        double threshold = 14, int dilationPixels = 6, int featherPixels = 4)
    {
        const int side = 64;
        if (frames.Count == 0 || frames.Any(frame => frame.Length != still.Length) || still.Length != RgbFrame.ByteCount)
            throw new ArgumentException("A subject matte requires 64x64 RGB frames and a matching still.");
        if (threshold <= 0) throw new ArgumentOutOfRangeException(nameof(threshold));
        if (dilationPixels < 0) throw new ArgumentOutOfRangeException(nameof(dilationPixels));
        if (featherPixels < 1) throw new ArgumentOutOfRangeException(nameof(featherPixels));

        var maximum = new double[side * side];
        foreach (var frame in frames)
        for (var pixel = 0; pixel < maximum.Length; pixel++)
        {
            var offset = pixel * 3;
            var distance = (Math.Abs(frame[offset] - still[offset])
                + Math.Abs(frame[offset + 1] - still[offset + 1])
                + Math.Abs(frame[offset + 2] - still[offset + 2])) / 3d;
            maximum[pixel] = Math.Max(maximum[pixel], distance);
        }

        var candidates = maximum.Select(value => value >= threshold).ToArray();
        var portraitPrior = new bool[side * side];
        AddEllipse(portraitPrior, side, side / 2, 24, 23, 29);
        AddEllipse(portraitPrior, side, side / 2, 57, 32, 22);
        for (var pixel = 0; pixel < candidates.Length; pixel++) candidates[pixel] &= portraitPrior[pixel];
        candidates = Erode(Dilate(candidates, side, 3), side, 3);
        FillHoles(candidates, side);
        var components = Components(candidates, side, 4).OrderByDescending(component => component.Count).ToArray();
        var selected = new bool[candidates.Length];
        if (components.Length > 0)
        {
            foreach (var pixel in components[0]) selected[pixel] = true;
            var primaryBounds = Bounds(components[0], side);
            foreach (var component in components.Skip(1))
            {
                var componentBounds = Bounds(component, side);
                if (BoxDistance(primaryBounds, componentBounds) > 8) continue;
                foreach (var pixel in component) selected[pixel] = true;
            }
        }

        var points = Enumerable.Range(0, selected.Length).Where(index => selected[index]).ToArray();
        var bounds = points.Length == 0 ? new ImageRect(0, 0, 0, 0) : Bounds(points, side);
        var fallback = points.Length < 80 || bounds.Width < 16 || bounds.Height < 16
            || points.Length > side * side * .25 || bounds.Width > 48 || bounds.Height > 56;
        var centreX = fallback ? side / 2 : bounds.X + bounds.Width / 2;
        centreX = Math.Clamp(centreX, 24, 40);
        var faceCentreY = fallback ? 23 : Math.Clamp(bounds.Y + bounds.Height / 2, 18, 30);

        bool[] subject;
        if (fallback)
        {
            subject = new bool[side * side];
        }
        else
        {
            subject = Erode(Dilate(selected, side, 4), side, 4);
            FillHoles(subject, side);
            subject = Dilate(subject, side, dilationPixels);
        }

        // The motion evidence locates the subject, but a portrait-safe silhouette guarantees that
        // tiny eye or mouth motion never becomes a moving island in an otherwise frozen face.
        AddEllipse(subject, side, centreX, faceCentreY, 19, 25);
        AddEllipse(subject, side, centreX, 57, 29, 19);
        FillHoles(subject, side);

        var alpha = new double[subject.Length];
        for (var pixel = 0; pixel < subject.Length; pixel++)
        {
            if (subject[pixel]) { alpha[pixel] = 1; continue; }
            var x = pixel % side;
            var y = pixel / side;
            var distance = DistanceTo(subject, side, x, y, featherPixels);
            if (distance > featherPixels) continue;
            var t = distance / (featherPixels + 1d);
            alpha[pixel] = 1 - t * t * (3 - 2 * t);
        }
        var frozen = alpha.Count(value => value <= 0) / (double)alpha.Length;
        return new(alpha, frozen, fallback);
    }

    private static void AddEllipse(bool[] mask, int side, int centreX, int centreY, int radiusX, int radiusY)
    {
        for (var y = 0; y < side; y++)
        for (var x = 0; x < side; x++)
        {
            var dx = (x - centreX) / (double)radiusX;
            var dy = (y - centreY) / (double)radiusY;
            if (dx * dx + dy * dy <= 1) mask[y * side + x] = true;
        }
    }

    private static int DistanceTo(bool[] mask, int side, int x, int y, int maximum)
    {
        var best = maximum + 1d;
        for (var dy = -maximum; dy <= maximum; dy++)
        for (var dx = -maximum; dx <= maximum; dx++)
        {
            var nx = x + dx;
            var ny = y + dy;
            if (nx < 0 || nx >= side || ny < 0 || ny >= side || !mask[ny * side + nx]) continue;
            best = Math.Min(best, Math.Sqrt(dx * dx + dy * dy));
        }
        return (int)Math.Ceiling(best);
    }

    private static bool[] Dilate(bool[] mask, int side, int radius)
    {
        if (radius == 0) return mask.ToArray();
        var result = new bool[mask.Length];
        for (var y = 0; y < side; y++)
        for (var x = 0; x < side; x++)
        for (var dy = -radius; dy <= radius && !result[y * side + x]; dy++)
        for (var dx = -radius; dx <= radius; dx++)
        {
            var nx = x + dx;
            var ny = y + dy;
            if (dx * dx + dy * dy <= radius * radius && nx >= 0 && nx < side && ny >= 0 && ny < side
                && mask[ny * side + nx]) { result[y * side + x] = true; break; }
        }
        return result;
    }

    private static bool[] Erode(bool[] mask, int side, int radius)
    {
        var result = new bool[mask.Length];
        for (var y = 0; y < side; y++)
        for (var x = 0; x < side; x++)
        {
            var keep = true;
            for (var dy = -radius; dy <= radius && keep; dy++)
            for (var dx = -radius; dx <= radius; dx++)
            {
                if (dx * dx + dy * dy > radius * radius) continue;
                var nx = x + dx;
                var ny = y + dy;
                if (nx < 0 || nx >= side || ny < 0 || ny >= side || !mask[ny * side + nx]) { keep = false; break; }
            }
            result[y * side + x] = keep;
        }
        return result;
    }

    private static void FillHoles(bool[] mask, int side)
    {
        var outside = new bool[mask.Length];
        var queue = new Queue<int>();
        void Seed(int pixel) { if (!mask[pixel] && !outside[pixel]) { outside[pixel] = true; queue.Enqueue(pixel); } }
        for (var value = 0; value < side; value++) { Seed(value); Seed((side - 1) * side + value); Seed(value * side); Seed(value * side + side - 1); }
        while (queue.Count > 0)
        {
            var pixel = queue.Dequeue();
            var x = pixel % side;
            var y = pixel / side;
            foreach (var (dx, dy) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
            {
                var nx = x + dx;
                var ny = y + dy;
                if (nx < 0 || nx >= side || ny < 0 || ny >= side) continue;
                var next = ny * side + nx;
                if (!mask[next] && !outside[next]) { outside[next] = true; queue.Enqueue(next); }
            }
        }
        for (var pixel = 0; pixel < mask.Length; pixel++) if (!mask[pixel] && !outside[pixel]) mask[pixel] = true;
    }

    private static ImageRect Bounds(IReadOnlyList<int> pixels, int side)
    {
        var minX = side; var minY = side; var maxX = 0; var maxY = 0;
        foreach (var pixel in pixels)
        {
            var x = pixel % side; var y = pixel / side;
            minX = Math.Min(minX, x); minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
        }
        return new(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    private static int BoxDistance(ImageRect left, ImageRect right)
    {
        var dx = Math.Max(0, Math.Max(left.X - right.Right, right.X - left.Right));
        var dy = Math.Max(0, Math.Max(left.Y - right.Bottom, right.Y - left.Bottom));
        return Math.Max(dx, dy);
    }

    private static IReadOnlyList<List<int>> Components(bool[] mask, int side, int minimum)
    {
        var result = new List<List<int>>();
        var seen = new bool[mask.Length];
        var queue = new Queue<int>();
        for (var start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || seen[start]) continue;
            var component = new List<int>();
            queue.Enqueue(start);
            seen[start] = true;
            while (queue.Count > 0)
            {
                var pixel = queue.Dequeue();
                component.Add(pixel);
                var x = pixel % side;
                var y = pixel / side;
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    var nx = x + dx;
                    var ny = y + dy;
                    if (nx < 0 || nx >= side || ny < 0 || ny >= side) continue;
                    var next = ny * side + nx;
                    if (mask[next] && !seen[next]) { seen[next] = true; queue.Enqueue(next); }
                }
            }
            if (component.Count >= minimum) result.Add(component);
        }
        return result;
    }

}
