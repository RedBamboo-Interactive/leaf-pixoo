using System.Text.Json.Serialization;

namespace Leaf.Plugins.Pixoo;

[JsonConverter(typeof(JsonStringEnumConverter<FaceState>))]
public enum FaceState
{
    [JsonStringEnumMemberName("dark")] Dark,
    [JsonStringEnumMemberName("resting")] Resting,
    [JsonStringEnumMemberName("listening")] Listening,
    [JsonStringEnumMemberName("thinking")] Thinking,
    [JsonStringEnumMemberName("speaking")] Speaking,
    [JsonStringEnumMemberName("working")] Working,
    [JsonStringEnumMemberName("dreaming")] Dreaming,
    [JsonStringEnumMemberName("asleep")] Asleep,
}

[JsonConverter(typeof(JsonStringEnumConverter<FaceGesture>))]
public enum FaceGesture
{
    [JsonStringEnumMemberName("arrive")] Arrive,
    [JsonStringEnumMemberName("react")] React,
}

[JsonConverter(typeof(JsonStringEnumConverter<DeviceKind>))]
public enum DeviceKind
{
    [JsonStringEnumMemberName("emulator")] Emulator,
    [JsonStringEnumMemberName("pixoo64")] Pixoo64,
}

public sealed record RgbFrame(byte[] Pixels, int DurationMs)
{
    public const int Width = 64;
    public const int ByteCount = Width * Width * 3;

    public RgbFrame Validate()
    {
        if (Pixels.Length != ByteCount) throw new ArgumentException($"A frame must contain exactly {ByteCount} RGB bytes.");
        if (DurationMs < 20) throw new ArgumentOutOfRangeException(nameof(DurationMs));
        return this;
    }
}

public sealed record FrameLoop(IReadOnlyList<RgbFrame> Frames)
{
    public FrameLoop Validate()
    {
        if (Frames.Count == 0) throw new ArgumentException("A display loop needs at least one frame.");
        foreach (var frame in Frames) frame.Validate();
        return this;
    }
}

public enum DisplayPlaybackMode
{
    Continuous,
    OneShot,
}

/// <summary>
/// A complete state animation plus optional infrequently rotated alternatives.
/// Any pauses are duplicate frames inside the loop, never server-timed re-uploads.
/// </summary>
public sealed record DisplayProgram(
    FrameLoop Still,
    FrameLoop? Motion,
    DisplayPlaybackMode Mode,
    IReadOnlyList<FrameLoop>? Alternatives = null)
{
    public FrameLoop InitialLoop => Motion ?? Still;

    public DisplayProgram Validate()
    {
        Still.Validate();
        Motion?.Validate();
        if (Alternatives is not null) foreach (var alternative in Alternatives) alternative.Validate();
        return this;
    }
}

public sealed record DisplayPush(
    FrameLoop Loop,
    FaceState State,
    FaceGesture? Gesture,
    string Cause,
    DateTimeOffset CreatedAt,
    string AnimationName);

public sealed record PushReceipt(DateTimeOffset PushedAt, FaceState State, FaceGesture? Gesture, string Cause, int FrameCount);

public sealed record DisplayPlaybackStatus(
    string? PlayingGif,
    DateTimeOffset? LastFetchedAt,
    string? LastFetchedFrom,
    string? DeliveryError);

public sealed record PixooDevice(
    Guid Id,
    string Name,
    DeviceKind Kind,
    string? Host,
    int Brightness,
    Guid AgentId,
    string AgentColor,
    int MinimumPushGapMs = 1000,
    bool HardwareEnabled = false);

public sealed record DeviceSnapshot(
    PixooDevice Device,
    FaceState State,
    FaceGesture? Gesture,
    IReadOnlyList<RgbFrame> Frames,
    IReadOnlyList<PushReceipt> History,
    DisplayPlaybackStatus Playback);
