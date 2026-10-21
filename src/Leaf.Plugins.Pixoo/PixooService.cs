using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Pixoo;

public sealed class PixooService(
    IEntityStore entities,
    IFaceRenderer renderer,
    EmulatorSink emulator,
    Pixoo64Sink hardware,
    IPushClock clock,
    MotionReplayScheduler motionReplays,
    IAgentEventMapper mapper,
    PortraitPackService portraits)
{
    public const string DeviceEntityType = "pixoo-device";
    private readonly ConcurrentDictionary<Guid, FaceStateMachine> states = new();
    private sealed record QueueSlot(bool Hardware, DevicePushQueue Queue);
    private readonly ConcurrentDictionary<Guid, QueueSlot> queues = new();
    private readonly ConcurrentDictionary<Guid, long> gestureEpochs = new();

    public async Task<IReadOnlyList<AgentSummary>> AgentsAsync(PixooCaller caller, CancellationToken ct)
    {
        var agents = await entities.QueryAsync(new EntityQuery { TypeSlug = "agent", Limit = 100 }, ct);
        return agents.Where(a => caller.AgentId is null || caller.AgentId == a.Id.ToString())
            .Where(a => a.Data["confidential"]?.GetValue<bool>() != true || a.Data["owner_id"]?.GetValue<string>() == caller.UserId)
            .Select(a => new AgentSummary(a.Id, a.Name, AgentColor(a), AgentAvatar(a))).ToArray();
    }

    public async Task<IReadOnlyList<PixooDevice>> DevicesAsync(PixooCaller caller, CancellationToken ct)
    {
        var list = await entities.QueryAsync(new EntityQuery { TypeSlug = DeviceEntityType,
            DataEquals = new Dictionary<string, object?> { ["owner_id"] = caller.UserId }, Limit = 100 }, ct);
        if (caller.AgentId is not null) list = list.Where(item => item.Data["agent_id"]?.GetValue<string>() == caller.AgentId).ToArray();
        var devices = list.Select(Decode).ToArray();
        foreach (var agentId in devices.Select(item => item.AgentId).Distinct()) portraits.Schedule(agentId);
        return devices;
    }

    public async Task<DeviceSnapshot?> SnapshotAsync(PixooCaller caller, Guid? requested, CancellationToken ct)
    {
        var devices = await DevicesAsync(caller, ct);
        var device = requested is { } id ? devices.FirstOrDefault(d => d.Id == id) : devices.FirstOrDefault();
        if (device is null) return null;
        portraits.Schedule(device.AgentId);
        var snapshot = emulator.Snapshot(device);
        if (snapshot.Frames.Count > 0) return snapshot;
        var state = states.GetOrAdd(device.Id, _ => new());
        var program = renderer.Render(device.AgentId, state.Current, device.AgentColor, device.Brightness, state.Gesture);
        return snapshot with { State = state.Current, Gesture = state.Gesture, Frames = program.InitialLoop.Frames };
    }

    public async Task<PixooDevice> CreateAsync(PixooCaller caller, DeviceChange request, CancellationToken ct)
    {
        var validated = await Validate(caller, request, ct);
        var created = await entities.CreateAsync(DeviceEntityType, validated.Name, Encode(caller.UserId, validated), ct);
        var device = Decode(created);
        portraits.Schedule(device.AgentId);
        await Push(device, FaceState.Resting, null, "device.created", ct);
        return device;
    }

    public async Task<PixooDevice> UpdateAsync(PixooCaller caller, Guid id, DeviceChange request, CancellationToken ct)
    {
        var existing = await entities.GetAsync(id, ct);
        if (existing is null || existing.TypeSlug != DeviceEntityType || existing.Data["owner_id"]?.GetValue<string>() != caller.UserId)
            throw new KeyNotFoundException("Pixoo device not found.");
        var validated = await Validate(caller, request, ct);
        var updated = await entities.ReplaceDataAsync(id, Encode(caller.UserId, validated), validated.Name, ct);
        var device = Decode(updated);
        portraits.Schedule(device.AgentId);
        await Push(device, states.GetOrAdd(id, _ => new()).Current, null, "device.updated", ct);
        return device;
    }

    public async Task<bool> ApplySignalAsync(AgentSignal signal, CancellationToken ct)
    {
        if (!mapper.TryMap(signal, out var mapped) || mapped is null) return false;
        var entitiesForAgent = await entities.QueryAsync(new EntityQuery { TypeSlug = DeviceEntityType,
            DataEquals = new Dictionary<string, object?> { ["agent_id"] = mapped.AgentId.ToString() }, Limit = 100 }, ct);
        var devices = entitiesForAgent.Select(Decode).ToArray();
        foreach (var device in devices) await Push(device, mapped.State, mapped.Gesture, mapped.Cause, ct);
        return devices.Length > 0;
    }

    public async Task<DeviceSnapshot> SimulateAsync(PixooCaller caller, Guid deviceId, FaceState state, FaceGesture? gesture, CancellationToken ct)
    {
        var device = (await DevicesAsync(caller, ct)).FirstOrDefault(d => d.Id == deviceId)
            ?? throw new KeyNotFoundException("Pixoo device not found.");
        await Push(device, state, gesture, gesture?.ToString().ToLowerInvariant() ?? "simulate", ct);
        await queues[device.Id].Queue.DrainAsync(ct);
        return (await SnapshotAsync(caller, device.Id, ct))!;
    }

    public async Task<PortraitPackStatus> RebuildPortraitAsync(PixooCaller caller, Guid agentId, bool force, CancellationToken ct)
    {
        if (!(await AgentsAsync(caller, ct)).Any(agent => agent.Id == agentId)) throw new KeyNotFoundException("Agent not found.");
        await portraits.RebuildAsync(agentId, force, ct);
        return portraits.Status(agentId);
    }

    public async Task<PortraitPackStatus> PortraitStatusAsync(PixooCaller caller, Guid agentId, CancellationToken ct)
    {
        if (!(await AgentsAsync(caller, ct)).Any(agent => agent.Id == agentId)) throw new KeyNotFoundException("Agent not found.");
        return portraits.Status(agentId);
    }

    public async Task RefreshPortraitAsync(Guid agentId)
    {
        var entitiesForAgent = await entities.QueryAsync(new EntityQuery
        {
            TypeSlug = DeviceEntityType,
            DataEquals = new Dictionary<string, object?> { ["agent_id"] = agentId.ToString() },
            Limit = 100,
        }, CancellationToken.None);
        foreach (var device in entitiesForAgent.Select(Decode))
        {
            var state = states.GetOrAdd(device.Id, _ => new());
            await Push(device, state.Current, state.Gesture, "portrait.pack.updated", CancellationToken.None);
        }
    }

    private async Task Push(PixooDevice device, FaceState state, FaceGesture? gesture, string cause, CancellationToken ct)
    {
        var machine = states.GetOrAdd(device.Id, _ => new());
        if (!machine.Apply(new(device.AgentId, state, gesture, cause)) && cause is not ("device.updated" or "portrait.pack.updated")) return;
        var program = renderer.Render(device.AgentId, state, device.AgentColor, device.Brightness, gesture);
        var epoch = gestureEpochs.AddOrUpdate(device.Id, 1, (_, value) => value + 1);
        var hardwareOutput = device.Kind == DeviceKind.Pixoo64 && device.HardwareEnabled;
        if (queues.TryGetValue(device.Id, out var existing) && existing.Hardware != hardwareOutput
            && queues.TryRemove(device.Id, out var removed))
        {
            motionReplays.Cancel(device.Id);
            await removed.Queue.DisposeAsync();
        }
        var slot = queues.GetOrAdd(device.Id, _ => new(hardwareOutput, new(hardwareOutput ? hardware : emulator, clock)));
        motionReplays.Replace(device, state, gesture, cause, program, slot.Queue);
        if (gesture is not null) _ = CompleteGesture(device, state, gesture.Value, epoch, program.InitialLoop);
    }

    private async Task CompleteGesture(PixooDevice device, FaceState state, FaceGesture gesture, long epoch, FrameLoop loop)
    {
        try
        {
            await clock.Delay(TimeSpan.FromMilliseconds(loop.Frames.Sum(frame => frame.DurationMs)), CancellationToken.None);
            if (gestureEpochs.TryGetValue(device.Id, out var current) && current == epoch)
                await Push(device, state, null, $"{gesture.ToString().ToLowerInvariant()}.complete", CancellationToken.None);
        }
        catch (Exception) { /* Gesture cleanup is best-effort; later state changes supersede it. */ }
    }

    private async Task<DeviceChange> Validate(PixooCaller caller, DeviceChange request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 80) throw new ArgumentException("Device name is required (max 80 characters).");
        if (request.Brightness is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(request.Brightness));
        if (request.MinimumPushGapMs is < 250 or > 10000) throw new ArgumentOutOfRangeException(nameof(request.MinimumPushGapMs));
        var agent = await entities.GetAsync(request.AgentId, ct);
        if (agent is null || agent.TypeSlug != "agent") throw new ArgumentException("A real Agent entity is required.");
        if (caller.AgentId is not null && caller.AgentId != request.AgentId.ToString()) throw new UnauthorizedAccessException("Cannot bind another Agent.");
        if (agent.Data["confidential"]?.GetValue<bool>() == true && agent.Data["owner_id"]?.GetValue<string>() != caller.UserId)
            throw new KeyNotFoundException("Agent not found.");
        if (request.Kind == DeviceKind.Pixoo64 && request.HardwareEnabled && string.IsNullOrWhiteSpace(request.Host))
            throw new ArgumentException("An explicitly enabled Pixoo-64 needs a host.");
        return request with { Name = request.Name.Trim(), Host = string.IsNullOrWhiteSpace(request.Host) ? null : request.Host.Trim(), AgentColor = AgentColor(agent) };
    }

    private static JsonObject Encode(string ownerId, DeviceChange value) => Encode(ownerId, value.Name, value.Kind, value.Host, value.Brightness,
        value.AgentId, value.AgentColor, value.MinimumPushGapMs, value.HardwareEnabled);

    private static JsonObject Encode(string ownerId, string name, DeviceKind kind, string? host, int brightness, Guid agentId,
        string color, int gap, bool hardwareEnabled) => new()
    {
        ["owner_id"] = ownerId,
        ["name"] = name,
        ["kind"] = kind.ToString().ToLowerInvariant(),
        ["host"] = host,
        ["brightness"] = brightness,
        ["agent_id"] = agentId.ToString(),
        ["agent_color"] = color,
        ["minimum_push_gap_ms"] = gap,
        ["hardware_enabled"] = hardwareEnabled,
    };

    private static PixooDevice Decode(LeafEntity entity)
    {
        var d = entity.Data;
        if (!Guid.TryParse(d["agent_id"]?.GetValue<string>(), out var agentId)) throw new InvalidOperationException("Pixoo device has no valid Agent binding.");
        Enum.TryParse<DeviceKind>(d["kind"]?.GetValue<string>(), true, out var kind);
        return new(entity.Id, d["name"]?.GetValue<string>() ?? entity.Name, kind, d["host"]?.GetValue<string>(),
            d["brightness"]?.GetValue<int>() ?? 70, agentId, d["agent_color"]?.GetValue<string>() ?? "#7c5cff",
            d["minimum_push_gap_ms"]?.GetValue<int>() ?? 1000, d["hardware_enabled"]?.GetValue<bool>() ?? false);
    }

    private static string AgentColor(LeafEntity agent)
    {
        var value = agent.Data["color"]?.GetValue<string>() ?? "#7c5cff";
        if (!value.StartsWith('#'))
            value = value.ToLowerInvariant() switch { "teal" => "#43e0c2", "blue" => "#55a7ff", "rose" => "#ff6f91", "amber" => "#ffbd59", _ => "#7c5cff" };
        return value;
    }

    private static string? AgentAvatar(LeafEntity agent)
    {
        var value = agent.Data["avatar"] is JsonObject avatar
            ? avatar["url"]?.GetValue<string>() ?? avatar["filename"]?.GetValue<string>()
            : agent.Data["avatar"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.StartsWith("/", StringComparison.Ordinal) || Uri.TryCreate(value, UriKind.Absolute, out _)) return value;
        return $"/api/assets/{value}";
    }
}

public sealed record AgentSummary(Guid Id, string Name, string Color, string? AvatarUrl);

public sealed record DeviceChange(
    string Name,
    DeviceKind Kind,
    string? Host,
    int Brightness,
    Guid AgentId,
    int MinimumPushGapMs = 1000,
    bool HardwareEnabled = false,
    string AgentColor = "#7c5cff");
