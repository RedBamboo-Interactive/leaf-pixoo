namespace Leaf.Plugins.Pixoo.Tests;

public sealed class StateMachineTests
{
    private static readonly Guid Agent = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void User_session_transitions_and_correlates_stream_by_session_id()
    {
        var mapper = new StrictAgentEventMapper();
        Assert.True(mapper.TryMap(new("session.created", "s-1", Agent.ToString(), "nova-chat", true), out var arrive));
        Assert.Equal(FaceState.Resting, arrive!.State);
        Assert.Equal(FaceGesture.Arrive, arrive.Gesture);

        Assert.True(mapper.TryMap(new("session.stream", "s-1", StreamKind: "thinking"), out var thinking));
        Assert.Equal(FaceState.Thinking, thinking!.State);
        Assert.True(mapper.TryMap(new("session.stream", "s-1", StreamKind: "tool_call"), out var working));
        Assert.Equal(FaceState.Working, working!.State);
        Assert.True(mapper.TryMap(new("session.stream", "s-1", StreamKind: "text"), out var speaking));
        Assert.Equal(FaceState.Speaking, speaking!.State);
        Assert.True(mapper.TryMap(new("session.ended", "s-1"), out var resting));
        Assert.Equal(FaceState.Resting, resting!.State);
    }

    [Theory]
    [InlineData("automation-runtime")]
    [InlineData("heartbeat-tick")]
    [InlineData("scheduled-run")]
    public void Background_sessions_never_move_the_face(string source)
    {
        var mapper = new StrictAgentEventMapper();
        Assert.False(mapper.TryMap(new("session.created", "background", Agent.ToString(), source, true), out _));
        Assert.False(mapper.TryMap(new("session.stream", "background", StreamKind: "text"), out _));
    }

    [Fact]
    public void Missing_identity_or_user_facing_classification_fails_closed()
    {
        var mapper = new StrictAgentEventMapper();
        Assert.False(mapper.TryMap(new("session.created", "s-2", Agent.ToString(), "nova"), out _));
        Assert.False(mapper.TryMap(new("session.stream", "unknown", StreamKind: "tool_call"), out _));
    }

    [Fact]
    public void State_machine_reports_semantic_changes()
    {
        var machine = new FaceStateMachine();
        Assert.True(machine.Apply(new(Agent, FaceState.Listening, null, "session.updated")));
        Assert.False(machine.Apply(new(Agent, FaceState.Listening, null, "session.updated")));
        Assert.True(machine.Apply(new(Agent, FaceState.Working, null, "session.stream")));
        Assert.Equal(FaceState.Working, machine.Current);
    }
}
