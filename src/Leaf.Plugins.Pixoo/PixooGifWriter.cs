namespace Leaf.Plugins.Pixoo;

/// <summary>
/// Writes a looping 64x64 GIF in the only frame layout the Pixoo-64 has been seen to render
/// correctly: every changed frame is a full 64x64 image, and a frame identical to the previous one
/// is a 1x1 redraw of the bottom-right pixel (disposal "do not dispose"). Cropped sub-rectangles in
/// the middle of the canvas (ffmpeg's <c>+offsetting</c> on localised motion) are drawn wrongly by
/// the device firmware and leave black dots and debris on the panel.
/// </summary>
internal static class PixooGifWriter
{
    private const int Size = 64;
    private const int Pixels = Size * Size;

    public static byte[] Write(IReadOnlyList<GifPaletteColor> palette, IReadOnlyList<byte[]> frames, int frameDurationMs)
    {
        if (palette.Count is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(palette));
        if (frames.Count == 0) throw new ArgumentException("A GIF needs at least one frame.", nameof(frames));
        if (frames.Any(frame => frame.Length != Pixels)) throw new ArgumentException("Frames must be 64x64 palette indices.", nameof(frames));
        if (frames.Any(frame => frame.Any(index => index >= palette.Count)))
            throw new ArgumentException("A frame references a colour outside the palette.", nameof(frames));
        var delay = (ushort)Math.Max(1, (int)Math.Round(frameDurationMs / 10d));

        using var output = new MemoryStream();
        output.Write("GIF89a"u8);
        WriteUInt16(output, Size);
        WriteUInt16(output, Size);
        output.WriteByte(0xF7); // global colour table present, 8-bit colour resolution, 256 entries
        output.WriteByte(0);    // background colour index
        output.WriteByte(0);    // pixel aspect ratio
        for (var index = 0; index < 256; index++)
        {
            var colour = index < palette.Count ? palette[index] : default;
            output.WriteByte(colour.R);
            output.WriteByte(colour.G);
            output.WriteByte(colour.B);
        }

        // NETSCAPE2.0 application extension: loop forever.
        output.Write([0x21, 0xFF, 0x0B]);
        output.Write("NETSCAPE2.0"u8);
        output.Write([0x03, 0x01, 0x00, 0x00, 0x00]);

        byte[]? previous = null;
        foreach (var frame in frames)
        {
            var unchanged = previous is not null && frame.AsSpan().SequenceEqual(previous);
            WriteGraphicControl(output, delay);
            if (unchanged) WriteImage(output, Size - 1, Size - 1, 1, 1, [frame[Pixels - 1]]);
            else WriteImage(output, 0, 0, Size, Size, frame);
            previous = frame;
        }
        output.WriteByte(0x3B);
        return output.ToArray();
    }

    private static void WriteGraphicControl(Stream output, ushort delay)
    {
        output.Write([0x21, 0xF9, 0x04, 0x04]); // disposal 1 (do not dispose), no transparency
        WriteUInt16(output, delay);
        output.Write([0x00, 0x00]);
    }

    private static void WriteImage(Stream output, int left, int top, int width, int height, byte[] indices)
    {
        output.WriteByte(0x2C);
        WriteUInt16(output, left);
        WriteUInt16(output, top);
        WriteUInt16(output, width);
        WriteUInt16(output, height);
        output.WriteByte(0); // no local colour table, not interlaced
        output.WriteByte(8); // LZW minimum code size
        var compressed = Lzw(indices);
        for (var offset = 0; offset < compressed.Length; offset += 255)
        {
            var length = Math.Min(255, compressed.Length - offset);
            output.WriteByte((byte)length);
            output.Write(compressed, offset, length);
        }
        output.WriteByte(0);
    }

    internal static byte[] Lzw(IReadOnlyList<byte> indices)
    {
        const int clear = 256, end = 257;
        var bits = new BitWriter();
        var table = new Dictionary<int, int>();
        var codeSize = 9;
        var next = 258;
        bits.Write(clear, codeSize);
        var prefix = -1;
        foreach (var symbol in indices)
        {
            if (prefix < 0) { prefix = symbol; continue; }
            var key = (prefix << 8) | symbol;
            if (table.TryGetValue(key, out var code)) { prefix = code; continue; }
            bits.Write(prefix, codeSize);
            if (next < 4096)
            {
                table[key] = next++;
                // Decoders add each entry one code later than the encoder, so widen one step after the
                // table first fills the current width (verified against Pillow's decoder).
                if (next > (1 << codeSize) && codeSize < 12) codeSize++;
            }
            else
            {
                bits.Write(clear, codeSize);
                table.Clear();
                codeSize = 9;
                next = 258;
            }
            prefix = symbol;
        }
        if (prefix >= 0) bits.Write(prefix, codeSize);
        bits.Write(end, codeSize);
        return bits.ToArray();
    }

    private static void WriteUInt16(Stream output, int value)
    {
        output.WriteByte((byte)(value & 0xFF));
        output.WriteByte((byte)((value >> 8) & 0xFF));
    }

    private sealed class BitWriter
    {
        private readonly List<byte> bytes = [];
        private int current;
        private int count;

        public void Write(int code, int width)
        {
            current |= code << count;
            count += width;
            while (count >= 8)
            {
                bytes.Add((byte)(current & 0xFF));
                current >>= 8;
                count -= 8;
            }
        }

        public byte[] ToArray()
        {
            if (count > 0) bytes.Add((byte)(current & 0xFF));
            current = 0;
            count = 0;
            return bytes.ToArray();
        }
    }
}
