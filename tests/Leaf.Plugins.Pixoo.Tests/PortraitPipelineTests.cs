using SkiaSharp;

namespace Leaf.Plugins.Pixoo.Tests;

public sealed class PortraitPipelineTests
{
    [Fact]
    public void Faithful_base_is_byte_identical_to_box_reduction_of_the_square_crop()
    {
        var prepared = new PortraitPackBuilder().Prepare(SyntheticPortrait());

        var expected = PortraitPackBuilder.BoxReduce(prepared.CropPng);

        Assert.Equal(expected, prepared.BasePixels);
        Assert.Equal(RgbFrame.ByteCount, prepared.BasePixels.Length);
        using var crop = SKBitmap.Decode(prepared.CropPng);
        Assert.Equal(crop.Width, crop.Height);
        Assert.Equal(prepared.SourceSquare.Width, crop.Width);
    }

    [Fact]
    public void Skin_coloured_clothing_cannot_push_the_head_out_of_the_portrait_crop()
    {
        var prepared = new PortraitPackBuilder().Prepare(SyntheticPortraitWithRaisedHandAndSkinColouredTop());

        Assert.Equal(0, prepared.SourceSquare.Y);
        Assert.InRange(prepared.SourceSquare.X, 100, 300);
        Assert.True(prepared.SourceSquare.Right >= 700);
    }

    [Fact]
    public void Slow_fallback_plate_widens_square_and_maps_it_back_to_video_space()
    {
        var plate = PortraitPackBuilder.BuildPlateGeometry(new ImageRect(200, 100, 300, 300), 1000, 800);

        Assert.Equal(new ImageRect(150, 100, 400, 300), plate.SourcePlate);
        Assert.Equal(new ImageRect(96, 0, 576, 576), plate.OutputSquare);
        Assert.Equal(768, plate.OutputWidth);
        Assert.Equal(576, plate.OutputHeight);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(700, 600)]
    public void Slow_fallback_plate_clamps_at_source_edges_without_moving_the_square(int squareX, int expectedPlateX)
    {
        var plate = PortraitPackBuilder.BuildPlateGeometry(new ImageRect(squareX, 80, 300, 300), 1000, 800);

        Assert.Equal(expectedPlateX, plate.SourcePlate.X);
        var expectedOutputX = squareX == 0 ? 0 : 192;
        Assert.Equal(new ImageRect(expectedOutputX, 0, 576, 576), plate.OutputSquare);
    }

    [Fact]
    public void Fast_sampling_drops_duplicate_last_frame_and_takes_every_second()
    {
        var selected = PortraitClipProcessor.SampleIndices(41, 2, 30);

        Assert.Equal(20, selected.Length);
        Assert.Equal(Enumerable.Range(0, 20).Select(value => value * 2), selected);
        Assert.DoesNotContain(40, selected);
    }

    [Fact]
    public void Motion_loop_uses_uniform_timing_and_still_loop_is_separate()
    {
        var decoded = Enumerable.Range(0, 41).Select(VideoFrame).ToArray();
        var settings = new PortraitClipSettings();

        var motion = PortraitClipProcessor.BuildMotionLoop(decoded, settings);
        var stillPixels = Enumerable.Repeat((byte)91, RgbFrame.ByteCount).ToArray();
        var still = PortraitClipProcessor.BuildStillLoop(stillPixels);

        Assert.Equal(20, motion.Frames.Count);
        Assert.All(motion.Frames, frame => Assert.Equal(100, frame.DurationMs));
        Assert.All(motion.Frames[1].Pixels, value => Assert.Equal((byte)2, value));
        Assert.All(motion.Frames[^1].Pixels, value => Assert.Equal((byte)38, value));
        Assert.Single(still.Frames);
        Assert.Equal(100, still.Frames[0].DurationMs);
        Assert.Equal(stillPixels, still.Frames[0].Pixels);
        Assert.NotSame(stillPixels, still.Frames[0].Pixels);
    }

    [Fact]
    public void Loop_never_exceeds_thirty_frames()
    {
        var decoded = Enumerable.Range(0, 81).Select(VideoFrame).ToArray();
        var settings = new PortraitClipSettings { SampleStride = 1, MaximumLoopFrames = 30 };

        var loop = PortraitClipProcessor.BuildMotionLoop(decoded, settings);

        Assert.Equal(30, loop.Frames.Count);
        Assert.All(loop.Frames[^1].Pixels, value => Assert.Equal((byte)29, value));
    }

    [Fact]
    public void Missing_or_failed_clip_falls_back_to_unchanged_still_portrait()
    {
        var pixels = Enumerable.Repeat((byte)120, RgbFrame.ByteCount).ToArray();
        var pack = Pack(pixels);

        var program = PortraitLoopRenderer.Render(pack, FaceState.Speaking, 100);

        Assert.Single(program.Still.Frames);
        Assert.Null(program.Motion);
        Assert.Equal(pixels, program.InitialLoop.Frames[0].Pixels);
        Assert.Equal(100, program.InitialLoop.Frames[0].DurationMs);
    }

