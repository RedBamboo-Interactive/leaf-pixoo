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
        }));
        group.MapGet("/agents", async (HttpContext context, PixooService service, CancellationToken ct) => Results.Ok(await service.AgentsAsync(PixooCaller.From(context.User), ct)));
        group.MapGet("/devices", async (HttpContext context, PixooService service, CancellationToken ct) => Results.Ok(await service.DevicesAsync(PixooCaller.From(context.User), ct)));
        group.MapPost("/devices", async (HttpContext context, PixooService service, CancellationToken ct) =>
            Results.Ok(await service.CreateAsync(PixooCaller.From(context.User), await Read<DeviceChange>(context, ct), ct)));
        group.MapPut("/devices/{id:guid}", async (Guid id, HttpContext context, PixooService service, CancellationToken ct) =>
            Results.Ok(await service.UpdateAsync(PixooCaller.From(context.User), id, await Read<DeviceChange>(context, ct), ct)));
        group.MapGet("/emulator", async (Guid? deviceId, HttpContext context, PixooService service, CancellationToken ct) =>
            await service.SnapshotAsync(PixooCaller.From(context.User), deviceId, ct) is { } snapshot ? Results.Ok(snapshot) : Results.NotFound());
        group.MapGet("/devices/{id:guid}/playback", async (Guid id, HttpContext context, PixooService service, CancellationToken ct) =>
            Results.Ok(await service.PlaybackAsync(PixooCaller.From(context.User), id, ct)));
        group.MapGet("/devices/{id:guid}/state", async (Guid id, HttpContext context, PixooService service, CancellationToken ct) =>
            Results.Ok(await service.DisplayStateAsync(PixooCaller.From(context.User), id, ct)));
        group.MapGet("/devices/{id:guid}/history", async (Guid id, HttpContext context, PixooService service, CancellationToken ct) =>
            Results.Ok(await service.HistoryAsync(PixooCaller.From(context.User), id, ct)));
        group.MapGet("/devices/{id:guid}/current.gif", async (Guid id, HttpContext context, PixooService service, CancellationToken ct) =>
        {
            var gif = await service.CurrentGifAsync(PixooCaller.From(context.User), id, ct);
            if (gif is null) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
            await CurrentGifHttp.WriteAsync(context, gif, ct);
        });
        group.MapPost("/devices/{id:guid}/simulate", async (Guid id, HttpContext context, PixooService service, CancellationToken ct) =>
        {
            var request = await Read<SimulateRequest>(context, ct);
            return Results.Ok(await service.SimulateAsync(PixooCaller.From(context.User), id, request.State, request.Gesture, ct));
        });
        group.MapPost("/portrait/rebuild", async (HttpContext context, PixooService service, CancellationToken ct) =>
        {
            var request = await Read<PortraitRebuildRequest>(context, ct);
            return Results.Ok(await service.RebuildPortraitAsync(PixooCaller.From(context.User), request.AgentId, request.Force, ct));
        });
        group.MapGet("/portrait/status", async (Guid agentId, HttpContext context, PixooService service, CancellationToken ct) =>
            Results.Ok(await service.PortraitStatusAsync(PixooCaller.From(context.User), agentId, ct)));
    }

    private static async Task<T> Read<T>(HttpContext context, CancellationToken ct)
        => await JsonSerializer.DeserializeAsync<T>(context.Request.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web), ct)
            ?? throw new BadHttpRequestException("JSON body required.");

    public sealed record SimulateRequest(FaceState State, FaceGesture? Gesture);
    public sealed record PortraitRebuildRequest(Guid AgentId, bool Force = false);
}
