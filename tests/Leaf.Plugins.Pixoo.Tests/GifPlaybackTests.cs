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
