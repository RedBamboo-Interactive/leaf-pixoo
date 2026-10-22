using System.Text.Json.Nodes;
using Leaf.Sdk;
using Microsoft.AspNetCore.Http;

namespace Leaf.Plugins.Pixoo.Tests;

public sealed class DisplayPreviewTests
{
    [Fact]
    public void Loop_key_changes_on_state_change_and_composite_rotation()
    {
        var store = new CurrentDisplayStore();
        var device = Device();
        var first = store.Set(device, Push(FaceState.Resting, "state.changed", 1), Loop(1), [71, 73, 70], 100);
        var stateChange = store.Set(device, Push(FaceState.Speaking, "session.stream", 2), Loop(2), [71, 73, 71], 100);
        var rotation = store.Set(device, Push(FaceState.Resting, "animation.rotate", 3), Loop(3), [71, 73, 72], 100);

        Assert.NotEqual(first.LoopKey, stateChange.LoopKey);
        Assert.NotEqual(stateChange.LoopKey, rotation.LoopKey);
        Assert.Equal("animation.rotate", store.History(device.Id)[0].Cause);
        Assert.Equal(rotation.LoopKey, store.Get(device.Id)?.LoopKey);
    }

    [Fact]
    public async Task Current_gif_uses_private_etag_and_returns_304_without_a_body()
    {
        var gif = new CurrentGif("0001-cafebabe", [71, 73, 70]);
        var notModified = new DefaultHttpContext();
        notModified.Request.Headers.IfNoneMatch = "\"0001-cafebabe\"";
        notModified.Response.Body = new MemoryStream();

        await CurrentGifHttp.WriteAsync(notModified, gif, CancellationToken.None);

        Assert.Equal(StatusCodes.Status304NotModified, notModified.Response.StatusCode);
        Assert.Equal("\"0001-cafebabe\"", notModified.Response.Headers.ETag);
        Assert.Equal("private", notModified.Response.Headers.CacheControl);
        Assert.Equal(0, notModified.Response.Body.Length);

        var fresh = new DefaultHttpContext();
        fresh.Response.Body = new MemoryStream();
        await CurrentGifHttp.WriteAsync(fresh, gif, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, fresh.Response.StatusCode);
        Assert.Equal("image/gif", fresh.Response.ContentType);
        Assert.Equal(gif.Bytes, ((MemoryStream)fresh.Response.Body).ToArray());
    }

    [Fact]
    public void Current_gif_device_lookup_is_owner_and_agent_scoped()
    {
        var owner = Guid.NewGuid().ToString();
        var agent = Guid.NewGuid();
        var entity = Entity(owner, agent);

        Assert.Equal(entity.Id, PixooService.RequireOwnedDevice(entity, new(owner, null)).Id);
        Assert.Equal(entity.Id, PixooService.RequireOwnedDevice(entity, new(owner, agent.ToString())).Id);
        Assert.Throws<KeyNotFoundException>(() => PixooService.RequireOwnedDevice(entity, new(Guid.NewGuid().ToString(), null)));
        Assert.Throws<KeyNotFoundException>(() => PixooService.RequireOwnedDevice(entity, new(owner, Guid.NewGuid().ToString())));
    }

    private static PixooDevice Device()
        => new(Guid.NewGuid(), "test", DeviceKind.Emulator, null, 80, Guid.NewGuid(), "#ffffff");

    private static FrameLoop Loop(byte value)
        => new([new RgbFrame(Enumerable.Repeat(value, RgbFrame.ByteCount).ToArray(), 100)]);

    private static DisplayPush Push(FaceState state, string cause, byte value)
        => new(Loop(value), state, null, cause, DateTimeOffset.UtcNow, state.ToString().ToLowerInvariant());

    private static LeafEntity Entity(string owner, Guid agent)
        => new(Guid.NewGuid(), PixooService.DeviceEntityType, "test", "test", new JsonObject
        {
            ["owner_id"] = owner,
            ["name"] = "test",
            ["kind"] = "emulator",
            ["brightness"] = 80,
            ["agent_id"] = agent.ToString(),
            ["agent_color"] = "#ffffff",
            ["minimum_push_gap_ms"] = 1000,
            ["hardware_enabled"] = false,
        }, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, owner);
}
