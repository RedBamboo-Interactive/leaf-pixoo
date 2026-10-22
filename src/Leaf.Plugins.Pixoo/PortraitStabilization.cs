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
    double IdenticalFractionMinimum,
    double IdenticalFractionMedian,
    IReadOnlyList<FrameAlignment> Alignments);

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
        double staticThreshold = 8,
        double motionThreshold = 5,
        int minimumComponentPixels = 3)
    {
        if (frames.Count == 0) throw new ArgumentException("At least one frame is required.", nameof(frames));
        if (still.Length != RgbFrame.ByteCount || frames.Any(frame => frame.Length != still.Length))
            throw new ArgumentException("Pixel stabilization requires 64x64 RGB frames.");
        const int side = 64;
        const int count = side * side;
        var distances = frames.Select(frame => Distances(frame, still)).ToArray();
        var subject = BuildSubjectRegion(distances, staticThreshold, side);

        var raw = distances.Select(values => values.Select((distance, pixel) => subject[pixel] && distance >= motionThreshold).ToArray()).ToArray();
        var stabilized = new List<byte[]>(frames.Count);
        for (var frameIndex = 0; frameIndex < frames.Count; frameIndex++)
        {
            var temporal = new bool[count];
            for (var pixel = 0; pixel < count; pixel++)
            {
                if (!raw[frameIndex][pixel]) continue;
                var x = pixel % side;
                var y = pixel / side;
                temporal[pixel] = HasNeighbour(raw, frameIndex - 1, x, y, side)
                    || HasNeighbour(raw, frameIndex + 1, x, y, side);
            }
            RemoveSmallComponents(temporal, side, minimumComponentPixels);
            var feather = Dilate(temporal, subject, side);
            var output = still.ToArray();
            for (var pixel = 0; pixel < count; pixel++)
            {
                if (!temporal[pixel] && !feather[pixel]) continue;
                var offset = pixel * 3;
                if (temporal[pixel])
                {
                    frames[frameIndex].AsSpan(offset, 3).CopyTo(output.AsSpan(offset, 3));
                }
                else
                {
                    for (var channel = 0; channel < 3; channel++)
                        output[offset + channel] = (byte)Math.Round((frames[frameIndex][offset + channel] + still[offset + channel]) / 2d);
                }
            }
            stabilized.Add(output);
        }

        var closed = PortraitColorCorrector.CloseSeam(stabilized, still);
        var fractions = closed.Select(frame => IdenticalFraction(frame, still)).OrderBy(value => value).ToArray();
        var median = fractions.Length % 2 == 1
            ? fractions[fractions.Length / 2]
            : (fractions[fractions.Length / 2 - 1] + fractions[fractions.Length / 2]) / 2;
        var loop = new FrameLoop(closed.Select(frame => new RgbFrame(frame, durationMs)).ToArray()).Validate();
        return new(loop, fractions[0], median, alignments);
    }

    internal static double IdenticalFraction(byte[] frame, byte[] still)
    {
        var identical = 0;
        for (var index = 0; index < frame.Length; index += 3)
            if (frame[index] == still[index] && frame[index + 1] == still[index + 1] && frame[index + 2] == still[index + 2]) identical++;
        return identical / 4096d;
    }

    private static double[] Distances(byte[] frame, byte[] still)
    {
        var result = new double[4096];
        for (var pixel = 0; pixel < result.Length; pixel++)
        {
            var offset = pixel * 3;
            result[pixel] = (Math.Abs(frame[offset] - still[offset])
                + Math.Abs(frame[offset + 1] - still[offset + 1])
                + Math.Abs(frame[offset + 2] - still[offset + 2])) / 3d;
        }
        return result;
    }

    internal static bool[] BuildSubjectRegion(IReadOnlyList<double[]> distances, double threshold, int side)
    {
        var candidates = new bool[side * side];
        for (var pixel = 0; pixel < candidates.Length; pixel++)
        {
            var activeFrames = 0;
            double maximum = 0;
            foreach (var frame in distances)
            {
                maximum = Math.Max(maximum, frame[pixel]);
                if (frame[pixel] >= threshold) activeFrames++;
            }
            candidates[pixel] = maximum >= threshold * 1.75 || activeFrames >= 2;
        }

        var components = Components(candidates, side, 3).OrderByDescending(component => component.Count).ToArray();
        if (components.Length == 0) return candidates;
        var selected = new bool[candidates.Length];
        foreach (var pixel in components[0]) selected[pixel] = true;
        return Dilate(Dilate(selected, Enumerable.Repeat(true, selected.Length).ToArray(), side),
            Enumerable.Repeat(true, selected.Length).ToArray(), side);
    }

    private static bool HasNeighbour(IReadOnlyList<bool[]> masks, int frame, int x, int y, int side)
    {
        if (frame < 0 || frame >= masks.Count) return false;
        for (var dy = -1; dy <= 1; dy++)
        for (var dx = -1; dx <= 1; dx++)
        {
            var nx = x + dx;
            var ny = y + dy;
            if (nx >= 0 && nx < side && ny >= 0 && ny < side && masks[frame][ny * side + nx]) return true;
        }
        return false;
    }

    private static bool[] Dilate(bool[] mask, bool[] dynamic, int side)
    {
        var result = new bool[mask.Length];
        for (var y = 0; y < side; y++)
        for (var x = 0; x < side; x++)
        {
            var pixel = y * side + x;
            if (mask[pixel] || !dynamic[pixel]) continue;
            for (var dy = -1; dy <= 1 && !result[pixel]; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                var nx = x + dx;
                var ny = y + dy;
                if (nx >= 0 && nx < side && ny >= 0 && ny < side && mask[ny * side + nx]) { result[pixel] = true; break; }
            }
        }
        return result;
    }

    private static void RemoveSmallComponents(bool[] mask, int side, int minimum)
    {
        foreach (var component in Components(mask, side, 1).Where(component => component.Count < minimum))
            foreach (var pixel in component) mask[pixel] = false;
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
