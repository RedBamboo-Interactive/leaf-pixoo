using System.Security.Claims;

namespace Leaf.Plugins.Pixoo.Tests;

public sealed class PortraitAccessTests
{
    [Fact]
    public void Portrait_access_requires_a_device_for_that_agent_or_an_administrator()
    {
        var agent = Guid.NewGuid();
        var user = new PixooCaller(Guid.NewGuid().ToString(), null);

        Assert.True(PixooService.CanManagePortrait(user, agent, [agent]));
        Assert.False(PixooService.CanManagePortrait(user, agent, [Guid.NewGuid()]));
        Assert.False(PixooService.CanManagePortrait(user, agent, []));
        Assert.True(PixooService.CanManagePortrait(user with { IsAdmin = true }, agent, []));
        Assert.False(PixooService.CanManagePortrait(user with { IsAdmin = true, AgentId = Guid.NewGuid().ToString() }, agent, []));
    }

    [Theory]
    [InlineData("[\"admin\"]", true)]
    [InlineData("[\"member\",\"Admin\"]", true)]
    [InlineData("admin", true)]
    [InlineData("[\"member\"]", false)]
    [InlineData("[not json", false)]
    public void Caller_reads_the_administrator_role_from_host_claims(string roles, bool expected)
    {
        var identity = new ClaimsIdentity([new Claim("sub", Guid.NewGuid().ToString()), new Claim("roles", roles)], "Bearer");

        Assert.Equal(expected, PixooCaller.From(new ClaimsPrincipal(identity)).IsAdmin);
    }
}
