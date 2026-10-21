namespace Leaf.Plugins.Pixoo.Tests;

public sealed class PixooPayloadTests
{
    [Fact]
    public void Embedded_manifest_is_valid_and_ships_no_skills()
    {
        var manifest = new PixooPlugin().Manifest;
        Assert.Equal("pixoo", manifest.Id);
        Assert.Equal("Leaf.Plugins.Pixoo", manifest.Backend!.Assembly);
    }

    [Fact]
    public void Payload_encodes_exact_64_square_rgb_frame()
    {
        var bytes = Enumerable.Range(0, RgbFrame.ByteCount).Select(i => (byte)(i % 251)).ToArray();
        var loop = new FrameLoop([new(bytes, 320)]);
        var payload = Assert.Single(Pixoo64Sink.BuildFramePayloads(loop, 7));
        Assert.Equal("Draw/SendHttpGif", payload["Command"]!.GetValue<string>());
        Assert.Equal(1, payload["PicNum"]!.GetValue<int>());
        Assert.Equal(64, payload["PicWidth"]!.GetValue<int>());
        Assert.Equal(0, payload["PicOffset"]!.GetValue<int>());
        Assert.Equal(7, payload["PicID"]!.GetValue<int>());
        Assert.Equal(320, payload["PicSpeed"]!.GetValue<int>());
        Assert.Equal(RgbFrame.ByteCount, Convert.FromBase64String(payload["PicData"]!.GetValue<string>()).Length);
        Assert.Equal(bytes, Convert.FromBase64String(payload["PicData"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData(FaceState.Resting)]
    [InlineData(FaceState.Thinking)]
    [InlineData(FaceState.Speaking)]
    [InlineData(FaceState.Dark)]
    public void Renderer_always_produces_device_sized_frames(FaceState state)
    {
        var program = new ProceduralFaceRenderer().Render(Guid.Empty, state, "#78e6c4", 75);
        Assert.NotEmpty(program.InitialLoop.Frames);
        Assert.All(program.InitialLoop.Frames, frame => Assert.Equal(64 * 64 * 3, frame.Pixels.Length));
    }
}
