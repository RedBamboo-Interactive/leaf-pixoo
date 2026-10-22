namespace Leaf.Plugins.Pixoo;

public readonly record struct FrameAlignment(int ShiftX, int ShiftY, double Confidence, bool Reliable);

public sealed record PortraitClipProcessingResult(
    FrameLoop Loop,
    double IdenticalFractionMinimum,
    double IdenticalFractionMedian,
    IReadOnlyList<FrameAlignment> Alignments);

/// <summary>
/// Estimates the global displacement of generated content relative to the source still. The
/// search compares clipped luminance gradients, which makes it insensitive to Wan's global
/// colour/exposure drift and robust to a small locally moving subject region.
/// </summary>
public static class PortraitFrameAligner
{
    public static FrameAlignment EstimateTranslation(byte[] still, byte[] frame, int width, int height,
        int maximumShift = 12)
    {
        Validate(still, frame, width, height);
        if (maximumShift < 0) throw new ArgumentOutOfRangeException(nameof(maximumShift));
        const int stride = 4;
        var margin = maximumShift + 3;
        var bestScore = double.MaxValue;
        var zeroScore = double.MaxValue;
        var bestX = 0;
        var bestY = 0;
        double texture = 0;
        var textureCount = 0;

        for (var dy = -maximumShift; dy <= maximumShift; dy++)
        for (var dx = -maximumShift; dx <= maximumShift; dx++)
        {
            double score = 0;
            var count = 0;
            for (var y = margin; y < height - margin; y += stride)
            for (var x = margin; x < width - margin; x += stride)
            {
                var referenceGradient = Gradient(still, width, x, y);
                var frameGradient = Gradient(frame, width, x + dx, y + dy);
                // A mouth or eye moving should not outweigh the static majority.
                score += Math.Min(80, Math.Abs(referenceGradient.X - frameGradient.X))
                    + Math.Min(80, Math.Abs(referenceGradient.Y - frameGradient.Y));
                count += 2;
                if (dx == 0 && dy == 0)
                {
                    texture += Math.Abs(referenceGradient.X) + Math.Abs(referenceGradient.Y);
                    textureCount += 2;
                }
            }
            score /= Math.Max(1, count);
            if (dx == 0 && dy == 0) zeroScore = score;
            if (score < bestScore - .0001
                || Math.Abs(score - bestScore) < .0001 && Math.Abs(dx) + Math.Abs(dy) < Math.Abs(bestX) + Math.Abs(bestY))
            {
                bestScore = score;
                bestX = dx;
                bestY = dy;
            }
        }

        var meanTexture = texture / Math.Max(1, textureCount);
        var confidence = zeroScore <= .001
            ? (bestX == 0 && bestY == 0 ? 1 : 0)
            : Math.Clamp((zeroScore - bestScore) / zeroScore, 0, 1);
        var reliable = bestX == 0 && bestY == 0
            ? meanTexture >= 1
            : meanTexture >= 1 && confidence >= .03;
        return reliable
            ? new(bestX, bestY, confidence, true)
            : new(0, 0, confidence, false);
    }

    public static byte[] Align(byte[] still, byte[] frame, int width, int height, FrameAlignment alignment)
    {
        Validate(still, frame, width, height);
        if (!alignment.Reliable || alignment.ShiftX == 0 && alignment.ShiftY == 0) return frame.ToArray();
        var result = still.ToArray();
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var sourceX = x + alignment.ShiftX;
            var sourceY = y + alignment.ShiftY;
            if (sourceX < 0 || sourceX >= width || sourceY < 0 || sourceY >= height) continue;
            var source = (sourceY * width + sourceX) * 3;
            var target = (y * width + x) * 3;
            frame.AsSpan(source, 3).CopyTo(result.AsSpan(target, 3));
        }
        return result;
    }

    private static (double X, double Y) Gradient(byte[] pixels, int width, int x, int y)
        => (Luma(pixels, width, x + 1, y) - Luma(pixels, width, x - 1, y),
            Luma(pixels, width, x, y + 1) - Luma(pixels, width, x, y - 1));

    private static double Luma(byte[] pixels, int width, int x, int y)
    {
        var index = (y * width + x) * 3;
        return pixels[index] * .2126 + pixels[index + 1] * .7152 + pixels[index + 2] * .0722;
    }

    private static void Validate(byte[] still, byte[] frame, int width, int height)
    {
        var expected = checked(width * height * 3);
        if (still.Length != expected || frame.Length != expected)
            throw new ArgumentException("Alignment RGB byte count does not match its dimensions.");
        if (width < 32 || height < 32) throw new ArgumentOutOfRangeException(nameof(width));
    }
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
        var dynamic = new bool[count];
        for (var pixel = 0; pixel < count; pixel++)
            dynamic[pixel] = distances.Max(values => values[pixel]) >= staticThreshold;

        var raw = distances.Select(values => values.Select((distance, pixel) => dynamic[pixel] && distance >= motionThreshold).ToArray()).ToArray();
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
            var feather = Dilate(temporal, dynamic, side);
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
        var seen = new bool[mask.Length];
        var queue = new Queue<int>();
        var component = new List<int>();
        for (var start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || seen[start]) continue;
            queue.Enqueue(start);
            seen[start] = true;
            component.Clear();
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
            if (component.Count < minimum) foreach (var pixel in component) mask[pixel] = false;
        }
    }
}
