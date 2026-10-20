namespace Leaf.Plugins.Pixoo;

public sealed record AgentSignal(
    string EventType,
    string? SessionId = null,
    string? OwnerAgentId = null,
    string? Source = null,
    bool? UserFacing = null,
    string? Status = null,
    string? StreamKind = null,
    string? Capability = null,
    string? ActorAgentId = null);

public sealed record MappedFaceSignal(Guid AgentId, FaceState State, FaceGesture? Gesture, string Cause);

public interface IAgentEventMapper
{
    bool TryMap(AgentSignal signal, out MappedFaceSignal? mapped);
}

/// <summary>
/// Identity-strict mapper. A session is attributed only by its explicit owner_agent_id;
/// a job only by an explicit agent actor. Session IDs and titles are never identities.
/// </summary>
public sealed class StrictAgentEventMapper : IAgentEventMapper
{
    private static readonly string[] BackgroundMarkers = ["automation", "heartbeat", "scheduled", "schedule"];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Guid> userSessions = new(StringComparer.Ordinal);

    public bool TryMap(AgentSignal signal, out MappedFaceSignal? mapped)
    {
        mapped = null;
        if (IsBackground(signal) || signal.UserFacing == false) return false;
        Guid agentId;
        if (signal.EventType == "job.updated")
        {
            if (signal.UserFacing != true || !Guid.TryParse(signal.ActorAgentId, out agentId)) return false;
        }
        else if (Guid.TryParse(signal.OwnerAgentId, out agentId))
        {
            // Current RedCompute session events do not expose this user-facing bit. Fail
            // closed until both identity and interaction class are explicit.
            if (signal.UserFacing != true) return false;
            if (!string.IsNullOrWhiteSpace(signal.SessionId)) userSessions[signal.SessionId] = agentId;
        }
        else if (signal.SessionId is null || !userSessions.TryGetValue(signal.SessionId, out agentId)) return false;
        var state = signal.EventType switch
        {
            "session.created" => FaceState.Resting,
            "session.ended" => FaceState.Resting,
            "session.updated" when Is(signal.Status, "waiting", "idle") => FaceState.Listening,
            "session.updated" when Is(signal.Status, "active", "running") => FaceState.Thinking,
            "session.stream" when Is(signal.StreamKind, "thinking", "reasoning") => FaceState.Thinking,
            "session.stream" when Is(signal.StreamKind, "tool", "tool_call", "tool-use", "tool_use") => FaceState.Working,
            "session.stream" when Is(signal.StreamKind, "text", "assistant", "content") => FaceState.Speaking,
            "job.updated" when Is(signal.Capability, "tts", "speech") && Is(signal.Status, "running", "completed") => FaceState.Speaking,
            _ => (FaceState?)null,
        };
        if (state is null) return false;
        FaceGesture? gesture = signal.EventType == "session.created" ? FaceGesture.Arrive : null;
        mapped = new(agentId, state.Value, gesture, signal.EventType);
        if (signal.EventType == "session.ended" && signal.SessionId is not null) userSessions.TryRemove(signal.SessionId, out _);
        return true;
    }

    private static bool IsBackground(AgentSignal signal)
    {
        var source = signal.Source ?? "";
        return BackgroundMarkers.Any(marker => source.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool Is(string? value, params string[] expected)
        => value is not null && expected.Contains(value, StringComparer.OrdinalIgnoreCase);
}

public sealed class FaceStateMachine
{
    public FaceState Current { get; private set; } = FaceState.Dark;
    public FaceGesture? Gesture { get; private set; }

    public bool Apply(MappedFaceSignal signal)
    {
        var changed = Current != signal.State || Gesture != signal.Gesture;
        Current = signal.State;
        Gesture = signal.Gesture;
        return changed;
    }

    public bool Simulate(FaceState state, FaceGesture? gesture = null)
        => Apply(new(Guid.Empty, state, gesture, gesture?.ToString().ToLowerInvariant() ?? "simulate"));
}