    [Fact]
    public void Working_reuses_thinking_and_asleep_reuses_dimmed_dreaming()
    {
        var pixels = Enumerable.Repeat((byte)200, RgbFrame.ByteCount).ToArray();
        var thinking = Clip(FaceState.Thinking, pixels);
        var dreaming = Clip(FaceState.Dreaming, pixels);
        var pack = Pack(pixels, thinking, dreaming);

        var working = PortraitLoopRenderer.Render(pack, FaceState.Working, 100);
        var asleep = PortraitLoopRenderer.Render(pack, FaceState.Asleep, 100);

        Assert.Equal(DisplayPlaybackMode.Continuous, working.Mode);
        Assert.All(working.Motion!.Frames[0].Pixels, value => Assert.Equal((byte)200, value));
        Assert.Equal(DisplayPlaybackMode.Continuous, asleep.Mode);
        Assert.All(asleep.Still.Frames[0].Pixels, value => Assert.Equal((byte)70, value));
        Assert.All(asleep.Motion!.Frames[0].Pixels, value => Assert.Equal((byte)70, value));
    }

    // Distance-from-still profile measured on a real 20-frame dreaming clip (2026-11-05): open, shut for ~12 frames, open.
    private static readonly double[] RealDreamingProfile =
        [0.0, 0.2, 0.7, 1.9, 3.6, 4.0, 3.7, 4.1, 4.7, 5.0, 4.8, 4.6, 4.8, 5.1, 5.1, 4.1, 1.9, 0.9, 0.5, 0.0];

    private static FrameLoop ProfiledClip()
        => new(RealDreamingProfile.Select(distance =>
            new RgbFrame(Enumerable.Repeat((byte)(100 + Math.Round(distance * 10)), RgbFrame.ByteCount).ToArray(), 100)).ToArray());

    [Fact]
    public void Sleeping_loop_holds_the_eyes_closed_stretch_and_never_shows_the_open_endpoints()
    {
        var loop = PortraitCompositeBuilder.BuildSleeping(ProfiledClip(), TimeSpan.FromSeconds(60));

        // Closed stretch is frames 4..15 (>= 70% of the 5.1 peak); open endpoints and the opening/closing frames never appear.
        var closed = Enumerable.Range(4, 12).Select(index => (byte)(100 + Math.Round(RealDreamingProfile[index] * 10))).ToHashSet();
        Assert.All(loop.Frames, frame => Assert.Contains(frame.Pixels[0], closed));
        Assert.All(loop.Frames, frame => Assert.Equal(100, frame.DurationMs));

        // Forward 12 + backward 10 = 22 steps at 2x = 44 frames per cycle; whole cycles only so the device wrap is seamless.
        Assert.Equal(0, loop.Frames.Count % 44);
        Assert.InRange(loop.Frames.Count, 550, 650);
        Assert.Equal(loop.Frames[0].Pixels, loop.Frames[44].Pixels);
        Assert.Equal(loop.Frames[^1].Pixels, loop.Frames[43].Pixels);
    }

    [Fact]
    public void Asleep_and_dreaming_render_without_the_open_eyed_frames_of_the_clip()
    {
        var still = Enumerable.Repeat((byte)100, RgbFrame.ByteCount).ToArray();
        var dreaming = Clip(FaceState.Dreaming, still) with { Loop = ProfiledClip() };
        var pack = Pack(still, dreaming);

        var asleep = PortraitLoopRenderer.Render(pack, FaceState.Asleep, 100);
        var dreamingProgram = PortraitLoopRenderer.Render(pack, FaceState.Dreaming, 100);

        Assert.All(asleep.Motion!.Frames, frame => Assert.True(frame.Pixels[0] >= Math.Round(136 * .35)));
        Assert.All(dreamingProgram.Motion!.Frames, frame => Assert.True(frame.Pixels[0] >= 136));
    }

    [Fact]
    public async Task Generation_is_ordered_and_bounded_to_two_attempts_per_clip()
    {
        var calls = new List<(string Key, int Attempt)>();
        var settings = new PortraitClipSettings { RetryCooldown = TimeSpan.Zero };

        await PortraitClipPlan.ExecuteAsync((spec, attempt, _) =>
        {
            calls.Add((spec.Key, attempt));
            return Task.FromResult(spec.State != FaceState.Speaking);
        }, settings, CancellationToken.None);

        Assert.Equal([
            ("resting-look-down", 1),
            ("resting-glance-aside", 1),
            ("resting-private-smile", 1),
            ("resting-breathe-blink", 1),
            ("speaking", 1), ("speaking", 2),
            ("listening", 1),
            ("thinking", 1),
            ("dreaming", 1),
        ], calls);
    }

