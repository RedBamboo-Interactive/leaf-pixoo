using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit.Abstractions;

namespace Leaf.Plugins.Pixoo.Tests;

/// <summary>Opt-in, private real-clip acceptance. No fixture or rendered output enters the repository.</summary>
public sealed class RealJiggleFixtureTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Final_gifs_do_not_add_more_cell_flicker_than_plain_box_reduction()
    {
        var fixtures = Environment.GetEnvironmentVariable("PIXOO_JIGGLE_FIXTURES");
        if (string.IsNullOrWhiteSpace(fixtures))
        {
            output.WriteLine("Skipped: PIXOO_JIGGLE_FIXTURES is not set.");
            return;
        }
        var scratch = Environment.GetEnvironmentVariable("REDLEAF_SCRATCH_DIR");
        if (string.IsNullOrWhiteSpace(scratch))
            throw new InvalidOperationException("REDLEAF_SCRATCH_DIR must be set for private jiggle diagnostics.");
        var review = Path.Combine(scratch, "pixoo-jiggle-review");
        Directory.CreateDirectory(review);
        var decoder = new FfmpegVideoFrameDecoder();
        var encoder = new FfmpegGifEncoder();
        var settings = new PortraitClipSettings();
        var results = new List<Result>();

        foreach (var name in new[] { "speaking", "thinking", "resting-look-down", "resting-glance-aside" })
        {
            var decoded = await decoder.DecodeAsync(await File.ReadAllBytesAsync(Path.Combine(fixtures, name + ".mp4")),
                384, 384, CancellationToken.None);
            var current = ReadLoop(await File.ReadAllBytesAsync(Path.Combine(fixtures, name + ".bin")));
            var still = current.Frames[0].Pixels;
            var plain = PortraitClipProcessor.BuildMotionLoop(decoded, settings);
            var processed = PortraitClipProcessor.ProcessMotionLoop(decoded, settings, still, decoded[0].Pixels).Loop;
            var currentGif = await DecodeGif(await EncodeCurrentGifAsync(current), decoder);
            var finalGif = await DecodeGif(await encoder.EncodeAsync(processed, 100), decoder);
            var result = new Result(name, Flicker(plain), Flicker(current), Flicker(processed), Flicker(finalGif),
                MouthMotion(finalGif));
            results.Add(result);
            output.WriteLine($"{name}: plain {result.Plain.Mean:P2} mean/{result.Plain.Median:P2} median; "
                + $"current {result.Current.Mean:P2}/{result.Current.Median:P2}; "
                + $"new {result.Processed.Mean:P2}/{result.Processed.Median:P2}; "
                + $"GIF {result.Gif.Mean:P2}/{result.Gif.Median:P2}; mouth {result.Mouth:F2} mean RGB");
            await WriteComparisonAsync(Path.Combine(review, name + "-comparison-v8.mp4"), decoded, plain,
                currentGif, processed, finalGif, settings);
        }

        Assert.All(results, result =>
        {
            Assert.True(result.Gif.Mean <= result.Plain.Mean + .0001,
                $"{result.Name} GIF mean flicker {result.Gif.Mean:P2} exceeded plain BOX {result.Plain.Mean:P2}.");
        });
        Assert.True(results.Single(result => result.Name == "speaking").Mouth >= 5,
            "Speaking mouth motion was suppressed by temporal stabilization.");
    }

    private static FlickerResult Flicker(FrameLoop loop)
    {
        var values = new double[loop.Frames.Count - 1];
        for (var frame = 0; frame < loop.Frames.Count - 1; frame++)
        {
            var left = loop.Frames[frame].Pixels;
            var right = loop.Frames[frame + 1].Pixels;
            var changed = 0;
            for (var pixel = 0; pixel < 4096; pixel++)
            {
                var offset = pixel * 3;
                var difference = (Math.Abs(left[offset] - right[offset])
                    + Math.Abs(left[offset + 1] - right[offset + 1])
                    + Math.Abs(left[offset + 2] - right[offset + 2])) / 3d;
                if (difference > 6) changed++;
            }
            values[frame] = changed / 4096d;
        }
        var sorted = values.Order().ToArray();
        var median = sorted.Length % 2 == 1 ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
        return new(values.Average(), median);
    }

    private static double MouthMotion(FrameLoop loop)
    {
        var best = 0d;
        for (var left = 0; left < loop.Frames.Count; left++)
        for (var right = left + 1; right < loop.Frames.Count; right++)
        {
            double difference = 0;
            var count = 0;
            for (var y = 23; y < 35; y++)
            for (var x = 23; x < 41; x++)
            {
                var offset = (y * 64 + x) * 3;
                for (var channel = 0; channel < 3; channel++)
                    difference += Math.Abs(loop.Frames[left].Pixels[offset + channel]
                        - loop.Frames[right].Pixels[offset + channel]);
                count += 3;
            }
            best = Math.Max(best, difference / count);
        }
        return best;
    }

    private static async Task<FrameLoop> DecodeGif(byte[] gif, FfmpegVideoFrameDecoder decoder)
    {
        var frames = await decoder.DecodeAsync(gif, 64, 64, CancellationToken.None);
        return new FrameLoop(frames.Select(frame => new RgbFrame(frame.Pixels, 100)).ToArray()).Validate();
    }

    private static async Task<byte[]> EncodeCurrentGifAsync(FrameLoop loop)
    {
        var start = StartFfmpeg();
        foreach (var argument in new[]
        {
            "-hide_banner", "-loglevel", "error", "-f", "rawvideo", "-pixel_format", "rgb24",
            "-video_size", "64x64", "-framerate", "10", "-i", "pipe:0", "-filter_complex",
            "[0:v]split[a][b];[a]palettegen=stats_mode=full[p];[b][p]paletteuse=dither=none",
            "-gifflags", "+offsetting", "-loop", "0", "-f", "gif", "pipe:1",
        }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("ffmpeg did not start.");
        var error = process.StandardError.ReadToEndAsync();
        var write = Task.Run(async () =>
        {
            foreach (var frame in loop.Frames) await process.StandardInput.BaseStream.WriteAsync(frame.Pixels);
            process.StandardInput.Close();
        });
        await using var bytes = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(bytes);
        await write;
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidDataException(await error);
        return bytes.ToArray();
    }

    private static async Task WriteComparisonAsync(string path, IReadOnlyList<DecodedVideoFrame> source,
        FrameLoop plain, FrameLoop currentGif, FrameLoop processed, FrameLoop finalGif, PortraitClipSettings settings)
    {
        var start = StartFfmpeg();
        foreach (var argument in new[]
        {
            "-hide_banner", "-loglevel", "error", "-y", "-f", "rawvideo", "-pixel_format", "rgb24",
            "-video_size", "1920x384", "-framerate", "10", "-i", "pipe:0", "-c:v", "libx264",
            "-preset", "medium", "-crf", "17", "-pix_fmt", "yuv420p", "-movflags", "+faststart", path,
        }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("ffmpeg did not start.");
        var error = process.StandardError.ReadToEndAsync();
        var sampled = PortraitClipProcessor.SampleIndices(source.Count, settings.SampleStride, settings.MaximumLoopFrames);
        var count = new[] { sampled.Length, plain.Frames.Count, currentGif.Frames.Count,
            processed.Frames.Count, finalGif.Frames.Count }.Min();
        for (var index = 0; index < count; index++)
        {
            var row = Compose(source[sampled[index]].Pixels, plain.Frames[index].Pixels,
                currentGif.Frames[index].Pixels, processed.Frames[index].Pixels, finalGif.Frames[index].Pixels);
            await process.StandardInput.BaseStream.WriteAsync(row);
        }
        process.StandardInput.Close();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidDataException(await error);
    }

    private static byte[] Compose(byte[] source, params byte[][] small)
    {
        var result = new byte[1920 * 384 * 3];
        for (var y = 0; y < 384; y++)
        {
            source.AsSpan(y * 384 * 3, 384 * 3).CopyTo(result.AsSpan(y * 1920 * 3, 384 * 3));
            for (var panel = 0; panel < small.Length; panel++)
            for (var x = 0; x < 384; x++)
            {
                var from = ((y / 6) * 64 + x / 6) * 3;
                var to = (y * 1920 + (panel + 1) * 384 + x) * 3;
                small[panel].AsSpan(from, 3).CopyTo(result.AsSpan(to, 3));
            }
        }
        return result;
    }

    private static ProcessStartInfo StartFfmpeg() => new()
    {
        FileName = "ffmpeg", UseShellExecute = false, RedirectStandardInput = true,
        RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
    };

    private static FrameLoop ReadLoop(byte[] json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("Fixture loop is malformed.");
        var frames = root["frames"] as JsonArray ?? throw new InvalidDataException("Fixture loop has no frames.");
        return new FrameLoop(frames.Select(item =>
        {
            var frame = item as JsonObject ?? throw new InvalidDataException("Fixture frame is malformed.");
            return new RgbFrame(Convert.FromBase64String(frame["pixels"]!.GetValue<string>()),
                frame["durationMs"]!.GetValue<int>());
        }).ToArray()).Validate();
    }

    private readonly record struct FlickerResult(double Mean, double Median);
    private sealed record Result(string Name, FlickerResult Plain, FlickerResult Current,
        FlickerResult Processed, FlickerResult Gif, double Mouth);
}
