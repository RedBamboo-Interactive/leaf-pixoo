using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Pixoo;

public sealed record PortraitClipStatus(
    string Key,
    FaceState State,
    string? Variant,
    string Phase,
    int Attempts,
    string? JobId,
    string? Error,
    double? FrozenBackgroundFraction,
    DateTimeOffset UpdatedAt);

public sealed record PortraitPackStatus(
    Guid AgentId,
    string State,
    string? SourceAssetId,
    string? SourceHash,
    string? Error,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<PortraitClipStatus> Clips);

public sealed class PortraitPackService(
    IEntityStore entities,
    IAssets assets,
    IExecutionTokens? executionTokens,
    HttpClient http,
    IVideoFrameDecoder decoder,
    PortraitClipSettings settings) : IPortraitPackCache, IDisposable
{
    // RedLeaf rejects plugin system execution tokens longer than 15 minutes.
    internal static readonly TimeSpan ExecutionTokenLifetime = TimeSpan.FromMinutes(14);

    public const string PackEntityType = "pixoo-portrait-pack";
    public const string Workflow = "wan-22-i2v-first-last-portrait-film-60fps-4a256f5b";
    public const string QuietWorkflow = "wan-video-22-quiet-reportage-film-60fps-88c61e77";
    private const string SlowGenerateUrl = "http://127.0.0.1:18804/api/apps/provider-comfyui/workflows/wan-22-i2v-first-last-portrait-film-60fps-4a256f5b/generate";
    private const string RawGenerateUrl = "http://127.0.0.1:18800/image-gen/generate?async=true";
    private const string ComputeBaseUrl = "http://127.0.0.1:18800";
    private readonly PortraitPackBuilder builder = new();
    private readonly ConcurrentDictionary<Guid, PortraitPack> current = new();
    private readonly AgentSingleFlight<PortraitPack?> builds = new();
    private readonly ConcurrentDictionary<Guid, PortraitPackStatus> statuses = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, PortraitClipStatus>> clipStatuses = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> failedAt = new();
    public event Func<Guid, Task>? PackReady;

    public PortraitPack? Current(Guid agentId) => current.GetValueOrDefault(agentId);

    public PortraitPackStatus Status(Guid agentId)
    {
        var status = statuses.GetValueOrDefault(agentId)
            ?? new(agentId, "not-built", null, null, null, DateTimeOffset.UtcNow, []);
        return status with { Clips = ClipSnapshot(agentId) };
    }

    public void Schedule(Guid agentId, bool sourceChanged = false)
    {
        if (!sourceChanged && failedAt.TryGetValue(agentId, out var failed)
            && DateTimeOffset.UtcNow - failed < settings.AgentFailureCooldown) return;
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

    public Task<PortraitPack?> RebuildAsync(Guid agentId, bool force, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        SetOverall(agentId, "queued", current.GetValueOrDefault(agentId)?.Provenance.SourceAssetId,
            current.GetValueOrDefault(agentId)?.Provenance.SourceHash, null);
        _ = GetBuild(agentId, force);
        return Task.FromResult(current.GetValueOrDefault(agentId));
    }

    private Task<PortraitPack?> GetBuild(Guid agentId, bool force)
        => builds.RunAsync(agentId, () => BuildAsync(agentId, force, CancellationToken.None));

    private async Task<PortraitPack?> BuildAsync(Guid agentId, bool force, CancellationToken ct)
    {
        SourcePortrait? source = null;
        try
        {
            source = await ResolveSourceAsync(agentId, ct);
            if (source is null)
            {
                current.TryRemove(agentId, out _);
                SetOverall(agentId, "no-source", null, null, "Agent has no readable Outfit or avatar asset.");
                await NotifyReadyAsync(agentId);
                return null;
            }
            var active = current.GetValueOrDefault(agentId);
            if (!force && active is not null
                && !PortraitSourceFingerprint.RequiresRebuild(active.Provenance.SourceHash, source.Hash)
                && HasAllPrimaryClips(active)) return active;

            SetOverall(agentId, "checking-cache", source.AssetId, source.Hash, null);
            var prepared = builder.Prepare(source.Bytes);
            var stored = force ? null : await LoadCachedAsync(agentId, source.Hash, ct);
            PortraitPack pack;
            Guid? entityId;
            if (stored is not null)
            {
                pack = stored.Pack;
                entityId = stored.EntityId;
                await ActivateAsync(agentId, pack);
            }
            else
            {
                SetOverall(agentId, "preparing", source.AssetId, source.Hash, null);
                pack = builder.BuildBase(prepared, source.AssetId, DateTimeOffset.UtcNow);
                var initial = await StoreInitialAsync(source, prepared, pack, ct);
                entityId = initial.EntityId;
                await ActivateAsync(agentId, pack);
            }
            if (HasAllPrimaryClips(pack))
            {
                failedAt.TryRemove(agentId, out _);
                SetOverall(agentId, "ready", source.AssetId, source.Hash, null);
                return pack;
            }

            var fastWorkflow = await ResolveFastWorkflowAsync(ct);
            var fallbackReason = fastWorkflow.Reason;
            byte[] generationImage;
            VideoPlateGeometry? slowGeometry = null;
            string inputKind;
            if (fastWorkflow.Template is not null)
            {
                generationImage = prepared.CropPng;
                inputKind = "square";
            }
            else
            {
                var slowPlate = builder.BuildSlowFallbackPlate(source.Bytes, prepared);
                generationImage = slowPlate.Png;
                slowGeometry = slowPlate.Geometry;
                inputKind = "slow-fallback-plate";
            }
            await using var inputStream = new MemoryStream(generationImage, writable: false);
            var inputAsset = await assets.UploadIdempotentAsync(inputStream,
                $"pixoo:{PortraitPackBuilder.PipelineVersion}:{inputKind}:{source.Hash}",
                $"pixoo-{agentId:N}-{inputKind}.png", "image/png", ct);
            var failures = new List<string>();
            await PortraitClipPlan.ExecuteAsync(async (spec, attempt, token) =>
            {
                if (!force && pack.Clips.ContainsKey(spec.Key))
                {
                    var existing = pack.Clips[spec.Key].Provenance;
                    SetClip(agentId, spec, "ready", 0, existing.JobId, existing.Note,
                        existing.FrozenBackgroundFraction);
                    return true;
                }
                var seed = SeedFor(source.Hash, spec.Key, attempt);
                string? admittedJobId = null;
                SetClip(agentId, spec, fastWorkflow.Template is null ? "generating-slow-fallback" : "generating",
                    attempt, null, fallbackReason);
                try
                {
                    var prompt = PortraitClipPrompts.For(spec, source.OutfitDescription);
                    var generated = await GenerateClipAsync(agentId, source, spec, prompt, inputAsset.Url, seed,
                        fastWorkflow.Template, fallbackReason,
                        jobId =>
                        {
                            admittedJobId = jobId;
                            SetClip(agentId, spec,
                                fastWorkflow.Template is null ? "generating-slow-fallback" : "generating",
                                attempt, jobId, fallbackReason);
                        }, token);
                    SetClip(agentId, spec, "processing", attempt, generated.JobId, generated.Note);
                    var decoded = await decoder.DecodeAsync(generated.Bytes, generated.Width, generated.Height, token);
                    var sourceReference = PortraitPackBuilder.DecodeAndBoxResize(generationImage,
                        generated.Width, generated.Height);
                    var processed = generated.Fast
                        ? PortraitClipProcessor.ProcessMotionLoop(decoded, settings, pack.BasePixels, sourceReference)
                        : PortraitClipProcessor.ProcessMotionLoop(decoded, settings.SlowWidth, settings.SlowHeight,
                            slowGeometry!.OutputSquare, settings.SlowSampleStride,
                            settings.SlowFrameDurationMs, settings.MaximumLoopFrames, pack.BasePixels,
                            sourceReference, settings);
                    var clip = await StoreClipAsync(source, spec, prompt, seed, generated, processed, token);
                    pack = pack.WithClip(clip);
                    entityId = await StoreEntityAsync(source, prepared, pack, entityId, token);
                    SetClip(agentId, spec, "ready", attempt, generated.JobId, generated.Note,
                        processed.FrozenBackgroundFraction);
                    await ActivateAsync(agentId, pack);
                    return true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var message = Compact(ex.Message, 400);
                    SetClip(agentId, spec, attempt == settings.MaximumAttemptsPerClip ? "failed" : "cooldown",
                        attempt, admittedJobId, message);
                    if (attempt == settings.MaximumAttemptsPerClip) failures.Add($"{spec.Key}: {message}");
                    return false;
                }
            }, settings, ct);

            if (failures.Count > 0)
            {
                failedAt[agentId] = DateTimeOffset.UtcNow;
                SetOverall(agentId, "ready-partial", source.AssetId, source.Hash, string.Join("; ", failures));
            }
            else
            {
                failedAt.TryRemove(agentId, out _);
                SetOverall(agentId, "ready", source.AssetId, source.Hash, null);
            }
            return pack;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failedAt[agentId] = DateTimeOffset.UtcNow;
            SetOverall(agentId, "failed", source?.AssetId, source?.Hash, Compact(ex.Message, 500));
            return current.GetValueOrDefault(agentId);
        }
    }

    private async Task ActivateAsync(Guid agentId, PortraitPack pack)
    {
        current[agentId] = pack;
        await NotifyReadyAsync(agentId);
    }

    private async Task NotifyReadyAsync(Guid agentId)
    {
        var handlers = PackReady;
        if (handlers is null) return;
        foreach (Func<Guid, Task> handler in handlers.GetInvocationList())
        {
            try { await handler(agentId); }
            catch { /* A later snapshot/state change also adopts the latest pack. */ }
        }
    }

    private async Task<FastWorkflowResolution> ResolveFastWorkflowAsync(CancellationToken ct)
    {
        var firstLast = await entities.GetBySlugAsync("comfyui-workflow", Workflow, ct);
        if (firstLast is null)
            return new(null, $"Fast graph unavailable: workflow entity '{Workflow}' is missing; using registered slow workflow.");
        var quiet = await entities.GetBySlugAsync("comfyui-workflow", QuietWorkflow, ct);
        if (quiet is null)
            return new(null, $"Fast graph unavailable: workflow entity '{QuietWorkflow}' is missing; using registered slow workflow.");
        try
        {
            return new(FastPortraitWorkflow.Prepare(firstLast, quiet, settings), null);
        }
        catch (FastWorkflowUnavailableException ex)
        {
            return new(null, $"Fast graph unavailable: {Compact(ex.Message, 300)}; using registered slow workflow.");
        }
    }

    private async Task<GeneratedVideo> GenerateClipAsync(Guid agentId, SourcePortrait source, PortraitClipSpec spec,
        PortraitClipPrompt prompt, string imageAssetUrl, long seed, FastPortraitWorkflowTemplate? fastWorkflow,
        string? fallbackReason, Action<string> admitted, CancellationToken ct)
    {
        var token = RequireExecutionTokens().IssueSystem(new PluginSystemExecutionTokenRequest(
            "plugin", PixooPlugin.Id, "Leaf Pixoo", $"Generate the selected Agent's {spec.Key} display loop",
            [new("agent", EntityId: agentId.ToString()), new("asset", Id: source.AssetId)], ExecutionTokenLifetime));
        var imageUrl = LeafAssetUrl(imageAssetUrl);
        if (fastWorkflow is not null)
        {
            var body = fastWorkflow.CreateSubmission(prompt, imageUrl, seed, spec.State, source.Hash);
            using var request = new HttpRequestMessage(HttpMethod.Post, RawGenerateUrl)
            {
                Content = JsonContent.Create(body),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            request.Headers.TryAddWithoutValidation("X-Async", "true");
            request.Headers.TryAddWithoutValidation("X-Idempotency-Key",
                $"pixoo:{PortraitPackBuilder.PipelineVersion}:{source.Hash}:{spec.Key}:{seed}");
            var jobId = await AdmitAsync(request, spec.State, admitted, ct);
            var bytes = await AwaitVideoAsync(jobId, spec.State, token.AccessToken, ct);
            return new(jobId, bytes, settings.Width, settings.Height, true,
                FastPortraitWorkflowTemplate.Variant, null);
        }

        var inputs = new JsonObject
        {
            ["prompt"] = prompt.Prompt,
            ["negative"] = prompt.Negative,
            ["image"] = imageUrl,
            ["end_image"] = imageUrl,
            ["width"] = settings.SlowWidth,
            ["height"] = settings.SlowHeight,
            ["frames"] = settings.SourceFrames,
            ["interpolation_multiplier"] = settings.SlowInterpolationMultiplier,
            ["steps"] = settings.SlowSteps,
            ["seed"] = seed,
        };
        using var slowRequest = new HttpRequestMessage(HttpMethod.Post, SlowGenerateUrl)
        {
            Content = JsonContent.Create(new JsonObject { ["wait"] = false, ["inputs"] = inputs }),
        };
        slowRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        slowRequest.Headers.TryAddWithoutValidation("X-Idempotency-Key",
            $"pixoo:{PortraitPackBuilder.PipelineVersion}:slow:{source.Hash}:{spec.Key}:{seed}");
        var slowJobId = await AdmitAsync(slowRequest, spec.State, admitted, ct);
        var slowBytes = await AwaitVideoAsync(slowJobId, spec.State, token.AccessToken, ct);
        return new(slowJobId, slowBytes, settings.SlowWidth, settings.SlowHeight, false, Workflow, fallbackReason);
    }

    private async Task<string> AdmitAsync(HttpRequestMessage request, FaceState state,
        Action<string> admitted, CancellationToken ct)
    {
        using var response = await http.SendAsync(request, ct);
        var admissionText = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{Slug(state)} clip admission failed ({(int)response.StatusCode}).");
        var jobId = JsonNode.Parse(admissionText)?["jobId"]?.GetValue<string>();
        if (!Guid.TryParse(jobId, out _)) throw new InvalidOperationException("Clip admission returned no durable job id.");
        admitted(jobId);
        return jobId;
    }

    private async Task<byte[]> AwaitVideoAsync(string jobId, FaceState state, string accessToken, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(15));
        JsonObject? job;
        do
        {
            using var poll = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:18800/jobs/{jobId}?waitSeconds=25");
            poll.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var polled = await http.SendAsync(poll, deadline.Token);
            polled.EnsureSuccessStatusCode();
            job = JsonNode.Parse(await polled.Content.ReadAsStringAsync(deadline.Token)) as JsonObject;
        } while (job?["terminal"]?.GetValue<bool>() != true);
        var status = job?["status"]?.GetValue<string>();
        if (!string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{Slug(state)} clip job {jobId} ended as {status ?? "unknown"}.");

        var outputs = (job?["outputs"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? [];
        var selectedOutput = outputs.FirstOrDefault(value =>
                Text(value, "contentType")?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true)
            ?? outputs.FirstOrDefault();
        var outputPath = selectedOutput is null ? null : Text(selectedOutput, "url");
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new InvalidOperationException($"Clip job {jobId} completed without an output URL.");
        if (!outputPath.StartsWith("/", StringComparison.Ordinal))
            throw new InvalidOperationException($"Clip job {jobId} returned a non-relative output URL.");
        using var output = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(ComputeBaseUrl), outputPath));
        output.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var downloaded = await http.SendAsync(output, deadline.Token);
        downloaded.EnsureSuccessStatusCode();
        return await downloaded.Content.ReadAsByteArrayAsync(deadline.Token);
    }

    private async Task<StoredPortrait> StoreInitialAsync(SourcePortrait source, PreparedPortrait prepared,
        PortraitPack pack, CancellationToken ct)
    {
        await using var cropStream = new MemoryStream(prepared.CropPng, writable: false);
        await using var baseStream = new MemoryStream(PortraitPackBuilder.EncodePng(pack.BasePixels), writable: false);
        var crop = await assets.UploadIdempotentAsync(cropStream,
            $"pixoo:{PortraitPackBuilder.PipelineVersion}:crop:{source.Hash}", "pixoo-portrait-crop.png", "image/png", ct);
        var baseAsset = await assets.UploadIdempotentAsync(baseStream,
            $"pixoo:{PortraitPackBuilder.PipelineVersion}:base:{source.Hash}", "pixoo-portrait-base.png", "image/png", ct);
        var data = EntityData(source, prepared, pack, crop.AssetId, baseAsset.AssetId);
        var entity = await entities.CreateAsync(PackEntityType, $"{source.AgentName} display pack {source.Hash[..8]}", data, ct);
        return new(entity.Id, pack);
    }

    private async Task<PortraitClip> StoreClipAsync(SourcePortrait source, PortraitClipSpec spec, PortraitClipPrompt prompt,
        long seed, GeneratedVideo generated, PortraitClipProcessingResult processed, CancellationToken ct)
    {
        var stem = $"pixoo:{PortraitPackBuilder.PipelineVersion}:{source.Hash}:{spec.Key}:{seed}";
        await using var videoStream = new MemoryStream(generated.Bytes, writable: false);
        await using var loopStream = new MemoryStream(EncodeLoop(processed.Loop), writable: false);
        var video = await assets.UploadIdempotentAsync(videoStream, $"{stem}:video",
            $"pixoo-{spec.Key}.mp4", "video/mp4", ct);
        var reduced = await assets.UploadIdempotentAsync(loopStream, $"{stem}:loop",
            $"pixoo-{spec.Key}-64x64.json", "application/json", ct);
        return new(processed.Loop, new(spec.Key, spec.State, generated.Workflow, seed, generated.JobId, video.AssetId, reduced.AssetId,
            prompt.Prompt, prompt.Negative, generated.Note, processed.FrozenBackgroundFraction,
            DateTimeOffset.UtcNow));
    }

    private async Task<Guid> StoreEntityAsync(SourcePortrait source, PreparedPortrait prepared, PortraitPack pack,
        Guid? entityId, CancellationToken ct)
    {
        await using var cropStream = new MemoryStream(prepared.CropPng, writable: false);
        await using var baseStream = new MemoryStream(PortraitPackBuilder.EncodePng(pack.BasePixels), writable: false);
        var crop = await assets.UploadIdempotentAsync(cropStream,
            $"pixoo:{PortraitPackBuilder.PipelineVersion}:crop:{source.Hash}", "pixoo-portrait-crop.png", "image/png", ct);
        var baseAsset = await assets.UploadIdempotentAsync(baseStream,
            $"pixoo:{PortraitPackBuilder.PipelineVersion}:base:{source.Hash}", "pixoo-portrait-base.png", "image/png", ct);
        var name = $"{source.AgentName} display pack {source.Hash[..8]}";
        var data = EntityData(source, prepared, pack, crop.AssetId, baseAsset.AssetId);
        if (entityId is { } id)
        {
            await entities.ReplaceDataAsync(id, data, name, ct);
            return id;
        }
        return (await entities.CreateAsync(PackEntityType, name, data, ct)).Id;
    }

    private static JsonObject EntityData(SourcePortrait source, PreparedPortrait prepared, PortraitPack pack,
        string cropAssetId, string baseAssetId)
    {
        var clips = new JsonObject();
        foreach (var (key, clip) in pack.Clips)
        {
            var provenance = clip.Provenance;
            clips[key] = new JsonObject
            {
                ["state"] = Slug(provenance.State),
                ["workflow"] = provenance.Workflow,
                ["seed"] = provenance.Seed,
                ["job_id"] = provenance.JobId,
                ["video_asset"] = provenance.VideoAssetId,
                ["loop_asset"] = provenance.LoopAssetId,
                ["prompt"] = provenance.Prompt,
                ["negative"] = provenance.Negative,
                ["note"] = provenance.Note,
                ["frozen_background_fraction"] = provenance.FrozenBackgroundFraction,
                ["generated_at"] = provenance.GeneratedAt,
            };
        }
        return new JsonObject
        {
            ["owner_id"] = source.OwnerId,
            ["agent_id"] = source.AgentId.ToString(),
            ["source_asset_id"] = source.AssetId,
            ["source_hash"] = source.Hash,
            ["pipeline_version"] = PortraitPackBuilder.PipelineVersion,
            ["generated_at"] = pack.Provenance.GeneratedAt,
            ["crop_asset"] = cropAssetId,
            ["base_asset"] = baseAssetId,
            ["source_square"] = RectJson(prepared.SourceSquare),
            ["clips"] = clips,
        };
    }

    private async Task<StoredPortrait?> LoadCachedAsync(Guid agentId, string hash, CancellationToken ct)
    {
        var matches = await entities.QueryAsync(new EntityQuery
        {
            TypeSlug = PackEntityType,
            DataEquals = new Dictionary<string, object?>
            {
                ["agent_id"] = agentId.ToString(), ["source_hash"] = hash,
                ["pipeline_version"] = PortraitPackBuilder.PipelineVersion,
            },
            Limit = 20,
        }, ct);
        foreach (var entity in matches.OrderByDescending(item => item.CreatedAt))
        {
            try
            {
                var baseId = Text(entity.Data, "base_asset");
                if (baseId is null) continue;
                var baseFile = await assets.ReadAsync(baseId, ct);
                if (baseFile is null) continue;
                var clips = new Dictionary<string, PortraitClip>();
                if (entity.Data["clips"] is JsonObject storedClips)
                {
                    foreach (var spec in PortraitClipPlan.Specs)
                    {
                        if (storedClips[spec.Key] is not JsonObject value) continue;
                        var loopId = Text(value, "loop_asset");
                        if (loopId is null || await assets.ReadAsync(loopId, ct) is not { } loopFile) continue;
                        var loop = DecodeLoop(loopFile.Bytes);
                        clips[spec.Key] = new(loop, new(spec.Key, spec.State,
                            Text(value, "workflow") ?? Workflow,
                            value["seed"]?.GetValue<long>() ?? 0,
                            Text(value, "job_id") ?? "cached",
                            Text(value, "video_asset") ?? string.Empty,
                            loopId,
                            Text(value, "prompt") ?? string.Empty,
                            Text(value, "negative") ?? string.Empty,
                            Text(value, "note"),
                            value["frozen_background_fraction"]?.GetValue<double>() ?? 0,
                            value["generated_at"]?.GetValue<DateTimeOffset>() ?? entity.CreatedAt));
                    }
                }
                var pack = new PortraitPack(PortraitPackBuilder.DecodePng(baseFile.Bytes),
                    new(Text(entity.Data, "source_asset_id") ?? string.Empty, hash,
                        PortraitPackBuilder.PipelineVersion,
                        entity.Data["generated_at"]?.GetValue<DateTimeOffset>() ?? entity.CreatedAt), clips);
                return new(entity.Id, pack);
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
        return new(agentId, agent.Name, Text(agent.Data, "owner_id") ?? "system", assetId, file.Bytes,
            PortraitSourceFingerprint.Hash(file.Bytes), outfitDescription);
    }

    private IExecutionTokens RequireExecutionTokens() => executionTokens
        ?? throw new InvalidOperationException("Video generation needs the plugin execution identity (os:issue-system-execution-token permission).");

    private void SetOverall(Guid agentId, string state, string? sourceAssetId, string? sourceHash, string? error)
        => statuses[agentId] = new(agentId, state, sourceAssetId, sourceHash, error, DateTimeOffset.UtcNow, []);

    private void SetClip(Guid agentId, PortraitClipSpec spec, string phase, int attempts, string? jobId, string? error,
        double? frozenBackgroundFraction = null)
    {
        var values = clipStatuses.GetOrAdd(agentId, _ => new());
        values[spec.Key] = new(spec.Key, spec.State, spec.Variant, phase, attempts, jobId, error,
            frozenBackgroundFraction, DateTimeOffset.UtcNow);
    }

    private IReadOnlyList<PortraitClipStatus> ClipSnapshot(Guid agentId)
    {
        var values = clipStatuses.GetValueOrDefault(agentId);
        return PortraitClipPlan.Specs.Select(spec => values?.GetValueOrDefault(spec.Key)
            ?? new(spec.Key, spec.State, spec.Variant,
                current.GetValueOrDefault(agentId)?.Clips.ContainsKey(spec.Key) == true ? "ready" : "pending",
                0, current.GetValueOrDefault(agentId)?.Clips.GetValueOrDefault(spec.Key)?.Provenance.JobId,
                current.GetValueOrDefault(agentId)?.Clips.GetValueOrDefault(spec.Key)?.Provenance.Note,
                current.GetValueOrDefault(agentId)?.Clips.GetValueOrDefault(spec.Key)?.Provenance.FrozenBackgroundFraction,
                DateTimeOffset.UtcNow)).ToArray();
    }

    private static bool HasAllPrimaryClips(PortraitPack pack)
        => PortraitClipPlan.Specs.All(spec => pack.Clips.ContainsKey(spec.Key));

    private static long SeedFor(string hash, string key, int attempt)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{hash}:{key}:{attempt}"));
        return (long)(BinaryPrimitives.ReadUInt64LittleEndian(bytes) % int.MaxValue) + 1;
    }

    private static byte[] EncodeLoop(FrameLoop loop)
    {
        var frames = new JsonArray(loop.Frames.Select(frame => (JsonNode)new JsonObject
        {
            ["pixels"] = Convert.ToBase64String(frame.Pixels), ["durationMs"] = frame.DurationMs,
        }).ToArray());
        return Encoding.UTF8.GetBytes(new JsonObject
        {
            ["schema"] = "leaf.pixoo.frame-loop/v1", ["width"] = 64, ["height"] = 64, ["frames"] = frames,
        }.ToJsonString());
    }

    private static FrameLoop DecodeLoop(byte[] bytes)
    {
        var root = JsonNode.Parse(bytes) as JsonObject ?? throw new InvalidDataException("Stored loop is malformed.");
        if (root["frames"] is not JsonArray frames || frames.Count is < 1 or > 30)
            throw new InvalidDataException("Stored loop frame count is invalid.");
        return new FrameLoop(frames.Select(item =>
        {
            var frame = item as JsonObject ?? throw new InvalidDataException("Stored loop frame is malformed.");
            return new RgbFrame(Convert.FromBase64String(Text(frame, "pixels") ?? string.Empty),
                frame["durationMs"]?.GetValue<int>() ?? 0);
        }).ToArray()).Validate();
    }

    private static JsonObject RectJson(ImageRect rect) => new()
        { ["x"] = rect.X, ["y"] = rect.Y, ["width"] = rect.Width, ["height"] = rect.Height };
    private static string Slug(FaceState state) => state.ToString().ToLowerInvariant();
    private static string? Text(JsonObject value, string key)
        => value[key] is JsonValue node && node.TryGetValue<string>(out var text) ? text : null;
    private static string Compact(string value, int maximum)
    {
        var compact = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact[..Math.Min(maximum, compact.Length)];
    }
    private static string LeafAssetUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var absolute)
            ? absolute.ToString()
            : $"http://127.0.0.1:18804/{value.TrimStart('/')}";
    private static string? Avatar(JsonObject value) => Text(value, "avatar")
        ?? (value["avatar"] as JsonObject is { } avatar ? Text(avatar, "filename") ?? Text(avatar, "url") : null);
    private static string? AssetId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)) value = uri.AbsolutePath;
        return Uri.UnescapeDataString(value.TrimEnd('/').Split('/').Last());
    }

    public void Dispose() => http.Dispose();

    private sealed record SourcePortrait(Guid AgentId, string AgentName, string OwnerId, string AssetId,
        byte[] Bytes, string Hash, string? OutfitDescription);
    private sealed record GeneratedVideo(string JobId, byte[] Bytes, int Width, int Height, bool Fast,
        string Workflow, string? Note);
    private sealed record FastWorkflowResolution(FastPortraitWorkflowTemplate? Template, string? Reason);
    private sealed record StoredPortrait(Guid EntityId, PortraitPack Pack);
}
