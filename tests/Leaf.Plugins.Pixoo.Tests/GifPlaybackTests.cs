using System.Net;
using Microsoft.AspNetCore.Http;

namespace Leaf.Plugins.Pixoo.Tests;

public sealed class GifPlaybackTests
{
    [Fact]
    public async Task Gif_is_64_square_uniform_ten_fps_looping_and_keeps_duplicate_frames()
    {
        var pixels = Enumerable.Repeat((byte)80, RgbFrame.ByteCount).ToArray();
        var loop = new FrameLoop(Enumerable.Range(0, 6)
            .Select(index => new RgbFrame(index is 0 or 1 ? pixels.ToArray() : Solid((byte)(80 + index)), 100))
            .ToArray());

        var gif = await new FfmpegGifEncoder().EncodeAsync(loop, 100);
        var parsed = ParseGif(gif);

        Assert.Equal(64, parsed.Width);
        Assert.Equal(64, parsed.Height);
        Assert.Equal(6, parsed.FrameCount);
        Assert.All(parsed.Delays, delay => Assert.Equal(10, delay));
        Assert.True(parsed.LoopsForever);
    }

    [Fact]
    public async Task Gif_host_returns_404_without_exact_process_token_and_records_fetch()
    {
        const string token = "00112233445566778899aabbccddeeff";
        await using var host = new LanGifHost(new(), token);
        var publication = host.SeedForTest("resting.gif", [1, 2, 3]);
        foreach (var (path, method) in new[]
        {
            ("/wrong/resting.gif", "GET"),
            ("/" + token + "/missing.gif", "GET"),
            ("/" + token + "/resting.gif", "POST"),
        })
        {
            var rejected = Context(path, method);
            await host.HandleAsync(rejected);
            Assert.Equal(404, rejected.Response.StatusCode);
        }

        var accepted = Context("/" + token + "/resting.gif");
        accepted.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.60");
        await host.HandleAsync(accepted);
        Assert.Equal(200, accepted.Response.StatusCode);
        Assert.Equal("image/gif", accepted.Response.ContentType);
        var fetch = await host.WaitForFetchAsync(publication, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Equal("192.168.1.60", fetch?.RemoteAddress);
    }

    [Fact]
    public void Published_url_stays_well_below_pixoo_limit()
    {
        var url = LanGifHost.BuildUrl(IPAddress.Parse("192.168.100.200"), 18950,
            new string('a', 32), new string('b', 100) + ".gif");
        Assert.True(url.Length < 255);
    }

    [Fact]
    public void Colour_transfer_closes_seam_without_mean_flicker()
    {
        var still = Gradient();
        var frames = Enumerable.Range(0, 12).Select(index => Drift(still, 35 + index * 3, -18 + index)).ToArray();

        var corrected = PortraitColorCorrector.Correct(frames, still);

        Assert.True(PortraitColorCorrector.MeanAbsoluteDifference(corrected[^1], still) < .01);
        var meanChanges = corrected.Zip(corrected.Skip(1),
            (left, right) => Math.Abs(PortraitColorCorrector.Mean(left) - PortraitColorCorrector.Mean(right))).ToArray();
        Assert.All(meanChanges, change => Assert.True(change < 12, $"Mean frame change {change:F2} introduced flicker."));
    }

    [Fact]
    public void Resting_composites_are_seeded_deterministic_and_use_eight_to_twenty_five_second_pauses()
    {
        var still = Solid(10);
        var motion = new FrameLoop(Enumerable.Range(0, 20).Select(_ => new RgbFrame(Solid(220), 100)).ToArray());

        var first = PortraitCompositeBuilder.BuildResting(still, [motion, motion], "stable-source-hash");
        var second = PortraitCompositeBuilder.BuildResting(still, [motion, motion], "stable-source-hash");

        Assert.Equal(3, first.Count);
        Assert.Equal(first.Select(loop => loop.Frames.Count), second.Select(loop => loop.Frames.Count));
        for (var variant = 0; variant < first.Count; variant++)
        {
            Assert.InRange(first[variant].Frames.Count, 600, 1200);
            Assert.True(first[variant].Frames.SelectMany(frame => frame.Pixels).SequenceEqual(
                second[variant].Frames.SelectMany(frame => frame.Pixels)));
            var initialPause = first[variant].Frames.TakeWhile(frame => frame.Pixels[0] == 10).Count();
            Assert.InRange(initialPause, 80, 250);
            Assert.All(first[variant].Frames, frame => Assert.Equal(100, frame.DurationMs));
        }
    }

    [Theory]
    [InlineData(FaceState.Listening, 80, 150, 1, 1)]
    [InlineData(FaceState.Thinking, 100, 180, 3, 4)]
    [InlineData(FaceState.Working, 100, 180, 3, 4)]
    public void Active_composites_are_seeded_and_use_the_configured_long_pauses(
        FaceState state, int minimumPauseFrames, int maximumPauseFrames, int minimumPlays, int maximumPlays)
    {
        var still = Solid(10);
        var motion = new FrameLoop(Enumerable.Range(0, 20).Select(_ => new RgbFrame(Solid(220), 100)).ToArray());

        var first = PortraitCompositeBuilder.BuildActive(still, motion, "stable-source-hash", state);
        var second = PortraitCompositeBuilder.BuildActive(still, motion, "stable-source-hash", state);

        Assert.Equal(first.Frames.SelectMany(frame => frame.Pixels), second.Frames.SelectMany(frame => frame.Pixels));
        var starts = new List<int>();
        for (var index = 1; index < first.Frames.Count; index++)
            if (first.Frames[index - 1].Pixels[0] == 10 && first.Frames[index].Pixels[0] == 220) starts.Add(index);
        Assert.InRange(starts.Count, minimumPlays, maximumPlays);
        var previousMotionEnd = -1;
        foreach (var start in starts)
        {
            var pauseFrames = start - previousMotionEnd - 1;
            Assert.InRange(pauseFrames, minimumPauseFrames, maximumPauseFrames);
            previousMotionEnd = start + 19;
        }
    }

    [Fact]
    public void Stabilizer_recovers_global_drift_freezes_the_background_and_keeps_the_subject_whole()
    {
        const int side = 384;
        var stillSource = TexturedSource(side);
        var shifts = new[] { (2, -2), (3, -1), (4, 0), (3, 1), (2, 2), (3, 1), (4, 0), (2, -1), (2, -2) };
        var decoded = shifts.Select((shift, index) => new DecodedVideoFrame(
            DriftingFrame(stillSource, side, shift.Item1, shift.Item2, index), side, side)).ToArray();

        for (var index = 0; index < decoded.Length; index++)
        {
            var alignment = PortraitFrameAligner.EstimateTranslation(stillSource, decoded[index].Pixels, side, side);
            Assert.True(alignment.Reliable, $"Frame {index} was not alignable (confidence {alignment.Confidence:F3}).");
            Assert.InRange(alignment.ShiftX, shifts[index].Item1 - 1, shifts[index].Item1 + 1);
            Assert.InRange(alignment.ShiftY, shifts[index].Item2 - 1, shifts[index].Item2 + 1);
        }

        var still = PortraitPackBuilder.BoxDownsampleRgb(stillSource, side, side, new ImageRect(0, 0, side, side));
        var settings = new PortraitClipSettings { SampleStride = 1, MaximumLoopFrames = 8 };
        var processed = PortraitClipProcessor.ProcessMotionLoop(decoded, settings, still, stillSource);

        Assert.InRange(processed.FrozenBackgroundFraction, .15, .70);

        // The coherent 36x30 source blob maps to this small central band and must remain animated.
        var blobChanged = processed.Loop.Frames.Skip(1).Take(processed.Loop.Frames.Count - 2)
            .Any(frame => RegionDifference(frame.Pixels, still, 23, 28, 13, 9) > 80);
        Assert.True(blobChanged, "The coherent local motion was removed.");
    }

    [Fact]
    public void Tiny_feature_motion_uses_a_whole_soft_head_and_shoulders_matte()
    {
        var still = Solid(80);
        var frames = Enumerable.Range(0, 11).Select(index =>
        {
            var frame = still.ToArray();
            for (var y = 3; y < 60; y++)
            for (var x = 14; x < 51; x++)
            for (var channel = 0; channel < 3; channel++)
                frame[(y * 64 + x) * 3 + channel] = (byte)(82 + index % 3); // sub-threshold subject motion
            Paint(frame, 28 + index % 2, 20, 7, 4, 170);       // the only threshold-crossing feature
            return frame;
        }).ToArray();

        var matte = PortraitPixelStabilizer.BuildSubjectMatte(frames, still);
        Assert.True(matte.UsedFallback);
        Assert.Equal(1, matte.Alpha[24 * 64 + 32]);
        Assert.Equal(1, matte.Alpha[52 * 64 + 32]);
        Assert.Equal(0, matte.Alpha[0]);
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
        {
            var value = matte.Alpha[y * 64 + x];
            if (x + 1 < 64) Assert.True(Math.Abs(value - matte.Alpha[y * 64 + x + 1]) <= .31);
            if (y + 1 < 64) Assert.True(Math.Abs(value - matte.Alpha[(y + 1) * 64 + x]) <= .31);
        }

        var stabilized = PortraitPixelStabilizer.Stabilize(frames, still, 100,
            Enumerable.Repeat(new FrameAlignment(0, 0, 1, 0, 1, true), frames.Length).ToArray());
        var middle = stabilized.Loop.Frames[5].Pixels;
        var facePixel = (32 * 64 + 32) * 3;
        Assert.Equal(frames[5].AsSpan(facePixel, 3).ToArray(), middle.AsSpan(facePixel, 3).ToArray());
        Assert.Equal(still.AsSpan(0, 3).ToArray(), middle.AsSpan(0, 3).ToArray());
    }

    [Fact]
    public void Similarity_aligner_recovers_subpixel_scale_and_translation()
    {
        const int side = 384;
        var still = TexturedSource(side);
        var frame = SimilarityFrame(still, side, 1.018, 3.4, -2.6);

        var alignment = PortraitFrameAligner.EstimateTranslation(still, frame, side, side);

        Assert.True(alignment.Reliable, $"Similarity fit was rejected (confidence {alignment.Confidence:F3}).");
        Assert.InRange(alignment.Scale, 1.014, 1.022);
        Assert.InRange(alignment.ShiftX, 2.65, 4.15);
        Assert.InRange(alignment.ShiftY, -3.35, -1.85);
    }

    [Fact]
    public void Subject_region_freezes_disconnected_far_background_motion()
    {
        var still = Solid(60);
        var frames = Enumerable.Range(0, 8).Select(index =>
        {
            var frame = still.ToArray();
            Paint(frame, 20 + index % 2, 12, 12, 14, 210); // coherent subject motion
            Paint(frame, 60, 5 + index % 2, 3, 12, 180);   // disconnected shelf-edge wobble
            return frame;
        }).ToArray();

        var stabilized = PortraitPixelStabilizer.Stabilize(frames, still, 100,
            Enumerable.Repeat(new FrameAlignment(0, 0, 1, 0, 1, true), frames.Length).ToArray());

        Assert.Contains(stabilized.Loop.Frames, frame => RegionDifference(frame.Pixels, still, 18, 10, 18, 18) > 0);
        Assert.All(stabilized.Loop.Frames, frame => Assert.Equal(0, RegionDifference(frame.Pixels, still, 60, 3, 4, 20)));
    }

    [Theory]
    [InlineData("2026-10-21T03:00:00+02:00", FaceState.Dreaming)]
    [InlineData("2026-10-21T23:45:00+02:00", FaceState.Asleep)]
    [InlineData("2026-10-21T07:00:00+02:00", FaceState.Asleep)]
    [InlineData("2026-10-21T12:00:00+02:00", FaceState.Resting)]
    public void Local_time_windows_select_the_ambient_state(string timestamp, FaceState expected)
        => Assert.Equal(expected, AmbientStateSelector.Select(DateTimeOffset.Parse(timestamp), new()));

    private static DefaultHttpContext Context(string path, string method = "GET")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static byte[] Solid(byte value) => Enumerable.Repeat(value, RgbFrame.ByteCount).ToArray();

    private static byte[] Gradient()
    {
        var pixels = new byte[RgbFrame.ByteCount];
        for (var index = 0; index < pixels.Length; index += 3)
        {
            var pixel = index / 3;
            pixels[index] = (byte)(40 + pixel % 120);
            pixels[index + 1] = (byte)(60 + pixel / 64 % 100);
            pixels[index + 2] = (byte)(80 + (pixel % 64) * 2);
        }
        return pixels;
    }

    private static byte[] Drift(byte[] source, int red, int blue)
    {
        var result = source.ToArray();
        for (var index = 0; index < result.Length; index += 3)
        {
            result[index] = (byte)Math.Clamp(result[index] + red, 0, 255);
            result[index + 2] = (byte)Math.Clamp(result[index + 2] + blue, 0, 255);
        }
        return result;
    }

    private static byte[] TexturedSource(int side)
    {
        var pixels = new byte[side * side * 3];
        for (var y = 0; y < side; y++)
        for (var x = 0; x < side; x++)
        {
            var index = (y * side + x) * 3;
            pixels[index] = (byte)(35 + (x * 7 + y * 3 + x * y % 31) % 180);
            pixels[index + 1] = (byte)(30 + (x * 2 + y * 9 + x * y % 47) % 175);
            pixels[index + 2] = (byte)(40 + (x * 5 + y * 5 + x * y % 23) % 170);
        }
        return pixels;
    }

    private static byte[] DriftingFrame(byte[] still, int side, int shiftX, int shiftY, int frameIndex)
    {
        var frame = still.ToArray();
        var random = new Random(9000 + frameIndex);
        for (var y = 0; y < side; y++)
        for (var x = 0; x < side; x++)
        {
            var targetX = x + shiftX;
            var targetY = y + shiftY;
            if (targetX < 0 || targetX >= side || targetY < 0 || targetY >= side) continue;
            var source = (y * side + x) * 3;
            var target = (targetY * side + targetX) * 3;
            for (var channel = 0; channel < 3; channel++)
                frame[target + channel] = (byte)Math.Clamp(still[source + channel] + random.Next(-3, 4), 0, 255);
        }

        // A spatially and temporally coherent local motion region.
        var blobX = 144 + frameIndex * 3 + shiftX;
        var blobY = 174 + shiftY;
        for (var y = blobY; y < blobY + 30; y++)
        for (var x = blobX; x < blobX + 36; x++)
        {
            var offset = (y * side + x) * 3;
            frame[offset] = 235; frame[offset + 1] = 72; frame[offset + 2] = 48;
        }

        // A one-frame 6x6 transient becomes one reduced pixel and must be rejected.
        if (frameIndex == 4)
        for (var y = 60 + shiftY; y < 66 + shiftY; y++)
        for (var x = 300 + shiftX; x < 306 + shiftX; x++)
        {
            var offset = (y * side + x) * 3;
            frame[offset] = frame[offset + 1] = frame[offset + 2] = 255;
        }
        return frame;
    }

    private static byte[] SimilarityFrame(byte[] still, int side, double scale, double shiftX, double shiftY)
    {
        var result = still.ToArray();
        var centre = (side - 1) / 2d;
        for (var y = 0; y < side; y++)
        for (var x = 0; x < side; x++)
        {
            var sourceX = centre + (x - centre - shiftX) / scale;
            var sourceY = centre + (y - centre - shiftY) / scale;
            if (sourceX < 0 || sourceX >= side - 1 || sourceY < 0 || sourceY >= side - 1) continue;
            var target = (y * side + x) * 3;
            for (var channel = 0; channel < 3; channel++)
                result[target + channel] = (byte)Math.Round(Bilinear(still, side, sourceX, sourceY, channel));
        }
        return result;
    }

    private static double Bilinear(byte[] pixels, int side, double x, double y, int channel)
    {
        var x0 = (int)Math.Floor(x); var y0 = (int)Math.Floor(y);
        var x1 = x0 + 1; var y1 = y0 + 1;
        var fx = x - x0; var fy = y - y0;
        double At(int px, int py) => pixels[(py * side + px) * 3 + channel];
        return At(x0, y0) * (1 - fx) * (1 - fy) + At(x1, y0) * fx * (1 - fy)
            + At(x0, y1) * (1 - fx) * fy + At(x1, y1) * fx * fy;
    }

    private static double RegionDifference(byte[] frame, byte[] still, int x, int y, int width, int height)
    {
        double difference = 0;
        for (var py = y; py < y + height; py++)
        for (var px = x; px < x + width; px++)
        {
            var offset = (py * 64 + px) * 3;
            difference += Math.Abs(frame[offset] - still[offset]);
            difference += Math.Abs(frame[offset + 1] - still[offset + 1]);
            difference += Math.Abs(frame[offset + 2] - still[offset + 2]);
        }
        return difference;
    }

    private static void Paint(byte[] pixels, int x, int y, int width, int height, byte value)
    {
        for (var py = y; py < y + height; py++)
        for (var px = x; px < x + width; px++)
        {
            var offset = (py * 64 + px) * 3;
            pixels[offset] = value;
            pixels[offset + 1] = (byte)(value / 2);
            pixels[offset + 2] = (byte)(255 - value / 2);
        }
    }

    private static GifInfo ParseGif(byte[] bytes)
    {
        Assert.True(bytes.Length > 20);
        Assert.Equal("GIF", System.Text.Encoding.ASCII.GetString(bytes, 0, 3));
        var width = bytes[6] | bytes[7] << 8;
        var height = bytes[8] | bytes[9] << 8;
        var packed = bytes[10];
        var offset = 13;
        if ((packed & 0x80) != 0) offset += 3 * (1 << ((packed & 7) + 1));
        var frames = 0;
        var delays = new List<int>();
        while (offset < bytes.Length)
        {
            switch (bytes[offset++])
            {
                case 0x21:
                    var label = bytes[offset++];
                    if (label == 0xF9)
                    {
                        Assert.Equal(4, bytes[offset++]);
                        offset++;
                        delays.Add(bytes[offset] | bytes[offset + 1] << 8);
                        offset += 3;
                        Assert.Equal(0, bytes[offset++]);
                    }
                    else SkipBlocks(bytes, ref offset);
                    break;
                case 0x2C:
                    frames++;
                    var descriptorPacked = bytes[offset + 8];
                    offset += 9;
                    if ((descriptorPacked & 0x80) != 0) offset += 3 * (1 << ((descriptorPacked & 7) + 1));
                    offset++;
                    SkipBlocks(bytes, ref offset);
                    break;
                case 0x3B:
                    offset = bytes.Length;
                    break;
                default:
                    throw new InvalidDataException("Unexpected GIF block.");
            }
        }
        var loops = System.Text.Encoding.ASCII.GetString(bytes).Contains("NETSCAPE2.0", StringComparison.Ordinal);
        return new(width, height, frames, delays, loops);
    }

    private static void SkipBlocks(byte[] bytes, ref int offset)
    {
        while (true)
        {
            var length = bytes[offset++];
            if (length == 0) return;
            offset += length;
        }
    }

    private sealed record GifInfo(int Width, int Height, int FrameCount, IReadOnlyList<int> Delays, bool LoopsForever);
}

public sealed class ColourCorrectionFidelityTests
{
    private static byte[] Portrait()
    {
        // A varied synthetic image: skin, hair, fabric and background tones across the frame.
        var pixels = new byte[RgbFrame.ByteCount];
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
        {
            var i = (y * 64 + x) * 3;
            pixels[i] = (byte)(60 + (x * 3 + y) % 190);
            pixels[i + 1] = (byte)(40 + (x + y * 2) % 170);
            pixels[i + 2] = (byte)(30 + (x * y) % 150);
        }
        return pixels;
    }

    [Fact]
    public void Frames_identical_to_the_still_come_back_unchanged()
    {
        var still = Portrait();
        var frames = Enumerable.Range(0, 12).Select(_ => still.ToArray()).ToArray();
        var corrected = PortraitColorCorrector.Correct(frames, still);
        foreach (var frame in corrected)
            Assert.True(PortraitColorCorrector.MeanAbsoluteDifference(frame, still) < 1.0);
    }

    [Fact]
    public void A_uniformly_darkened_clip_is_restored_without_a_colour_cast()
    {
        var still = Portrait();
        var dark = still.Select(value => (byte)(value * .85)).ToArray();
        var corrected = PortraitColorCorrector.Correct(Enumerable.Range(0, 12).Select(_ => dark.ToArray()).ToArray(), still);
        var middle = corrected[6];
        for (var channel = 0; channel < 3; channel++)
        {
            double sum = 0;
            for (var i = channel; i < still.Length; i += 3) sum += middle[i] - still[i];
            Assert.InRange(sum / (still.Length / 3), -4, 4);
        }
    }
}
