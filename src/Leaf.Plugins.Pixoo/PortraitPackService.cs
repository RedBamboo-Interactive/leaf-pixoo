using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Pixoo;

public sealed record PortraitPackStatus(
    Guid AgentId,
    string State,
    string? SourceAssetId,
    string? SourceHash,
    long? Seed,
    string? Error,
    DateTimeOffset UpdatedAt);

public sealed class PortraitPackService(
    IEntityStore entities,
    IAssets assets,
    IExecutionTokens executionTokens,
    HttpClient http) : IPortraitPackCache, IDisposable
{
    public const string PackEntityType = "pixoo-portrait-pack";
    public const string Workflow = "qwen-image-21-reference-e1003bc3";
    private const string GenerateUrl = "http://127.0.0.1:18804/api/apps/provider-comfyui/workflows/qwen-image-21-reference-e1003bc3/generate";
    private readonly PortraitPackBuilder builder = new();
    private readonly ConcurrentDictionary<Guid, PortraitPack> current = new();
    private readonly ConcurrentDictionary<Guid, Task<PortraitPack?>> builds = new();
    private readonly ConcurrentDictionary<Guid, PortraitPackStatus> statuses = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> failedAt = new();
    public event Func<Guid, Task>? PackReady;

    public PortraitPack? Current(Guid agentId) => current.GetValueOrDefault(agentId);
    public PortraitPackStatus Status(Guid agentId) => statuses.GetValueOrDefault(agentId)
        ?? new(agentId, "not-built", null, null, null, null, DateTimeOffset.UtcNow);

    public void Schedule(Guid agentId, bool sourceChanged = false)
    {
        if (!sourceChanged && failedAt.TryGetValue(agentId, out var failed)
            && DateTimeOffset.UtcNow - failed < TimeSpan.FromMinutes(5)) return;
        _ = GetBuild(agentId, force: false);
    }

    public async Task HandleEntityUpdatedAsync(JsonObject payload, CancellationToken ct = default)
    {
        var type = Text(payload, "typeSlug");
        var id = Text(payload, "id");
        if (type == "agent" && Guid.TryParse(id, out var agentId)) { Schedule(agentId, sourceChanged: true); return; }
        if (type != "outfit" || !Guid.TryParse(id, out var outfitId)) return;
        var agents = await entities.QueryAsync(new EntityQuery
        {
            TypeSlug = "agent",
            DataEquals = new Dictionary<string, object?> { ["outfit"] = outfitId.ToString() },
            Limit = 100,
        }, ct);
        foreach (var agent in agents) Schedule(agent.Id, sourceChanged: true);
    }

    public async Task<PortraitPack?> RebuildAsync(Guid agentId, bool force, CancellationToken ct)
        => await GetBuild(agentId, force).WaitAsync(ct);

    private Task<PortraitPack?> GetBuild(Guid agentId, bool force)
        => builds.GetOrAdd(agentId, id => Task.Run(async () =>
        {
            try { return await BuildAsync(id, force, CancellationToken.None); }
            finally { builds.TryRemove(id, out _); }
        }));

    private async Task<PortraitPack?> BuildAsync(Guid agentId, bool force, CancellationToken ct)
    {
        SourcePortrait? source = null;
        try
        {
            source = await ResolveSourceAsync(agentId, ct);
            if (source is null)
            {
                statuses[agentId] = new(agentId, "no-source", null, null, null, "Agent has no readable Outfit or avatar asset.", DateTimeOffset.UtcNow);
                return current.GetValueOrDefault(agentId);
            }
            var existing = current.GetValueOrDefault(agentId);
            if (!force && existing is not null && !PortraitSourceFingerprint.RequiresRebuild(existing.Provenance.SourceHash, source.Hash)) return existing;
            statuses[agentId] = new(agentId, "checking-cache", source.AssetId, source.Hash, null, null, DateTimeOffset.UtcNow);
            if (!force && await LoadCachedAsync(agentId, source.Hash, ct) is { } cached)
            {
                await ActivateAsync(agentId, cached);
                return cached;
            }

            statuses[agentId] = new(agentId, "preparing", source.AssetId, source.Hash, null, null, DateTimeOffset.UtcNow);
            var prepared = builder.Prepare(source.Bytes);
            var faithful = builder.BuildFallback(prepared, source.AssetId, DateTimeOffset.UtcNow);
            await using var cropStream = new MemoryStream(prepared.CropPng, writable: false);
            var cropAsset = await assets.UploadIdempotentAsync(cropStream, $"pixoo:{PortraitPackBuilder.PipelineVersion}:crop:{source.Hash}", $"pixoo-{agentId:N}-crop.png", "image/png", ct);
            PortraitPack? selected = null;
            Exception? lastFailure = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var seed = SeedFor(source.Hash, attempt + 3);
                statuses[agentId] = new(agentId, "generating", source.AssetId, source.Hash, seed, null, DateTimeOffset.UtcNow);
                try
                {
                    var redraw = await GenerateAsync(agentId, source, prepared, cropAsset.Url, seed, ct);
                    selected = builder.Build(redraw, prepared, source.AssetId, Workflow, seed, DateTimeOffset.UtcNow);
                    break;
                }
                catch (TerminalPortraitJobException ex)
                {
                    lastFailure = ex;
                }
                catch (InvalidDataException ex)
                {
                    lastFailure = ex;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastFailure = ex;
                    break;
                }
            }
            if (selected is null)
            {
                failedAt[agentId] = DateTimeOffset.UtcNow;
                await StoreAsync(source, cropAsset, prepared.CropPng, faithful, ct);
                await ActivateAsync(agentId, faithful);
                statuses[agentId] = new(agentId, "ready-fallback", source.AssetId, source.Hash, 0,
                    lastFailure?.Message ?? "No semantic redraw passed validation.", DateTimeOffset.UtcNow);
                return faithful;
            }
            statuses[agentId] = new(agentId, "storing", source.AssetId, source.Hash, selected.Provenance.Seed, null, DateTimeOffset.UtcNow);
            await StoreAsync(source, cropAsset, prepared.CropPng, selected, ct);
            await ActivateAsync(agentId, selected);
            failedAt.TryRemove(agentId, out _);
            return selected;
        }
        catch (Exception ex)
        {
            statuses[agentId] = new(agentId, "failed", source?.AssetId, source?.Hash, null, ex.Message, DateTimeOffset.UtcNow);
            return current.GetValueOrDefault(agentId);
        }
    }

    private async Task ActivateAsync(Guid agentId, PortraitPack pack)
    {
        current[agentId] = pack;
        statuses[agentId] = new(agentId, "ready", pack.Provenance.SourceAssetId, pack.Provenance.SourceHash,
            pack.Provenance.Seed, null, DateTimeOffset.UtcNow);
        var handlers = PackReady;
        if (handlers is null) return;
        foreach (Func<Guid, Task> handler in handlers.GetInvocationList())
        {
            try { await handler(agentId); }
            catch { /* A later snapshot/state change also adopts the ready pack. */ }
        }
    }

    private async Task<byte[]> GenerateAsync(Guid agentId, SourcePortrait source, PreparedPortrait prepared, string cropUrl, long seed, CancellationToken ct)
    {
        var token = executionTokens.IssueSystem(new PluginSystemExecutionTokenRequest(
            "plugin", PixooPlugin.Id, "Leaf Pixoo", "Build the selected Agent's peripheral portrait pack",
            [new("agent", EntityId: agentId.ToString()), new("asset", Id: source.AssetId)], TimeSpan.FromMinutes(15)));
        var outfitDescription = CompactDescription(source.OutfitDescription);
        var prompt = $"The person from <image1>: preserve the same person, face, eyes, hair, outfit, pose, expression and bust framing from the reference. Hair colour must remain {prepared.ColorHints.Hair}. Outer garment colour must remain {prepared.ColorHints.Jacket}. The visible inner garment or top must remain {prepared.ColorHints.InnerTop}; never turn it white or grey. Outfit description: {outfitDescription}. Redraw as refined hand-pixeled character portrait for a 64x64 LED matrix, flat limited 40-colour palette, crisp square pixel clusters, strong readable eyes and mouth, clean silhouette, simplified background colour shapes, no text or icons. Upper-body avatar portrait, full head visible, head, shoulders, chest and upper torso dominant in frame.";
        var inputs = new JsonObject
        {
            ["prompt"] = prompt,
            ["reference_image"] = $"http://127.0.0.1:18804{cropUrl}",
            ["reference_pixels"] = false,
            ["reference_resolution"] = 512,
            ["width"] = 1024,
            ["height"] = 1024,
            ["steps"] = 25,
            ["cfg"] = 1.0,
            ["negative_prompt"] = "low quality",
            ["seed"] = seed,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, GenerateUrl)
        {
            Content = JsonContent.Create(new JsonObject { ["wait"] = false, ["inputs"] = inputs }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        using var response = await http.SendAsync(request, ct);
        var admission = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Portrait job admission failed ({(int)response.StatusCode}).");
        var jobId = JsonNode.Parse(admission)?["jobId"]?.GetValue<string>();
        if (!Guid.TryParse(jobId, out _)) throw new InvalidOperationException("Portrait job admission returned no durable job id.");

        JsonObject? job;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(12));
        var bounded = deadline.Token;
        do
        {
            using var poll = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:18800/jobs/{jobId}?waitSeconds=25");
            poll.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            using var polled = await http.SendAsync(poll, bounded);
            polled.EnsureSuccessStatusCode();
            job = JsonNode.Parse(await polled.Content.ReadAsStringAsync(bounded)) as JsonObject;
        } while (job?["terminal"]?.GetValue<bool>() != true);
        var status = job?["status"]?.GetValue<string>();
        if (!string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase))
            throw new TerminalPortraitJobException($"Portrait job {jobId} ended as {status ?? "unknown"}.");
        var outputPath = job?["outputs"]?[0]?["url"]?.GetValue<string>() ?? $"/image-gen/jobs/{jobId}/output";
        using var output = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:18800{outputPath}");
        output.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        using var downloaded = await http.SendAsync(output, bounded);
        downloaded.EnsureSuccessStatusCode();
        return await downloaded.Content.ReadAsByteArrayAsync(bounded);
    }

    private async Task StoreAsync(SourcePortrait source, AssetRef cropAsset, byte[] cropPng, PortraitPack pack, CancellationToken ct)
    {
        var stem = $"pixoo:{pack.Provenance.PipelineVersion}:{pack.Provenance.SourceHash}:{pack.Provenance.Seed}";
        var basePng = PortraitPackBuilder.EncodePng(pack.BasePixels);
        var fallbackPng = PortraitPackBuilder.EncodePng(pack.FallbackPixels);
        var maskPng = PortraitPackBuilder.EncodeMaskPng(pack.FigureMask);
        await using var baseStream = new MemoryStream(basePng, false);
        await using var fallbackStream = new MemoryStream(fallbackPng, false);
        await using var maskStream = new MemoryStream(maskPng, false);
        var baseAsset = await assets.UploadIdempotentAsync(baseStream, $"{stem}:base", "pixoo-portrait-base.png", "image/png", ct);
        var fallbackAsset = await assets.UploadIdempotentAsync(fallbackStream, $"pixoo:{pack.Provenance.PipelineVersion}:{pack.Provenance.SourceHash}:fallback", "pixoo-portrait-fallback.png", "image/png", ct);
        var maskAsset = await assets.UploadIdempotentAsync(maskStream, $"{stem}:mask", "pixoo-portrait-figure-mask.png", "image/png", ct);
        var reviewAssets = new JsonArray();
        foreach (var review in PortraitReview.Create(pack, cropPng, source.Color))
        {
            await using var reviewStream = new MemoryStream(review.Bytes, writable: false);
            var reviewAsset = await assets.UploadIdempotentAsync(reviewStream, $"{stem}:review:{review.FileName}",
                $"pixoo-{review.FileName}", "image/png", ct);
            reviewAssets.Add(new JsonObject { ["name"] = review.FileName, ["assetId"] = reviewAsset.AssetId });
        }
        var manifest = new JsonObject
        {
            ["schema"] = "leaf.pixoo.portrait-pack/v2",
            ["pipelineVersion"] = pack.Provenance.PipelineVersion,
            ["sourceAssetId"] = source.AssetId,
            ["sourceHash"] = source.Hash,
            ["workflow"] = pack.Provenance.Workflow,
            ["seed"] = pack.Provenance.Seed,
            ["generatedAt"] = pack.Provenance.GeneratedAt,
            ["detectedGridPitch"] = pack.Provenance.DetectedGridPitch,
            ["cropAsset"] = cropAsset.AssetId,
            ["baseAsset"] = baseAsset.AssetId,
            ["fallbackAsset"] = fallbackAsset.AssetId,
            ["figureMaskAsset"] = maskAsset.AssetId,
            ["paletteSize"] = pack.PaletteSize,
            ["faceContrast"] = pack.FaceContrast,
            ["colorMatch"] = ColorMatchJson(pack.ColorMatch),
            ["regions"] = RegionsJson(pack.Regions),
            ["reviewAssets"] = reviewAssets,
        };
        var manifestBytes = Encoding.UTF8.GetBytes(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        await using var manifestStream = new MemoryStream(manifestBytes, false);
        var manifestAsset = await assets.UploadIdempotentAsync(manifestStream, $"{stem}:manifest", "pixoo-portrait-pack.json", "application/json", ct);
        var data = new JsonObject
        {
            ["owner_id"] = source.OwnerId,
            ["agent_id"] = source.AgentId.ToString(),
            ["source_asset_id"] = source.AssetId,
            ["source_hash"] = source.Hash,
            ["pipeline_version"] = pack.Provenance.PipelineVersion,
            ["workflow"] = pack.Provenance.Workflow,
            ["seed"] = pack.Provenance.Seed,
            ["generated_at"] = pack.Provenance.GeneratedAt,
            ["crop_asset"] = cropAsset.AssetId,
            ["base_asset"] = baseAsset.AssetId,
            ["fallback_asset"] = fallbackAsset.AssetId,
            ["figure_mask_asset"] = maskAsset.AssetId,
            ["manifest_asset"] = manifestAsset.AssetId,
            ["palette_size"] = pack.PaletteSize,
            ["face_contrast"] = pack.FaceContrast,
            ["color_match"] = ColorMatchJson(pack.ColorMatch),
            ["regions"] = RegionsJson(pack.Regions),
        };
        await entities.CreateAsync(PackEntityType, $"{source.AgentName} portrait {source.Hash[..8]}", data, ct);
    }

    private async Task<PortraitPack?> LoadCachedAsync(Guid agentId, string hash, CancellationToken ct)
    {
        var matches = await entities.QueryAsync(new EntityQuery
        {
            TypeSlug = PackEntityType,
            DataEquals = new Dictionary<string, object?> { ["agent_id"] = agentId.ToString(), ["source_hash"] = hash,
                ["pipeline_version"] = PortraitPackBuilder.PipelineVersion },
            Limit = 20,
        }, ct);
        foreach (var entity in matches.OrderByDescending(item => item.CreatedAt))
        {
            var baseId = Text(entity.Data, "base_asset"); var fallbackId = Text(entity.Data, "fallback_asset"); var maskId = Text(entity.Data, "figure_mask_asset");
            if (baseId is null || fallbackId is null || maskId is null) continue;
            var baseFile = await assets.ReadAsync(baseId, ct); var fallbackFile = await assets.ReadAsync(fallbackId, ct); var maskFile = await assets.ReadAsync(maskId, ct);
            if (baseFile is null || fallbackFile is null || maskFile is null || entity.Data["regions"] is not JsonObject regionData) continue;
            try
            {
                var regions = Regions(regionData);
                var basePixels = PortraitPackBuilder.DecodePng(baseFile.Bytes);
                var fallbackPixels = PortraitPackBuilder.DecodePng(fallbackFile.Bytes);
                var mask = PortraitPackBuilder.DecodeMaskPng(maskFile.Bytes);
                var provenance = new PortraitProvenance(Text(entity.Data, "source_asset_id") ?? "", hash, PortraitPackBuilder.PipelineVersion,
                    Text(entity.Data, "workflow") ?? Workflow, entity.Data["seed"]?.GetValue<long>() ?? 0,
                    entity.Data["generated_at"]?.GetValue<DateTimeOffset>() ?? entity.CreatedAt, null);
                var validation = builder.Validate(basePixels, regions);
                var metrics = entity.Data["color_match"] is JsonObject storedMetrics
                    ? ColorMatch(storedMetrics)
                    : PortraitColorMatcher.Measure(basePixels, regions, mask, fallbackPixels, regions, mask);
                if (validation.Valid) return new(basePixels, fallbackPixels, mask, regions, provenance, validation.PaletteSize, validation.FaceContrast, metrics);
            }
            catch { /* Ignore malformed historical candidates. */ }
        }
        return null;
    }

    private async Task<SourcePortrait?> ResolveSourceAsync(Guid agentId, CancellationToken ct)
    {
        var agent = await entities.GetAsync(agentId, ct);
        if (agent?.TypeSlug != "agent") return null;
        string? reference = null;
        string? outfitDescription = null;
        if (Guid.TryParse(Text(agent.Data, "outfit"), out var outfitId))
        {
            var outfit = await entities.GetAsync(outfitId, ct);
            if (outfit?.TypeSlug == "outfit")
            {
                reference = Text(outfit.Data, "asset");
                outfitDescription = Text(outfit.Data, "description") ?? Text(outfit.Data, "prompt") ?? outfit.Name;
            }
        }
        reference ??= Avatar(agent.Data);
        var assetId = AssetId(reference);
        if (assetId is null) return null;
        var file = await assets.ReadAsync(assetId, ct);
        if (file is null || !file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return null;
        return new(agentId, agent.Name, Text(agent.Data, "owner_id") ?? "system", Text(agent.Data, "color") ?? "#7c5cff",
            assetId, file.Bytes, PortraitSourceFingerprint.Hash(file.Bytes), outfitDescription);
    }

    private static long SeedFor(string hash, int attempt)
    {
        var bytes = Convert.FromHexString(hash[..16]);
        var value = BinaryPrimitives.ReadUInt64LittleEndian(bytes) ^ (0x9E3779B97F4A7C15UL * (ulong)(attempt + 1));
        return (long)(value % int.MaxValue) + 1;
    }

    private static JsonObject RegionsJson(PortraitRegions r) => new()
    {
        ["face"] = RegionJson(r.Face), ["leftEye"] = RegionJson(r.LeftEye), ["rightEye"] = RegionJson(r.RightEye),
        ["mouth"] = RegionJson(r.Mouth), ["torso"] = RegionJson(r.Torso), ["subject"] = RegionJson(r.Subject),
    };
    private static JsonObject RegionJson(PixelRegion r) => new() { ["x"] = r.X, ["y"] = r.Y, ["width"] = r.Width, ["height"] = r.Height };
    private static JsonObject ColorMatchJson(PortraitColorMatchMetrics m) => new()
    {
        ["hairHue"] = m.HairHue, ["referenceHairHue"] = m.ReferenceHairHue,
        ["hairLightness"] = m.HairLightness, ["referenceHairLightness"] = m.ReferenceHairLightness,
        ["hairSamples"] = m.HairSamples,
        ["hairHueDelta"] = m.HairHueDelta, ["hairLightnessDelta"] = m.HairLightnessDelta,
        ["garmentHue"] = m.GarmentHue, ["referenceGarmentHue"] = m.ReferenceGarmentHue,
        ["garmentLightness"] = m.GarmentLightness, ["referenceGarmentLightness"] = m.ReferenceGarmentLightness,
        ["garmentSamples"] = m.GarmentSamples,
        ["garmentHueDelta"] = m.GarmentHueDelta, ["garmentLightnessDelta"] = m.GarmentLightnessDelta,
        ["jacketHue"] = m.JacketHue, ["referenceJacketHue"] = m.ReferenceJacketHue,
        ["jacketLightness"] = m.JacketLightness, ["referenceJacketLightness"] = m.ReferenceJacketLightness,
        ["jacketSamples"] = m.JacketSamples,
    };
    private static PortraitColorMatchMetrics ColorMatch(JsonObject value) => new(
        Number(value, "hairHue"), Number(value, "referenceHairHue"), Number(value, "hairLightness"), Number(value, "referenceHairLightness"), Integer(value, "hairSamples"),
        Number(value, "garmentHue"), Number(value, "referenceGarmentHue"), Number(value, "garmentLightness"), Number(value, "referenceGarmentLightness"), Integer(value, "garmentSamples"),
        Number(value, "jacketHue"), Number(value, "referenceJacketHue"), Number(value, "jacketLightness"), Number(value, "referenceJacketLightness"), Integer(value, "jacketSamples"));
    private static PortraitRegions Regions(JsonObject value) => new(Region(value, "face"), Region(value, "leftEye"), Region(value, "rightEye"), Region(value, "mouth"), Region(value, "torso"), Region(value, "subject"));
    private static PixelRegion Region(JsonObject value, string key)
    {
        var item = value[key] as JsonObject ?? throw new InvalidDataException($"Missing {key} region.");
        return new PixelRegion(item["x"]!.GetValue<int>(), item["y"]!.GetValue<int>(), item["width"]!.GetValue<int>(), item["height"]!.GetValue<int>()).Clamp();
    }
    private static string? Text(JsonObject value, string key) => value[key] is JsonValue node && node.TryGetValue<string>(out var text) ? text : null;
    private static double Number(JsonObject value, string key) => value[key]?.GetValue<double>() ?? 0;
    private static int Integer(JsonObject value, string key) => value[key]?.GetValue<int>() ?? 0;
    private static string CompactDescription(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "not available";
        var compact = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact[..Math.Min(500, compact.Length)];
    }
    private static string? Avatar(JsonObject value) => Text(value, "avatar") ?? (value["avatar"] as JsonObject is { } avatar ? Text(avatar, "filename") ?? Text(avatar, "url") : null);
    private static string? AssetId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)) value = uri.AbsolutePath;
        return Uri.UnescapeDataString(value.TrimEnd('/').Split('/').Last());
    }

    public void Dispose() => http.Dispose();

    private sealed record SourcePortrait(Guid AgentId, string AgentName, string OwnerId, string Color, string AssetId, byte[] Bytes, string Hash, string? OutfitDescription);
    private sealed class TerminalPortraitJobException(string message) : Exception(message);
}
