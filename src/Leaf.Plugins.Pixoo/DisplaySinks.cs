using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Leaf.Plugins.Pixoo;

public interface IDisplaySink
{
    Task PushAsync(PixooDevice device, DisplayPush push, CancellationToken ct = default);
}

public sealed class EmulatorSink(
    DisplayPlaybackStatusStore statuses,
    IGifEncoder gifEncoder,
    CurrentDisplayStore current,
    PixooPlaybackSettings settings,
    IPushClock? clock = null) : IDisplaySink
{
    public async Task PushAsync(PixooDevice device, DisplayPush push, CancellationToken ct = default)
    {
        push.Loop.Validate();
        var now = clock?.UtcNow ?? DateTimeOffset.UtcNow;
        var gif = await gifEncoder.EncodeAsync(push.Loop, settings.GifFrameDurationMs, ct);
        statuses.Playing(device.Id, push.AnimationName, null, null);
        current.Set(device, push, push.Loop, gif, settings.GifFrameDurationMs, now);
    }
}

/// <summary>
/// Unverified local-only Pixoo-64 driver. It performs no discovery and no retries.
/// It is only selected for an explicitly enabled hardware device.
/// </summary>
public sealed class Pixoo64Sink(
    HttpClient http,
    ILanGifHost gifHost,
    IGifEncoder gifEncoder,
    DisplayPlaybackStatusStore statuses,
    CurrentDisplayStore current,
    PixooPlaybackSettings settings) : IDisplaySink
{
    public async Task PushAsync(PixooDevice device, DisplayPush push, CancellationToken ct = default)
    {
        if (!device.HardwareEnabled) throw new InvalidOperationException("Pixoo hardware output is disabled.");
        if (string.IsNullOrWhiteSpace(device.Host)) throw new InvalidOperationException("A Pixoo host is required.");
        push.Loop.Validate();
        var endpoint = new UriBuilder("http", device.Host) { Path = "/post" }.Uri;
        await Post(endpoint, new JsonObject { ["Command"] = "Channel/SetBrightness", ["Brightness"] = Math.Clamp(device.Brightness, 0, 100) }, ct);
        if (push.State == FaceState.Dark)
        {
            await Post(endpoint, new JsonObject { ["Command"] = "Channel/OnOffScreen", ["OnOff"] = 0 }, ct);
            statuses.Dark(device.Id);
            var darkGif = await gifEncoder.EncodeAsync(push.Loop, settings.GifFrameDurationMs, ct);
            current.Set(device, push, push.Loop, darkGif, settings.GifFrameDurationMs);
            return;
        }

        try
        {
            var gif = await gifEncoder.EncodeAsync(push.Loop, settings.GifFrameDurationMs, ct);
            var publication = await gifHost.PublishAsync(device, push.AnimationName, gif, ct);
            await Post(endpoint, new JsonObject
            {
                ["Command"] = "Device/PlayTFGif",
                ["FileType"] = 2,
                ["FileName"] = publication.Url,
            }, ct);
            await Post(endpoint, new JsonObject { ["Command"] = "Channel/OnOffScreen", ["OnOff"] = 1 }, ct);
            var fetch = await gifHost.WaitForFetchAsync(publication, settings.FetchTimeout, ct);
            if (fetch is null) throw new GifDeliveryException(
                "Display could not fetch its animation: check the Leaf Pixoo GIF firewall rule.");
            statuses.Playing(device.Id, push.AnimationName, fetch, null);
            current.Set(device, push, push.Loop, gif, settings.GifFrameDurationMs);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var message = ex is GifDeliveryException
                ? ex.Message
                : "Display could not fetch its animation: check the Leaf Pixoo GIF firewall rule. " + ex.Message;
            var fallback = await PushFramesFallback(endpoint, push.Loop, ct);
            await Post(endpoint, new JsonObject { ["Command"] = "Channel/OnOffScreen", ["OnOff"] = 1 }, ct);
            statuses.Playing(device.Id, push.AnimationName + " (frame fallback)", null, message);
            var fallbackGif = await gifEncoder.EncodeAsync(fallback, settings.GifFrameDurationMs, ct);
            current.Set(device, push, fallback, fallbackGif, settings.GifFrameDurationMs);
        }
    }

    internal static IReadOnlyList<JsonObject> BuildFramePayloads(FrameLoop loop, int picId)
    {
        loop.Validate();
        return loop.Frames.Select((frame, offset) => new JsonObject
        {
            ["Command"] = "Draw/SendHttpGif",
            ["PicNum"] = loop.Frames.Count,
            ["PicWidth"] = 64,
            ["PicOffset"] = offset,
            ["PicID"] = picId,
            ["PicSpeed"] = frame.DurationMs,
            ["PicData"] = Convert.ToBase64String(frame.Pixels),
        }).ToArray();
    }

    internal FrameLoop BuildFallbackLoop(FrameLoop loop)
    {
        loop.Validate();
        var count = Math.Min(settings.FallbackMaximumFrames, loop.Frames.Count);
        var frames = Enumerable.Range(0, count).Select(index =>
        {
            var source = loop.Frames[index * loop.Frames.Count / count];
            return new RgbFrame(source.Pixels, settings.GifFrameDurationMs);
        }).ToArray();
        return new FrameLoop(frames).Validate();
    }

    private async Task<FrameLoop> PushFramesFallback(Uri endpoint, FrameLoop source, CancellationToken ct)
    {
        var loop = BuildFallbackLoop(source);
        await Post(endpoint, new JsonObject { ["Command"] = "Draw/ResetHttpGifId" }, ct);
        foreach (var payload in BuildFramePayloads(loop, picId: 1)) await Post(endpoint, payload, ct);
        return loop;
    }

    private async Task Post(Uri endpoint, JsonObject payload, CancellationToken ct)
    {
        // The Pixoo's embedded HTTP server cannot read chunked bodies (PostAsJsonAsync streams without a
        // Content-Length) and answers HTTP 200 with {"error_code":"Request data illegal json"}. Send a
        // buffered body and treat any non-zero error_code as a failed push.
        using var content = new StringContent(payload.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(endpoint, content, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);
        EnsureDeviceAccepted(payload["Command"]?.GetValue<string>() ?? "?", body);
    }

    internal static void EnsureDeviceAccepted(string command, string body)
    {
        JsonNode? code;
        try { code = JsonNode.Parse(body)?["error_code"]; }
        catch (System.Text.Json.JsonException) { throw new InvalidOperationException($"Pixoo returned non-JSON for {command}."); }
        var accepted = code is JsonValue value
            && ((value.TryGetValue<int>(out var number) && number == 0)
                || (value.TryGetValue<string>(out var text) && text == "0"));
        if (!accepted) throw new InvalidOperationException($"Pixoo rejected {command}: {code?.ToJsonString() ?? "no error_code"}");
    }

    private sealed class GifDeliveryException(string message) : Exception(message);
}
