namespace Leaf.Plugins.Pixoo.Tests;

public sealed class PixooGifWriterTests
{
    private static readonly GifPaletteColor[] Palette =
        Enumerable.Range(0, 256).Select(i => new GifPaletteColor((byte)i, (byte)(255 - i), (byte)(i * 7))).ToArray();

    [Theory]
    [InlineData(0)]      // flat
    [InlineData(1)]      // noisy: exercises code-width growth and the 4096-entry clear
    [InlineData(2)]      // gradient
    public void Lzw_round_trips_exactly(int kind)
    {
        var random = new Random(7);
        var indices = Enumerable.Range(0, 4096).Select(i => (byte)(kind switch
        {
            0 => 42,
            1 => random.Next(256),
            _ => (i % 64) * 4,
        })).ToArray();
        Assert.Equal(indices, Decode(PixooGifWriter.Lzw(indices), 4096));
    }

    [Fact]
    public void Changed_frames_are_full_canvas_and_repeats_are_one_corner_pixel()
    {
        var a = Enumerable.Repeat((byte)3, 4096).ToArray();
        var b = a.ToArray();
        for (var i = 1500; i < 1600; i++) b[i] = 9; // localised change, like a moving mouth
        var gif = PixooGifWriter.Write(Palette, [a, a, b, b, b, a], 100);

        var frames = Frames(gif);
        Assert.Equal(6, frames.Count);
        Assert.Equal([(0, 0, 64, 64), (63, 63, 1, 1), (0, 0, 64, 64), (63, 63, 1, 1), (63, 63, 1, 1), (0, 0, 64, 64)],
            frames.Select(f => (f.Left, f.Top, f.Width, f.Height)).ToArray());
        Assert.All(frames, f => Assert.Equal(1, f.Disposal));
        Assert.Equal(b, Decode(frames[2].Data, 4096));
        Assert.Equal("GIF89a", System.Text.Encoding.ASCII.GetString(gif, 0, 6));
        Assert.Equal(0x3B, gif[^1]);
    }

    private sealed record Frame(int Left, int Top, int Width, int Height, int Disposal, byte[] Data);

    private static List<Frame> Frames(byte[] gif)
    {
        var position = 13 + 256 * 3;
        var frames = new List<Frame>();
        var disposal = 0;
        while (gif[position] != 0x3B)
        {
            if (gif[position] == 0x21)
            {
                if (gif[position + 1] == 0xF9) disposal = (gif[position + 3] >> 2) & 7;
                position += 2;
                while (gif[position] != 0) position += gif[position] + 1;
                position++;
                continue;
            }
            int U16(int at) => gif[at] | (gif[at + 1] << 8);
            var left = U16(position + 1); var top = U16(position + 3);
            var width = U16(position + 5); var height = U16(position + 7);
            position += 10; // descriptor
            position++;     // LZW minimum code size
            var data = new List<byte>();
            while (gif[position] != 0) { data.AddRange(gif.AsSpan(position + 1, gif[position]).ToArray()); position += gif[position] + 1; }
            position++;
            frames.Add(new(left, top, width, height, disposal, data.ToArray()));
        }
        return frames;
    }

    private static byte[] Decode(byte[] data, int expected)
    {
        const int clear = 256, end = 257;
        var output = new List<byte>(expected);
        var table = new List<byte[]>();
        void Reset() { table.Clear(); for (var i = 0; i < 256; i++) table.Add([(byte)i]); table.Add([]); table.Add([]); }
        Reset();
        var size = 9; var bit = 0; byte[]? previous = null;
        while (true)
        {
            var code = 0;
            for (var i = 0; i < size; i++, bit++) code |= ((data[bit >> 3] >> (bit & 7)) & 1) << i;
            if (code == clear) { Reset(); size = 9; previous = null; continue; }
            if (code == end) break;
            byte[] entry = code < table.Count ? table[code] : [.. previous!, previous![0]];
            output.AddRange(entry);
            if (previous is not null && table.Count < 4096) table.Add([.. previous, entry[0]]);
            previous = entry;
            if (table.Count == (1 << size) && size < 12) size++;
        }
        return output.ToArray();
    }
}