    [Fact]
    public async Task Builds_are_single_flight_per_agent()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var singleFlight = new AgentSingleFlight<int>();
        var agentId = Guid.NewGuid();
        async Task<int> Build()
        {
            Interlocked.Increment(ref calls);
            started.SetResult();
            await gate.Task;
            return 42;
        }

        var first = singleFlight.RunAsync(agentId, Build);
        await started.Task;
        var second = singleFlight.RunAsync(agentId, Build);
        gate.SetResult();

        Assert.Equal(42, await first);
        Assert.Equal(42, await second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Prompts_are_state_specific_and_only_add_the_supplied_scene_description()
    {
        const string description = "amber knit top, blue denim jacket, library alcove";

        var resting = PortraitClipPrompts.For(FaceState.Resting, description);
        var speaking = PortraitClipPrompts.For(FaceState.Speaking, description);

        Assert.Contains(description, resting.Prompt);
        Assert.Contains("one natural blink", resting.Prompt);
        Assert.Contains("conversational lip and jaw movement", speaking.Prompt);
        Assert.DoesNotContain("rain", resting.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("woman", resting.Prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Source_hash_change_invalidates_the_pack()
    {
        var original = PortraitSourceFingerprint.Hash([1, 2, 3]);
        Assert.False(PortraitSourceFingerprint.RequiresRebuild(original, PortraitSourceFingerprint.Hash([1, 2, 3])));
        Assert.True(PortraitSourceFingerprint.RequiresRebuild(original, PortraitSourceFingerprint.Hash([1, 2, 4])));
    }

    private static DecodedVideoFrame VideoFrame(int value)
        => new(Enumerable.Repeat((byte)value, 384 * 384 * 3).ToArray(), 384, 384);

    private static PortraitPack Pack(byte[] pixels, params PortraitClip[] clips)
        => new(pixels, new("source", "hash", PortraitPackBuilder.PipelineVersion, DateTimeOffset.UnixEpoch),
            clips.ToDictionary(clip => clip.Provenance.Key));

    private static PortraitClip Clip(FaceState state, byte[] pixels)
        => new(new FrameLoop([new RgbFrame(pixels, 133)]),
            new(PortraitClipPlan.PrimaryKey(state), state, PortraitPackService.Workflow, 1, "job", "video", "loop", "prompt", "negative", null,
                0,
                DateTimeOffset.UnixEpoch));

    private static byte[] SyntheticPortrait()
    {
        using var bitmap = new SKBitmap(1024, 1024, SKColorType.Rgba8888, SKAlphaType.Opaque);
        bitmap.Erase(new SKColor(30, 48, 68));
        using var canvas = new SKCanvas(bitmap);
        using var skin = new SKPaint { Color = new SKColor(214, 159, 123), IsAntialias = false };
        using var hair = new SKPaint { Color = new SKColor(205, 166, 72), IsAntialias = false };
        using var jacket = new SKPaint { Color = new SKColor(58, 98, 148), IsAntialias = false };
        using var dark = new SKPaint { Color = new SKColor(45, 34, 31), IsAntialias = false };
        canvas.DrawRect(250, 610, 524, 414, jacket);
        canvas.DrawOval(new SKRect(326, 130, 698, 650), hair);
        canvas.DrawOval(new SKRect(366, 175, 658, 585), skin);
        canvas.DrawOval(new SKRect(418, 335, 456, 358), dark);
        canvas.DrawOval(new SKRect(550, 320, 589, 343), dark);
        canvas.DrawRect(474, 480, 98, 18, dark);
        canvas.Flush();
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private static byte[] SyntheticPortraitWithRaisedHandAndSkinColouredTop()
    {
        using var bitmap = new SKBitmap(1024, 1024, SKColorType.Rgba8888, SKAlphaType.Opaque);
        bitmap.Erase(new SKColor(24, 31, 40));
        using var canvas = new SKCanvas(bitmap);
        using var skin = new SKPaint { Color = new SKColor(214, 159, 123), IsAntialias = false };
        using var top = new SKPaint { Color = new SKColor(228, 195, 158), IsAntialias = false };
        using var hair = new SKPaint { Color = new SKColor(205, 166, 72), IsAntialias = false };
        using var dark = new SKPaint { Color = new SKColor(35, 28, 25), IsAntialias = false };
        canvas.DrawRect(300, 430, 424, 500, top);
        canvas.DrawOval(new SKRect(382, 55, 674, 390), hair);
        canvas.DrawOval(new SKRect(414, 88, 642, 352), skin);
        canvas.DrawOval(new SKRect(450, 176, 482, 194), dark);
        canvas.DrawOval(new SKRect(566, 176, 598, 194), dark);
        canvas.DrawRect(496, 272, 64, 14, dark);
        canvas.DrawOval(new SKRect(52, 128, 220, 300), skin);
        canvas.Flush();
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }
}
