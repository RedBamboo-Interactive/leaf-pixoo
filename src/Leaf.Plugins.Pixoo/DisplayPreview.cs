using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;

namespace Leaf.Plugins.Pixoo;

public sealed record CurrentDisplayLoop(
    string LoopKey,
    DisplayPush Push,
    byte[] GifBytes,
    int FrameDurationMs,
    DateTimeOffset UpdatedAt);

public sealed record DisplayStateSummary(
    FaceState State,
    FaceGesture? Gesture,
    string? LoopKey,
    int FrameCount,
    int FrameDurationMs,
    DisplayPlaybackStatus Playback);

public sealed record CurrentGif(string LoopKey, byte[] Bytes);

/// <summary>The presentation that was actually accepted by a display sink.</summary>
public sealed class CurrentDisplayStore
{
    private readonly ConcurrentDictionary<Guid, CurrentDisplayLoop> current = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentQueue<PushReceipt>> history = new();
    private long revision;

    public CurrentDisplayLoop Set(PixooDevice device, DisplayPush push, FrameLoop deliveredLoop,
        byte[] gifBytes, int frameDurationMs, DateTimeOffset? updatedAt = null)
    {
        deliveredLoop.Validate();
        if (gifBytes.Length == 0) throw new ArgumentException("Current display GIF cannot be empty.", nameof(gifBytes));
        var sequence = Interlocked.Increment(ref revision);
        var digest = Convert.ToHexString(SHA256.HashData(gifBytes).AsSpan(0, 6)).ToLowerInvariant();
        var key = $"{sequence:x16}-{digest}";
        var delivered = push with { Loop = deliveredLoop };
        var value = new CurrentDisplayLoop(key, delivered, gifBytes, frameDurationMs,
            updatedAt ?? DateTimeOffset.UtcNow);
        current[device.Id] = value;
        var receipts = history.GetOrAdd(device.Id, _ => new());
        receipts.Enqueue(new(value.UpdatedAt, push.State, push.Gesture, push.Cause, deliveredLoop.Frames.Count));
        while (receipts.Count > 20) receipts.TryDequeue(out _);
        return value;
    }

    public CurrentDisplayLoop? Get(Guid deviceId) => current.GetValueOrDefault(deviceId);

    public IReadOnlyList<PushReceipt> History(Guid deviceId)
        => history.TryGetValue(deviceId, out var values) ? values.Reverse().ToArray() : [];
}

internal static class CurrentGifHttp
{
    public static async Task WriteAsync(HttpContext context, CurrentGif gif, CancellationToken ct)
    {
        var etag = $"\"{gif.LoopKey}\"";
        context.Response.Headers.ETag = etag;
        context.Response.Headers.CacheControl = "private";
        if (context.Request.Headers.IfNoneMatch.SelectMany(value => value?.Split(',') ?? [])
            .Any(value => string.Equals(value.Trim(), etag, StringComparison.Ordinal)))
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "image/gif";
        context.Response.ContentLength = gif.Bytes.Length;
        await context.Response.Body.WriteAsync(gif.Bytes, ct);
    }
}
