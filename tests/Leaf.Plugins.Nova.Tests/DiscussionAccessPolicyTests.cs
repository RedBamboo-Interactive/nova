using System.Security.Claims;
using System.Text.Json;
using Leaf.Plugins.Nova;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class DiscussionAccessPolicyTests
{
    private const string Owner = "user-owner";
    private const string Agent = "agent-nova";

    [Fact]
    public void ConfidentialDiscussionAllowsOnlyExplicitOwnerOrOwningAgent()
    {
        var discussion = ConfidentialDiscussion();

        Assert.True(DiscussionAccessPolicy.CanRead(discussion, Human(Owner)));
        Assert.False(DiscussionAccessPolicy.CanRead(discussion, Human("someone-else")));
        Assert.False(DiscussionAccessPolicy.CanRead(discussion, LocalDefault()));
        Assert.True(DiscussionAccessPolicy.CanRead(discussion, Execution(Agent, Owner)));
        Assert.False(DiscussionAccessPolicy.CanRead(discussion, Execution("another-agent", Owner)));
        Assert.False(DiscussionAccessPolicy.CanRead(discussion, Execution(Agent, "another-user")));
    }

    [Fact]
    public void OnlyExplicitHumanOwnerCanChangeConfidentiality()
    {
        var discussion = ConfidentialDiscussion();

        Assert.True(DiscussionAccessPolicy.CanManageConfidentiality(discussion, Human(Owner)));
        Assert.False(DiscussionAccessPolicy.CanManageConfidentiality(discussion, LocalDefault()));
        Assert.False(DiscussionAccessPolicy.CanManageConfidentiality(discussion, Execution(Agent, Owner)));
    }

    private static DiscussionRead ConfidentialDiscussion() => new(
        "discussion-1", "Private", "session-1", DiscussionStatus.Idle,
        DateTime.UtcNow, DateTime.UtcNow, 1, null, Owner, Guid.NewGuid(), Agent,
        Confidential: true);

    private static DefaultHttpContext Human(string userId)
        => Context(new ClaimsIdentity([new Claim("sub", userId)], "Bearer"));

    private static DefaultHttpContext LocalDefault()
        => Context(new ClaimsIdentity([new Claim("sub", "local-user")], "LocalDefault"));

    private static DefaultHttpContext Execution(string agentId, string beneficiaryId)
    {
        var identity = JsonSerializer.Serialize(new
        {
            actor = new { kind = "agent", id = "nova", entityId = agentId },
            beneficiary = new { kind = "user", id = beneficiaryId },
        });
        return Context(new ClaimsIdentity([
            new Claim("sub", beneficiaryId),
            new Claim("token_use", "execution"),
            new Claim("execution_identity", identity),
        ], "Bearer"));
    }

    private static DefaultHttpContext Context(ClaimsIdentity identity)
        => new() { User = new ClaimsPrincipal(identity) };
}
