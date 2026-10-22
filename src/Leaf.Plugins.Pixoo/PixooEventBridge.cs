using System.Text.Json.Nodes;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Pixoo;

/// <summary>
/// Adapts identity-bearing kernel events if/when the kernel publishes them to
/// IPluginEvents. In the current kernel, Compute events are only relayed to /ws,
/// so this bridge deliberately produces no guessed attribution.
/// </summary>
public sealed class PixooEventBridge(IPluginEvents events, PixooService service, PortraitPackService portraits) : IDisposable
{
    private readonly List<IDisposable> subscriptions = [];
    private readonly AgentActivityTracker activity = new();

    public void Start()
    {
        if (subscriptions.Count > 0) return;
        foreach (var type in new[] { "session.created", "session.updated", "session.stream", "session.ended", "job.updated" })
            subscriptions.Add(events.Subscribe(type, Handle));
        subscriptions.Add(events.Subscribe("entity.updated", HandlePortrait));
        subscriptions.Add(events.Subscribe("agent.avatar-changed", HandlePortrait));
        subscriptions.Add(events.Subscribe(AgentActivityTracker.EventType, HandleActivity));
    }

    /// <summary>
    /// Nova's content-free activity signal. Bus handlers run inline in the publisher, so only fold
    /// the state here and hand the device work off; a slow display must never hold Nova up.
    /// </summary>
    internal Task HandleActivity(PluginEvent evt)
    {
        if (evt.SourcePlugin != "nova") return Task.CompletedTask;
        (Guid AgentId, FaceState? State)? change;
        try { change = activity.Apply(evt.Payload); }
        catch (Exception) { return Task.CompletedTask; }
        if (change is not { } value) return Task.CompletedTask;
        // A Nova turn flips between thinking, speaking and working every second or two. Every change is
        // a new GIF the Pixoo must download, and back-to-back swaps rebooted the device. Only a state
        // that has held for the settle time reaches the displays; a newer change restarts the wait.
        var pending = new CancellationTokenSource();
        if (settling.TryGetValue(value.AgentId, out var previous)) previous.Cancel();
        settling[value.AgentId] = pending;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ActivitySettle, pending.Token);
                await service.ApplyAgentActivityAsync(value.AgentId, value.State, CancellationToken.None);
            }
            catch (OperationCanceledException) { /* Superseded by a newer activity change. */ }
            catch (Exception) { /* Device status surfaces delivery failures. */ }
        });
        return Task.CompletedTask;
    }

    internal static TimeSpan ActivitySettle { get; set; } = TimeSpan.FromSeconds(3);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, CancellationTokenSource> settling = new();

    private async Task HandlePortrait(PluginEvent evt)
    {
        if (evt.EventType == "agent.avatar-changed")
        {
            if (Guid.TryParse(Str(evt.Payload, "agentId"), out var agentId)) portraits.Schedule(agentId, sourceChanged: true);
            return;
        }
        await portraits.HandleEntityUpdatedAsync(evt.Payload, CancellationToken.None);
    }

    internal Task Handle(PluginEvent evt)
    {
        if (evt.SourcePlugin is not ("os" or "redcompute")) return Task.CompletedTask;
        var payload = evt.Payload;
        var nested = payload["event"] as JsonObject;
        var provenance = payload["creationProvenance"] as JsonObject;
        var actor = provenance?["actor"] as JsonObject;
        var entrypoint = provenance?["origin"]?["entrypoint"] as JsonObject;
        var context = provenance?["context"] as JsonArray;
        var background = context?.OfType<JsonObject>().Any(x => IsBackground(Str(x, "kind"))) == true;
        var source = Str(payload, "source") ?? Str(entrypoint, "kind");
        var userFacing = payload["userFacing"]?.GetValue<bool?>();
        if (background || IsBackground(source)) userFacing = false;
        var signal = new AgentSignal(
            evt.EventType,
            Str(payload, "sessionId") ?? Str(payload, "id"),
            Str(payload, "ownerAgentId") ?? Str(payload, "owner_agent_id"),
            source,
            userFacing,
            Str(payload, "status"),
            Str(nested, "type") ?? Str(payload, "streamKind"),
            Str(payload, "capabilitySlug") ?? Str(payload, "capability"),
            Str(actor, "entityId"));
        return service.ApplySignalAsync(signal, CancellationToken.None);
    }

    private static string? Str(JsonObject? value, string key) => value?[key]?.GetValue<string>();
    private static bool IsBackground(string? value) => value is not null &&
        new[] { "automation", "heartbeat", "scheduled", "schedule" }
            .Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));

    public void Dispose()
    {
        foreach (var subscription in subscriptions) subscription.Dispose();
        subscriptions.Clear();
    }
}
