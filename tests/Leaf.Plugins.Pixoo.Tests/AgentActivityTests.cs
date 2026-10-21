using System.Text.Json.Nodes;

namespace Leaf.Plugins.Pixoo.Tests;

public sealed class AgentActivityTests
{
    private static readonly Guid Agent = Guid.Parse("a7b059ac-f678-8b58-819b-1bcf3acb22db");

    private static JsonObject Event(string phase, string turn = "t1", bool userFacing = true, Guid? agent = null) => new()
    {
        ["agentId"] = (agent ?? Agent).ToString(), ["phase"] = phase, ["userFacing"] = userFacing,
        ["at"] = DateTimeOffset.UtcNow.ToString("O"), ["turn"] = turn,
    };

    [Fact]
    public void A_turn_moves_through_thinking_speaking_working_and_back_to_ambient()
    {
        var tracker = new AgentActivityTracker();
        Assert.Equal((Agent, (FaceState?)FaceState.Thinking), tracker.Apply(Event("thinking")));
        Assert.Equal((Agent, (FaceState?)FaceState.Speaking), tracker.Apply(Event("speaking")));
        Assert.Equal((Agent, (FaceState?)FaceState.Working), tracker.Apply(Event("working")));
        Assert.Equal((Agent, (FaceState?)null), tracker.Apply(Event("idle")));
    }

    [Fact]
    public void Repeated_phase_is_not_a_change()
    {
        var tracker = new AgentActivityTracker();
        tracker.Apply(Event("thinking"));
        Assert.Null(tracker.Apply(Event("thinking")));
    }

    [Fact]
    public void Automation_turns_never_reach_the_face()
    {
        var tracker = new AgentActivityTracker();
        Assert.Null(tracker.Apply(Event("thinking", userFacing: false)));
        Assert.Null(tracker.Apply(Event("speaking", userFacing: false)));
    }

    [Fact]
    public void Agent_stays_active_until_every_concurrent_turn_is_idle()
    {
        var tracker = new AgentActivityTracker();
        tracker.Apply(Event("thinking", "a"));
        Assert.Equal((Agent, (FaceState?)FaceState.Speaking), tracker.Apply(Event("speaking", "b")));
        Assert.Equal((Agent, (FaceState?)FaceState.Thinking), tracker.Apply(Event("idle", "b")));
        Assert.Equal((Agent, (FaceState?)null), tracker.Apply(Event("idle", "a")));
    }

    [Fact]
    public void Agents_are_tracked_independently()
    {
        var tracker = new AgentActivityTracker();
        var other = Guid.NewGuid();
        tracker.Apply(Event("thinking"));
        Assert.Equal((other, (FaceState?)FaceState.Thinking), tracker.Apply(Event("thinking", "x", agent: other)));
        Assert.Equal((Agent, (FaceState?)null), tracker.Apply(Event("idle")));
    }

    [Fact]
    public void Malformed_events_are_ignored_or_rejected()
    {
        var tracker = new AgentActivityTracker();
        Assert.Null(tracker.Apply(new JsonObject { ["phase"] = "thinking", ["userFacing"] = true, ["turn"] = "t" }));
        Assert.Null(tracker.Apply(new JsonObject { ["agentId"] = Agent.ToString(), ["phase"] = "thinking", ["userFacing"] = true }));
        Assert.Throws<FormatException>(() => tracker.Apply(Event("dancing")));
    }
}
