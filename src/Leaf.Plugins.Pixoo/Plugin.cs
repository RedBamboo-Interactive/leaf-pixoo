using System.Reflection;
using System.Text.Json;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Leaf.Plugins.Pixoo;

public sealed class PixooPlugin : ILeafPlugin
{
    public const string Id = "pixoo";
    public PluginManifest Manifest { get; } = LoadManifest();

    private static PluginManifest LoadManifest()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("plugin.json")
            ?? throw new InvalidOperationException("Embedded plugin.json is missing.");
        using var reader = new StreamReader(stream);
        return PluginManifest.Parse(reader.ReadToEnd());
    }

    public void ConfigureServices(IServiceCollection services, PluginContext ctx)
    {
        services.AddSingleton<IPushClock, SystemPushClock>();
        services.AddSingleton<IRotationRandom, SystemRotationRandom>();
        services.AddSingleton(PixooPlaybackSettings.FromEnvironment());
        services.AddSingleton(new CompositeSettings());
        services.AddSingleton<DisplayPlaybackStatusStore>();
        services.AddSingleton<CurrentDisplayStore>();
        services.AddSingleton<IGifEncoder, FfmpegGifEncoder>();
        services.AddSingleton<ILanGifHost, LanGifHost>();
        services.AddSingleton<ProceduralFaceRenderer>();
        services.AddSingleton<IAgentEventMapper, StrictAgentEventMapper>();
        services.AddSingleton<EmulatorSink>();
        services.AddSingleton(new PortraitClipSettings());
        services.AddSingleton<AnimationRotationScheduler>();
        services.AddSingleton<AmbientStateScheduler>();
        services.AddSingleton<IVideoFrameDecoder, FfmpegVideoFrameDecoder>();
        services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(4) });
        services.AddSingleton<Pixoo64Sink>();
        services.AddSingleton<IPixooHealthTransport>(sp => sp.GetRequiredService<Pixoo64Sink>());
        services.AddSingleton<PixooHealthMonitor>();
        services.AddSingleton(sp => new PortraitPackService(
            sp.GetRequiredKeyedService<IEntityStore>(Id),
            sp.GetRequiredService<IAssets>(),
            sp.GetKeyedService<IExecutionTokens>(Id),
            new HttpClient { Timeout = TimeSpan.FromSeconds(40) },
            sp.GetRequiredService<IVideoFrameDecoder>(),
            sp.GetRequiredService<PortraitClipSettings>()));
        services.AddSingleton<IPortraitPackCache>(sp => sp.GetRequiredService<PortraitPackService>());
        services.AddSingleton<IFaceRenderer>(sp => new PortraitFaceRenderer(
            sp.GetRequiredService<IPortraitPackCache>(),
            sp.GetRequiredService<ProceduralFaceRenderer>(),
            sp.GetRequiredService<CompositeSettings>()));
        services.AddSingleton(sp =>
        {
            var portraits = sp.GetRequiredService<PortraitPackService>();
            var service = new PixooService(
                sp.GetRequiredKeyedService<IEntityStore>(Id),
                sp.GetRequiredService<IFaceRenderer>(),
                sp.GetRequiredService<EmulatorSink>(),
                sp.GetRequiredService<Pixoo64Sink>(),
                sp.GetRequiredService<IPushClock>(),
                sp.GetRequiredService<AnimationRotationScheduler>(),
                sp.GetRequiredService<IAgentEventMapper>(),
                portraits,
                sp.GetRequiredService<DisplayPlaybackStatusStore>(),
                sp.GetRequiredService<CurrentDisplayStore>(),
                sp.GetRequiredService<PixooPlaybackSettings>(),
                sp.GetRequiredService<PixooHealthMonitor>());
            portraits.PackReady += service.RefreshPortraitAsync;
            return service;
        });
        services.AddSingleton(sp => new PixooEventBridge(
            sp.GetRequiredKeyedService<IPluginEvents>(Id),
            sp.GetRequiredService<PixooService>(),
            sp.GetRequiredService<PortraitPackService>()));
    }

    public Task OnStartupAsync(IPluginHost host, CancellationToken ct)
    {
        host.GetRequiredService<PixooEventBridge>().Start();
        var service = host.GetRequiredService<PixooService>();
        service.StartHealthMonitoring();
        host.GetRequiredService<AmbientStateScheduler>().Start(service.ApplyAmbientStateAsync);
        // Fire and forget: a slow or unreachable display must never delay Leaf startup.
        _ = Task.Run(async () =>
        {
            try { await service.ResumeAllAsync(CancellationToken.None); }
            catch (Exception) { /* Device and portrait status surfaces carry the failure. */ }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    public void MapEndpoints(RouteGroupBuilder group)
    {
        group.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (UnauthorizedAccessException ex) { return Results.Json(new { error = "forbidden", message = ex.Message }, statusCode: 403); }
            catch (KeyNotFoundException ex) { return Results.Json(new { error = "not_found", message = ex.Message }, statusCode: 404); }
            catch (ArgumentException ex) { return Results.Json(new { error = "invalid_request", message = ex.Message }, statusCode: 400); }
            catch (JsonException) { return Results.Json(new { error = "malformed_request" }, statusCode: 400); }
        });
        group.MapGet("/", () => Results.Ok(new
        {
            id = Id,
            contract = "leaf.pixoo/1",
            deviceModel = "64x64 uniform-timing GIF state programs; long pauses live inside each GIF",
            hardware = "Validated local Device/PlayTFGif delivery with confirmed LAN fetch; <=40 frame push fallback; no discovery or cloud",
            liveEvents = "identity-strict adapter present; current kernel does not expose Compute /ws frames to backend plugins",
        })).WithSummary("Describes the Pixoo plugin contract version and its device, hardware and live-event capabilities.");
        group.MapGet("/agents", async (HttpContext context, PixooService service, CancellationToken ct) => Results.Ok(await service.AgentsAsync(PixooCaller.From(context.User), ct)))
            .WithSummary("Lists the Agents the caller may bind a display to, with their colour and avatar.");
        group.MapGet("/devices", async (HttpContext context, PixooService service, CancellationToken ct) => Results.Ok(await service.DevicesAsync(PixooCaller.From(context.User), ct)))
            .WithSummary("Lists the caller's Pixoo displays, each bound to one Agent.");
        group.MapPost("/devices", async (HttpContext context, PixooService service, CancellationToken ct) =>
            Results.Ok(await service.CreateAsync(PixooCaller.From(context.User), await Read<DeviceChange>(context, ct), ct)))
            .WithSummary("Creates a Pixoo display bound to one Agent and pushes its first state. The JSON body requires name, kind (emulator or pixoo64), brightness and agentId; host, minimumPushGapMs, hardwareEnabled and agentColor are optional.");
        group.MapPut("/devices/{id:guid}", async (Guid id, HttpContext context, PixooService service, CancellationToken ct) =>
            Results.Ok(await service.UpdateAsync(PixooCaller.From(context.User), id, await Read<DeviceChange>(context, ct), ct)))
            .WithSummary("Replaces the settings of one of the caller's displays and pushes its current state again. The JSON body is the full device (same fields as creating one), not a partial patch.");
        group.MapGet("/emulator", async (Guid? deviceId, HttpContext context, PixooService service, CancellationToken ct) =>
            await service.SnapshotAsync(PixooCaller.From(context.User), deviceId, ct) is { } snapshot ? Results.Ok(snapshot) : Results.NotFound())
            .WithSummary("Returns the device, current face state, LED frames, push history and delivery status for one display, or the first display when deviceId is omitted. Responds 404 when the caller has no matching display.");
        group.MapGet("/devices/{id:guid}/playback", async (Guid id, HttpContext context, PixooService service, CancellationToken ct) =>
            Results.Ok(await service.PlaybackAsync(PixooCaller.From(context.User), id, ct)))
            .WithSummary("Returns the delivery status of one display, including any delivery error.");
        group.MapGet("/devices/{id:guid}/state", async (Guid id, HttpContext context, PixooService service, CancellationToken ct) =>
            Results.Ok(await service.DisplayStateAsync(PixooCaller.From(context.User), id, ct)))
            .WithSummary("Returns a compact summary of what one display is showing: face state, gesture, loop key, frame count and delivery status. Lighter than the emulator snapshot.");
        group.MapGet("/devices/{id:guid}/history", async (Guid id, HttpContext context, PixooService service, CancellationToken ct) =>
            Results.Ok(await service.HistoryAsync(PixooCaller.From(context.User), id, ct)))
            .WithSummary("Lists the bounded history of recent pushes to one display.");
        group.MapGet("/devices/{id:guid}/current.gif", async (Guid id, HttpContext context, PixooService service, CancellationToken ct) =>
        {
            var gif = await service.CurrentGifAsync(PixooCaller.From(context.User), id, ct);
            if (gif is null) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
            await CurrentGifHttp.WriteAsync(context, gif, ct);
        }).WithSummary("Returns the GIF that one display is currently looping, or 404 when nothing has been pushed to it yet.");
        group.MapPost("/devices/{id:guid}/simulate", async (Guid id, HttpContext context, PixooService service, CancellationToken ct) =>
        {
            var request = await Read<SimulateRequest>(context, ct);
            return Results.Ok(await service.SimulateAsync(PixooCaller.From(context.User), id, request.State, request.Gesture, ct));
        }).WithSummary("Forces one display into a face state, optionally with a one-shot gesture, and returns the resulting state summary. The JSON body requires state (dark, resting, listening, thinking, speaking, working, dreaming or asleep); gesture (arrive or react) is optional. This is the supported way to drive a display, and it changes what the physical device shows when hardware is enabled.");
        group.MapPost("/portrait/rebuild", async (HttpContext context, PixooService service, CancellationToken ct) =>
        {
            var request = await Read<PortraitRebuildRequest>(context, ct);
            return Results.Ok(await service.RebuildPortraitAsync(PixooCaller.From(context.User), request.AgentId, request.Force, ct));
        }).WithSummary("Rebuilds the cached portrait pack for one Agent from its current outfit image and returns the pack status. The JSON body requires agentId; force (default false) is optional. The caller must own a display bound to that Agent or be an administrator, and the rebuild can start GPU video jobs.");
        group.MapGet("/portrait/status", async (Guid agentId, HttpContext context, PixooService service, CancellationToken ct) =>
            Results.Ok(await service.PortraitStatusAsync(PixooCaller.From(context.User), agentId, ct)))
            .WithSummary("Returns the portrait pack and clip generation status for one Agent. Requires the agentId query parameter, and the caller must own a display bound to that Agent or be an administrator.");
    }

    private static async Task<T> Read<T>(HttpContext context, CancellationToken ct)
        => await JsonSerializer.DeserializeAsync<T>(context.Request.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web), ct)
            ?? throw new BadHttpRequestException("JSON body required.");

    public sealed record SimulateRequest(FaceState State, FaceGesture? Gesture);
    public sealed record PortraitRebuildRequest(Guid AgentId, bool Force = false);
}
