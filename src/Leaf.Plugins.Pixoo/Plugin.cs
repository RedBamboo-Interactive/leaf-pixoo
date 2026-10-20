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
        services.AddSingleton<ProceduralFaceRenderer>();
        services.AddSingleton<IAgentEventMapper, StrictAgentEventMapper>();
        services.AddSingleton<EmulatorSink>();
        services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(4) });
        services.AddSingleton<Pixoo64Sink>();
        services.AddSingleton(sp => new PortraitPackService(
            sp.GetRequiredKeyedService<IEntityStore>(Id),
            sp.GetRequiredKeyedService<IAssets>(Id),
            sp.GetRequiredKeyedService<IExecutionTokens>(Id),
            new HttpClient { Timeout = TimeSpan.FromSeconds(40) }));
        services.AddSingleton<IPortraitPackCache>(sp => sp.GetRequiredService<PortraitPackService>());
        services.AddSingleton<IFaceRenderer>(sp => new PortraitFaceRenderer(
            sp.GetRequiredService<IPortraitPackCache>(),
            sp.GetRequiredService<ProceduralFaceRenderer>()));
        services.AddSingleton(sp =>
        {
            var portraits = sp.GetRequiredService<PortraitPackService>();
            var service = new PixooService(
                sp.GetRequiredKeyedService<IEntityStore>(Id),
                sp.GetRequiredService<IFaceRenderer>(),
                sp.GetRequiredService<EmulatorSink>(),
                sp.GetRequiredService<Pixoo64Sink>(),
                sp.GetRequiredService<IPushClock>(),
                sp.GetRequiredService<IAgentEventMapper>(),
                portraits);
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
            deviceModel = "64x64 RGB multi-frame loops; state-change pushes; serialized latest-wins queue",
            hardware = "UNTESTED and disabled by default; explicit host only; no discovery or cloud",
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
    }

    private static async Task<T> Read<T>(HttpContext context, CancellationToken ct)
        => await JsonSerializer.DeserializeAsync<T>(context.Request.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web), ct)
            ?? throw new BadHttpRequestException("JSON body required.");

    public sealed record SimulateRequest(FaceState State, FaceGesture? Gesture);
    public sealed record PortraitRebuildRequest(Guid AgentId, bool Force = false);
}
