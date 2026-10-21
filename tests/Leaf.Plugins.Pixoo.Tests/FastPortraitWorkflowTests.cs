using System.Text.Json.Nodes;
using Leaf.Sdk;

namespace Leaf.Plugins.Pixoo.Tests;

public sealed class FastPortraitWorkflowTests
{
    [Fact]
    public void Rewrite_copies_lightx2v_nodes_rewires_samplers_and_removes_film()
    {
        var template = FastPortraitWorkflow.Prepare(Workflow(PortraitPackService.Workflow, BaseGraph()),
            Workflow(PortraitPackService.QuietWorkflow, QuietGraph()), new PortraitClipSettings());
        var graph = template.Graph;

        foreach (var id in new[] { "175", "176", "177", "178", "181", "182" })
            Assert.Equal($"lightx2v-{id}", Inputs(graph, id)["lora_name"]!.GetValue<string>());
        Assert.Equal("177", Inputs(graph, "527")["model"]!.AsArray()[0]!.GetValue<string>());
        Assert.Equal("178", Inputs(graph, "528")["model"]!.AsArray()[0]!.GetValue<string>());
        Assert.Equal(1, Inputs(graph, "527")["cfg"]!.GetValue<int>());
        Assert.Equal(1, Inputs(graph, "528")["cfg"]!.GetValue<int>());
        Assert.Equal(4, Inputs(graph, "536")["value"]!.GetValue<int>());
        Assert.Equal(2, Inputs(graph, "537")["value"]!.GetValue<int>());
        Assert.Equal(384, Inputs(graph, "580")["width"]!.GetValue<int>());
        Assert.Equal(384, Inputs(graph, "580")["height"]!.GetValue<int>());
        Assert.Equal(41, Inputs(graph, "619")["value"]!.GetValue<int>());
        Assert.Null(graph["617"]);
        Assert.Equal("529", Inputs(graph, "621")["images"]!.AsArray()[0]!.GetValue<string>());
        Assert.Equal(15, Inputs(graph, "621")["fps"]!.GetValue<int>());
    }

    [Fact]
    public void Submission_materializes_prompt_seed_square_images_and_raw_compute_contract()
    {
        var template = FastPortraitWorkflow.Prepare(Workflow(PortraitPackService.Workflow, BaseGraph()),
            Workflow(PortraitPackService.QuietWorkflow, QuietGraph()), new PortraitClipSettings());
        var prompt = new PortraitClipPrompt("positive", "negative");

        var body = template.CreateSubmission(prompt, "http://leaf.invalid/crop.png", 1234,
            FaceState.Speaking, new string('a', 64));
        var graph = JsonNode.Parse(body["workflow_json"]!.GetValue<string>())!.AsObject();

        Assert.Equal("positive", Inputs(graph, "16")["positive_prompt"]!.GetValue<string>());
        Assert.Equal("negative", Inputs(graph, "16")["negative_prompt"]!.GetValue<string>());
        Assert.Equal(1234, Inputs(graph, "583")["value"]!.GetValue<long>());
        Assert.Equal("http://leaf.invalid/crop.png", body["image"]!.GetValue<string>());
        Assert.Equal(body["image"]!.GetValue<string>(), body["end_image"]!.GetValue<string>());
        Assert.Equal("622", body["output_node"]!.GetValue<string>());
        Assert.Equal("video", body["media_type"]!.GetValue<string>());
        var parameters = body["workflow_parameters"]!.AsArray();
        Assert.Equal(["579", "623"], parameters.Select(item => item!["node_id"]!.GetValue<string>()));
    }

    [Fact]
    public void Rewrite_fails_closed_when_a_required_node_is_missing()
    {
        var graph = BaseGraph();
        graph.Remove("617");

        var error = Assert.Throws<FastWorkflowUnavailableException>(() =>
            FastPortraitWorkflow.Prepare(Workflow(PortraitPackService.Workflow, graph),
                Workflow(PortraitPackService.QuietWorkflow, QuietGraph()), new PortraitClipSettings()));

        Assert.Contains("617", error.Message);
    }

    private static LeafEntity Workflow(string slug, JsonObject graph)
        => new(Guid.NewGuid(), "comfyui-workflow", slug, slug,
            new JsonObject { ["workflow_json"] = graph }, DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch, "fixture");

    private static JsonObject BaseGraph()
    {
        var graph = new JsonObject();
        foreach (var id in new[] { "16", "527", "528", "529", "536", "537", "580", "583", "617", "619", "621", "622", "579", "623" })
            graph[id] = Node($"base-{id}");
        return graph;
    }

    private static JsonObject QuietGraph()
    {
        var graph = new JsonObject();
        foreach (var id in new[] { "175", "176", "177", "178", "181", "182" })
            graph[id] = Node($"quiet-{id}", new JsonObject { ["lora_name"] = $"lightx2v-{id}" });
        return graph;
    }

    private static JsonObject Node(string classType, JsonObject? inputs = null)
        => new() { ["class_type"] = classType, ["inputs"] = inputs ?? new JsonObject() };

    private static JsonObject Inputs(JsonObject graph, string id) => graph[id]!["inputs"]!.AsObject();
}
