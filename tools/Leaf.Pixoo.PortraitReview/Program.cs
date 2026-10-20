using System.Text.Json;
using System.Text.Json.Nodes;
using System.Buffers.Binary;
using Leaf.Plugins.Pixoo;

if (args.Length < 3 || args[0] is not ("prepare" or "build"))
{
    Console.Error.WriteLine("Usage: prepare <source> <output-dir> | build <source> <candidate> <seed> <output-dir>");
    return 2;
}

var builder = new PortraitPackBuilder();
var sourcePath = Path.GetFullPath(args[1]);
if (args[0] == "prepare")
{
    var output = Path.GetFullPath(args[2]);
    Directory.CreateDirectory(output);
    var prepared = builder.Prepare(await File.ReadAllBytesAsync(sourcePath));
    await File.WriteAllBytesAsync(Path.Combine(output, "source-crop.png"), prepared.CropPng);
    await File.WriteAllBytesAsync(Path.Combine(output, "faithful-fallback-64.png"), PortraitPackBuilder.EncodePng(prepared.FallbackPixels));
    await File.WriteAllBytesAsync(Path.Combine(output, "faithful-colour-regions-64.png"), PortraitPackBuilder.EncodePng(
        PortraitColorMatcher.VisualizeRegions(prepared.FallbackPixels, prepared.Regions, prepared.FigureMask)));
    await File.WriteAllBytesAsync(Path.Combine(output, "faithful-fallback-8x.png"), PortraitReview.ContactSheet(
        new FrameLoop([new RgbFrame(prepared.FallbackPixels, 1000)]), 8));
    var fallback = builder.BuildFallback(prepared, "review-source", DateTimeOffset.UtcNow);
    var seeds = Enumerable.Range(3, 3).Select(attempt => SeedFor(prepared.SourceHash, attempt)).ToArray();
    Console.WriteLine(JsonSerializer.Serialize(new { prepared.SourceHash, seeds, prepared.Regions, prepared.ColorHints, fallback.PaletteSize, fallback.FaceContrast }));
    return 0;
}

if (args.Length != 5 || !long.TryParse(args[3], out var seed))
{
    Console.Error.WriteLine("Usage: build <source> <candidate> <seed> <output-dir>");
    return 2;
}

var candidatePath = Path.GetFullPath(args[2]);
var outputDir = Path.GetFullPath(args[4]);
Directory.CreateDirectory(outputDir);
var sourceBytes = await File.ReadAllBytesAsync(sourcePath);
var preparedPortrait = builder.Prepare(sourceBytes);
var pack = builder.Build(await File.ReadAllBytesAsync(candidatePath), preparedPortrait,
    "815d29b8-3bf4-443f-81ca-ba9263d1ee9f.png", PortraitPackService.Workflow, seed, DateTimeOffset.UtcNow);
foreach (var artifact in PortraitReview.Create(pack, preparedPortrait.CropPng, "#9a7cff"))
    await File.WriteAllBytesAsync(Path.Combine(outputDir, artifact.FileName), artifact.Bytes);
await File.WriteAllBytesAsync(Path.Combine(outputDir, "source-crop.png"), preparedPortrait.CropPng);
await File.WriteAllBytesAsync(Path.Combine(outputDir, "redraw-source.png"), await File.ReadAllBytesAsync(candidatePath));

var states = new JsonObject();
foreach (var state in Enum.GetValues<FaceState>())
{
    var loop = PortraitAnimation.Render(pack, state, "#9a7cff", 82);
    states[state.ToString().ToLowerInvariant()] = new JsonArray(loop.Frames.Select(frame => (JsonNode)new JsonObject
    {
        ["pixels"] = Convert.ToBase64String(frame.Pixels),
        ["durationMs"] = frame.DurationMs,
    }).ToArray());
}
var preview = new JsonObject
{
    ["sourceAssetId"] = pack.Provenance.SourceAssetId,
    ["sourceHash"] = pack.Provenance.SourceHash,
    ["workflow"] = pack.Provenance.Workflow,
    ["seed"] = pack.Provenance.Seed,
    ["paletteSize"] = pack.PaletteSize,
    ["faceContrast"] = pack.FaceContrast,
    ["regions"] = JsonSerializer.SerializeToNode(pack.Regions),
    ["colorMatch"] = JsonSerializer.SerializeToNode(pack.ColorMatch),
    ["states"] = states,
};
await File.WriteAllTextAsync(Path.Combine(outputDir, "portrait-preview.json"),
    preview.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(new { seed, pack.PaletteSize, pack.FaceContrast, pack.Provenance.DetectedGridPitch, pack.ColorMatch }));
return 0;

static long SeedFor(string hash, int attempt)
{
    var bytes = Convert.FromHexString(hash[..16]);
    var value = BinaryPrimitives.ReadUInt64LittleEndian(bytes) ^ (0x9E3779B97F4A7C15UL * (ulong)(attempt + 1));
    return (long)(value % int.MaxValue) + 1;
}
