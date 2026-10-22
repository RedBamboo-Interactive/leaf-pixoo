namespace Leaf.Plugins.Pixoo;

internal readonly record struct GifPaletteColor(byte R, byte G, byte B);

internal sealed record GifPaletteResult(
    IReadOnlyList<byte[]> Frames,
    IReadOnlyList<GifPaletteColor> Palette,
    IReadOnlyList<byte[]> Indices);

/// <summary>
/// Quantizes a complete loop once, then keeps the previous palette entry when a small RGB change
/// would otherwise hop between neighbouring entries. The resulting RGB frames contain no more than
/// 256 colours, so ffmpeg's global palette pass preserves the stabilized choices.
/// </summary>
internal static class GifPaletteStabilizer
{
    public static GifPaletteResult Stabilize(FrameLoop loop, double hysteresisThreshold = 10,
        int maximumColors = 256)
    {
        loop.Validate();
        if (hysteresisThreshold < 0) throw new ArgumentOutOfRangeException(nameof(hysteresisThreshold));
        if (maximumColors is < 2 or > 256) throw new ArgumentOutOfRangeException(nameof(maximumColors));
        var histogram = new Dictionary<int, int>();
        foreach (var frame in loop.Frames)
        for (var offset = 0; offset < frame.Pixels.Length; offset += 3)
        {
            var key = Key(frame.Pixels[offset], frame.Pixels[offset + 1], frame.Pixels[offset + 2]);
            histogram[key] = histogram.GetValueOrDefault(key) + 1;
        }
        var palette = BuildPalette(histogram, maximumColors);
        var cache = new Dictionary<int, byte>();
        var frames = new List<byte[]>(loop.Frames.Count);
        var indices = new List<byte[]>(loop.Frames.Count);
        byte[]? previousIndices = null;
        byte[]? previousSource = null;
        for (var frameIndex = 0; frameIndex < loop.Frames.Count; frameIndex++)
        {
            var source = loop.Frames[frameIndex].Pixels;
            var mapped = new byte[4096];
            var output = new byte[RgbFrame.ByteCount];
            var forceExactMapping = frameIndex == 0 || frameIndex == loop.Frames.Count - 1;
            for (var pixel = 0; pixel < mapped.Length; pixel++)
            {
                var offset = pixel * 3;
                var key = Key(source[offset], source[offset + 1], source[offset + 2]);
                if (!cache.TryGetValue(key, out var index))
                {
                    index = Nearest(palette, source[offset], source[offset + 1], source[offset + 2]);
                    cache[key] = index;
                }
                if (!forceExactMapping && previousIndices is not null && previousSource is not null
                    && index != previousIndices[pixel]
                    && MeanDifference(source, previousSource, offset) <= hysteresisThreshold)
                    index = previousIndices[pixel];
                mapped[pixel] = index;
                var colour = palette[index];
                output[offset] = colour.R;
                output[offset + 1] = colour.G;
                output[offset + 2] = colour.B;
            }
            indices.Add(mapped);
            frames.Add(output);
            previousIndices = mapped;
            previousSource = source;
        }
        return new(frames, palette, indices);
    }

    private static IReadOnlyList<GifPaletteColor> BuildPalette(IReadOnlyDictionary<int, int> histogram,
        int maximumColors)
    {
        var colours = histogram.Select(item => new WeightedColour(
            (byte)(item.Key >> 16), (byte)(item.Key >> 8), (byte)item.Key, item.Value)).ToList();
        if (colours.Count <= maximumColors)
            return colours.OrderBy(value => Key(value.R, value.G, value.B))
                .Select(value => new GifPaletteColor(value.R, value.G, value.B)).ToArray();
        var buckets = new List<List<WeightedColour>> { colours };
        while (buckets.Count < maximumColors)
        {
            var selected = buckets.Select((bucket, index) => (bucket, index, score: Score(bucket)))
                .Where(item => item.bucket.Count > 1).OrderByDescending(item => item.score).FirstOrDefault();
            if (selected.bucket is null) break;
            var channel = SplitChannel(selected.bucket);
            selected.bucket.Sort((left, right) => Channel(left, channel).CompareTo(Channel(right, channel)));
            var half = selected.bucket.Sum(value => value.Count) / 2;
            var total = 0;
            var split = 1;
            for (; split < selected.bucket.Count; split++)
            {
                total += selected.bucket[split - 1].Count;
                if (total >= half) break;
            }
            split = Math.Clamp(split, 1, selected.bucket.Count - 1);
            buckets[selected.index] = selected.bucket.Take(split).ToList();
            buckets.Add(selected.bucket.Skip(split).ToList());
        }
        return buckets.Select(Average).ToArray();
    }

    private static long Score(IReadOnlyList<WeightedColour> colours)
    {
        var range = Math.Max(Range(colours, 0), Math.Max(Range(colours, 1), Range(colours, 2)));
        return (long)range * colours.Sum(value => value.Count);
    }

    private static int SplitChannel(IReadOnlyList<WeightedColour> colours)
    {
        var ranges = new[] { Range(colours, 0), Range(colours, 1), Range(colours, 2) };
        return Array.IndexOf(ranges, ranges.Max());
    }

    private static int Range(IReadOnlyList<WeightedColour> colours, int channel)
        => colours.Max(value => Channel(value, channel)) - colours.Min(value => Channel(value, channel));

    private static byte Channel(WeightedColour colour, int channel)
        => channel switch { 0 => colour.R, 1 => colour.G, _ => colour.B };

    private static GifPaletteColor Average(IReadOnlyList<WeightedColour> colours)
    {
        var count = colours.Sum(value => (long)value.Count);
        return new((byte)Math.Round(colours.Sum(value => value.R * (long)value.Count) / (double)count),
            (byte)Math.Round(colours.Sum(value => value.G * (long)value.Count) / (double)count),
            (byte)Math.Round(colours.Sum(value => value.B * (long)value.Count) / (double)count));
    }

    private static byte Nearest(IReadOnlyList<GifPaletteColor> palette, byte red, byte green, byte blue)
    {
        var best = 0;
        var bestDistance = long.MaxValue;
        for (var index = 0; index < palette.Count; index++)
        {
            var colour = palette[index];
            var dr = red - colour.R;
            var dg = green - colour.G;
            var db = blue - colour.B;
            var distance = (long)dr * dr + (long)dg * dg + (long)db * db;
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = index;
        }
        return (byte)best;
    }

    private static double MeanDifference(byte[] current, byte[] previous, int offset)
        => (Math.Abs(current[offset] - previous[offset]) + Math.Abs(current[offset + 1] - previous[offset + 1])
            + Math.Abs(current[offset + 2] - previous[offset + 2])) / 3d;

    private static int Key(byte red, byte green, byte blue) => red << 16 | green << 8 | blue;

    private readonly record struct WeightedColour(byte R, byte G, byte B, int Count);
}
