using System.Text.Json.Nodes;
using Leaf.Sdk;

namespace Leaf.Plugins.Pixoo;

internal sealed class FastWorkflowUnavailableException(string message) : InvalidOperationException(message);

internal sealed record FastPortraitWorkflowTemplate(LeafEntity BaseWorkflow, JsonObject Graph)
{
    public const string Variant = "pixoo-lightx2v-flf-v1";

    public JsonObject CreateSubmission(PortraitClipPrompt prompt, string imageUrl, long seed,
        FaceState state, string sourceHash)
    {
        var graph = Graph.DeepClone().AsObject();
        FastPortraitWorkflow.Inputs(graph, "583")["value"] = seed;
        var prompts = FastPortraitWorkflow.Inputs(graph, "16");
        prompts["positive_prompt"] = prompt.Prompt;
        prompts["negative_prompt"] = prompt.Negative;
        FastPortraitWorkflow.Inputs(graph, "622")["filename_prefix"] = $"pixoo/{sourceHash[..12]}-{state.ToString().ToLowerInvariant()}";

        return new JsonObject
        {
            ["workflow_entity"] = new JsonObject
            {
                ["id"] = BaseWorkflow.Id.ToString(),
                ["typeSlug"] = BaseWorkflow.TypeSlug,
                ["slug"] = BaseWorkflow.Slug,
                ["name"] = $"{BaseWorkflow.Name} — Pixoo LightX2V",
                ["version"] = 1,
                ["updatedAt"] = BaseWorkflow.UpdatedAt,
                ["variant"] = Variant,
            },
            ["workflow_inputs"] = new JsonObject { ["image"] = imageUrl, ["end_image"] = imageUrl },
            ["workflow_input_sources"] = new JsonObject { ["image"] = "explicit", ["end_image"] = "explicit" },
            ["workflow_json"] = graph.ToJsonString(),
            ["workflow_parameters"] = new JsonArray
            {
                new JsonObject { ["name"] = "image", ["node_id"] = "579", ["field"] = "image", ["type"] = "image" },
                new JsonObject { ["name"] = "end_image", ["node_id"] = "623", ["field"] = "image", ["type"] = "image" },
            },
            ["output_node"] = "622",
            ["media_type"] = "video",
            ["image"] = imageUrl,
            ["end_image"] = imageUrl,
            ["name"] = $"Pixoo {state.ToString().ToLowerInvariant()} motion",
            ["rationale"] = "Generate a short motion loop for the selected Agent's Pixoo display",
        };
    }
}

internal static class FastPortraitWorkflow
{
    private static readonly string[] LoraNodes = ["175", "176", "177", "178", "181", "182"];

    public static FastPortraitWorkflowTemplate Prepare(LeafEntity firstLast, LeafEntity quietReportage,
        PortraitClipSettings settings)
    {
        var graph = ReadGraph(firstLast, "first/last-frame").DeepClone().AsObject();
        var quiet = ReadGraph(quietReportage, "quiet-reportage");
        foreach (var id in LoraNodes)
        {
            if (quiet[id] is not JsonObject source)
                throw new FastWorkflowUnavailableException($"quiet-reportage workflow is missing LoRA node {id}");
            graph[id] = source.DeepClone();
        }

        Inputs(graph, "527")["model"] = new JsonArray("177", 0);
        Inputs(graph, "528")["model"] = new JsonArray("178", 0);
        Inputs(graph, "527")["cfg"] = 1;
        Inputs(graph, "528")["cfg"] = 1;
        Inputs(graph, "536")["value"] = settings.Steps;
        Inputs(graph, "537")["value"] = settings.SamplerSplit;
        var size = Inputs(graph, "580");
        size["width"] = settings.Width;
        size["height"] = settings.Height;
        Inputs(graph, "619")["value"] = settings.SourceFrames;
        if (!graph.Remove("617"))
            throw new FastWorkflowUnavailableException("first/last-frame workflow is missing FILM node 617");
        var video = Inputs(graph, "621");
        RequireNode(graph, "529");
        video["images"] = new JsonArray("529", 0);
        video["fps"] = settings.OutputFps;

        // State-specific values are filled only after cloning the validated template.
        foreach (var id in new[] { "16", "579", "583", "622", "623" }) Inputs(graph, id);
        return new(firstLast, graph);
    }

    internal static JsonObject Inputs(JsonObject graph, string id)
    {
        var node = RequireNode(graph, id);
        return node["inputs"] as JsonObject
            ?? throw new FastWorkflowUnavailableException($"workflow node {id} has no inputs object");
    }

    private static JsonObject RequireNode(JsonObject graph, string id)
        => graph[id] as JsonObject
            ?? throw new FastWorkflowUnavailableException($"first/last-frame workflow is missing node {id}");

    private static JsonObject ReadGraph(LeafEntity entity, string label)
    {
        if (entity.TypeSlug != "comfyui-workflow")
            throw new FastWorkflowUnavailableException($"{label} entity is not a comfyui-workflow");
        if (entity.Data["workflow_json"] is JsonObject graph) return graph;
        if (entity.Data["workflow_json"] is JsonValue value && value.TryGetValue<string>(out var text))
        {
            try
            {
                if (JsonNode.Parse(text) is JsonObject parsed) return parsed;
            }
            catch (System.Text.Json.JsonException)
            {
                throw new FastWorkflowUnavailableException($"{label} workflow_json is malformed");
            }
        }
        throw new FastWorkflowUnavailableException($"{label} workflow has no workflow_json object");
    }
}
