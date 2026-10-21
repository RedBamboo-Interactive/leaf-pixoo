using System.Text.Json.Nodes;

namespace Leaf.Plugins.Pixoo;

/// <summary>
/// Folds Nova's content-free <c>nova.agent.activity</c> events into one face state per Agent.
/// Several discussions of the same Agent can run at once; the most expressive active phase wins
/// (speaking, then working, then thinking), and the Agent returns to its ambient state only when
/// every user-facing turn has gone idle. Automation and heartbeat turns never reach the face.
/// </summary>
public sealed class AgentActivityTracker
{
    public const string EventType = "nova.agent.activity";

    private readonly object gate = new();
    private readonly Dictionary<Guid, Dictionary<string, FaceState>> turns = [];

    /// <summary>Returns the Agent and its new aggregate state (null = ambient), or null when nothing changed.</summary>
    public (Guid AgentId, FaceState? State)? Apply(JsonObject payload)
    {
        if (!Guid.TryParse(Text(payload, "agentId"), out var agentId)) return null;
        if (payload["userFacing"]?.GetValue<bool>() != true) return null;
        var turn = Text(payload, "turn");
        if (string.IsNullOrWhiteSpace(turn)) return null;
        FaceState? phase = Text(payload, "phase") switch
        {
            "thinking" => FaceState.Thinking,
            "speaking" => FaceState.Speaking,
            "working" => FaceState.Working,
            "idle" => null,
            _ => throw new FormatException("unknown phase"),
        };

        lock (gate)
        {
            var before = Aggregate(agentId);
            if (!turns.TryGetValue(agentId, out var active)) turns[agentId] = active = [];
            if (phase is { } value) active[turn] = value;
            else active.Remove(turn);
            if (active.Count == 0) turns.Remove(agentId);
            var after = Aggregate(agentId);
            return before == after ? null : (agentId, after);
        }
    }

    private FaceState? Aggregate(Guid agentId)
    {
        if (!turns.TryGetValue(agentId, out var active) || active.Count == 0) return null;
        if (active.ContainsValue(FaceState.Speaking)) return FaceState.Speaking;
        if (active.ContainsValue(FaceState.Working)) return FaceState.Working;
        return FaceState.Thinking;
    }

    private static string? Text(JsonObject payload, string key)
        => payload[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
