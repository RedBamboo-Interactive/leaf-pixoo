using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Leaf.Plugins.Pixoo;

public interface IDisplaySink
{
    Task PushAsync(PixooDevice device, DisplayPush push, CancellationToken ct = default);
}

public sealed class EmulatorSink : IDisplaySink
{
    private sealed record Entry(DisplayPush Push, DateTimeOffset PushedAt);
    private readonly ConcurrentDictionary<Guid, Entry> current = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentQueue<PushReceipt>> history = new();

    public Task PushAsync(PixooDevice device, DisplayPush push, CancellationToken ct = default)
    {
        push.Loop.Validate();
        var now = DateTimeOffset.UtcNow;
        current[device.Id] = new(push, now);
        var queue = history.GetOrAdd(device.Id, _ => new());
        queue.Enqueue(new(now, push.State, push.Gesture, push.Cause, push.Loop.Frames.Count));
        while (queue.Count > 20) queue.TryDequeue(out _);
        return Task.CompletedTask;
    }

    public DeviceSnapshot Snapshot(PixooDevice device)
    {
        current.TryGetValue(device.Id, out var value);
        var frames = value?.Push.Loop.Frames ?? [];
        var receipts = history.TryGetValue(device.Id, out var queue) ? queue.Reverse().ToArray() : [];
        return new(device, value?.Push.State ?? FaceState.Dark, value?.Push.Gesture, frames, receipts);
    }
}

/// <summary>
/// Unverified local-only Pixoo-64 driver. It performs no discovery and no retries.
/// It is only selected for an explicitly enabled hardware device.
/// </summary>
public sealed class Pixoo64Sink(HttpClient http) : IDisplaySink
{
    public async Task PushAsync(PixooDevice device, DisplayPush push, CancellationToken ct = default)
    {
        if (!device.HardwareEnabled) throw new InvalidOperationException("Pixoo hardware output is disabled.");
        if (string.IsNullOrWhiteSpace(device.Host)) throw new InvalidOperationException("A Pixoo host is required.");
        push.Loop.Validate();
        var endpoint = new UriBuilder("http", device.Host) { Path = "/post" }.Uri;
        await Post(endpoint, new JsonObject { ["Command"] = "Draw/ResetHttpGifId" }, ct);
        foreach (var payload in BuildFramePayloads(push.Loop, picId: 1)) await Post(endpoint, payload, ct);
        await Post(endpoint, new JsonObject { ["Command"] = "Channel/SetBrightness", ["Brightness"] = Math.Clamp(device.Brightness, 0, 100) }, ct);
        await Post(endpoint, new JsonObject { ["Command"] = "Channel/OnOffScreen", ["OnOff"] = push.State == FaceState.Dark ? 0 : 1 }, ct);
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
}
