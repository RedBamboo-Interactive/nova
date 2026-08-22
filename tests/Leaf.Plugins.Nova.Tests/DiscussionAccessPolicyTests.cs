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
        Assert.True(DiscussionAccessPolicy.CanRead(discussion, BrowserExecution(Owner)));
        Assert.True(DiscussionAccessPolicy.CanRead(discussion, AgentExecution(Agent, Owner)));
        Assert.True(DiscussionAccessPolicy.CanRead(discussion, LegacyAgentExecution(Agent, Owner)));
        Assert.False(DiscussionAccessPolicy.CanRead(discussion, AgentExecution("another-agent", Owner)));
        Assert.False(DiscussionAccessPolicy.CanRead(discussion, AgentExecution(Agent, "another-user")));
    }

    [Fact]
    public void BrowserReadRequiresRootNovaAppContextAndExactOwner()
    {
        var discussion = ConfidentialDiscussion();

        Assert.False(DiscussionAccessPolicy.CanRead(
            discussion, BrowserExecution("another-user")));
        Assert.False(DiscussionAccessPolicy.CanRead(
            discussion, BrowserExecution(Owner, subjectId: "local-user")));
        Assert.False(DiscussionAccessPolicy.CanRead(
            discussion, BrowserExecution(Owner, appId: "codered", actorId: "nova")));
        Assert.False(DiscussionAccessPolicy.CanRead(
            discussion, BrowserExecution(Owner, actorId: "codered")));
        Assert.False(DiscussionAccessPolicy.CanRead(
            discussion, BrowserExecution(Owner, actorKind: "agent")));
        Assert.False(DiscussionAccessPolicy.CanRead(
            discussion, BrowserExecution(Owner, route: "/apps/codered")));
        Assert.False(DiscussionAccessPolicy.CanRead(
            discussion, BrowserExecution(Owner, parentExecutionId: Guid.NewGuid().ToString())));
    }

    [Fact]
    public void OnlyExplicitHumanOwnerCanChangeConfidentiality()
    {
        var discussion = ConfidentialDiscussion();

        Assert.True(DiscussionAccessPolicy.CanManageConfidentiality(discussion, Human(Owner)));
        Assert.True(DiscussionAccessPolicy.CanManageConfidentiality(
            discussion, BrowserExecution(Owner)));
        Assert.False(DiscussionAccessPolicy.CanManageConfidentiality(discussion, LocalDefault()));
        Assert.False(DiscussionAccessPolicy.CanManageConfidentiality(
            discussion, AgentExecution(Agent, Owner)));
    }

    [Fact]
    public void BrowserMutationRequiresRootNovaAppContextAndExactOwner()
    {
        var discussion = ConfidentialDiscussion();

        Assert.False(DiscussionAccessPolicy.CanManageConfidentiality(
            discussion, BrowserExecution("another-user")));
        Assert.False(DiscussionAccessPolicy.CanManageConfidentiality(
            discussion, BrowserExecution(Owner, appId: "codered", actorId: "nova")));
        Assert.False(DiscussionAccessPolicy.CanManageConfidentiality(
            discussion, BrowserExecution(Owner, actorId: "codered")));
        Assert.False(DiscussionAccessPolicy.CanManageConfidentiality(
            discussion, BrowserExecution(Owner, actorKind: "agent")));
        Assert.False(DiscussionAccessPolicy.CanManageConfidentiality(
            discussion, BrowserExecution(Owner, route: "/apps/codered")));
        Assert.False(DiscussionAccessPolicy.CanManageConfidentiality(
            discussion, BrowserExecution(Owner, parentExecutionId: Guid.NewGuid().ToString())));
    }

    [Fact]
    public void LocalDefaultCannotLaunderItselfThroughANovaBrowserToken()
    {
        var discussion = ConfidentialDiscussion("local-user");

        Assert.False(DiscussionAccessPolicy.CanRead(
            discussion, BrowserExecution("local-user")));
        Assert.False(DiscussionAccessPolicy.CanManageConfidentiality(
            discussion, BrowserExecution("local-user")));
    }

    private static DiscussionRead ConfidentialDiscussion(string owner = Owner) => new(
        "discussion-1", "Private", "session-1", DiscussionStatus.Idle,
        DateTime.UtcNow, DateTime.UtcNow, 1, null, owner, Guid.NewGuid(), Agent,
        Confidential: true);

    private static DefaultHttpContext Human(string userId)
        => Context(new ClaimsIdentity([new Claim("sub", userId)], "Bearer"));

    private static DefaultHttpContext LocalDefault()
        => Context(new ClaimsIdentity([new Claim("sub", "local-user")], "LocalDefault"));

    private static DefaultHttpContext AgentExecution(string agentId, string beneficiaryId)
    {
        var identity = JsonSerializer.Serialize(new
        {
            app = new { id = "nova", name = "Nova" },
            actor = new { kind = "agent", id = "nova", entityId = agentId },
            beneficiary = new { kind = "user", id = beneficiaryId },
            context = Array.Empty<object>(),
        });
        return ExecutionContext(beneficiaryId, identity);
    }

    private static DefaultHttpContext LegacyAgentExecution(string agentId, string beneficiaryId)
    {
        var identity = JsonSerializer.Serialize(new
        {
            actor = new { kind = "agent", entityId = agentId },
            beneficiary = new { kind = "user", id = beneficiaryId },
        });
        return ExecutionContext(beneficiaryId, identity);
    }

    private static DefaultHttpContext BrowserExecution(
        string beneficiaryId,
        string appId = "nova",
        string actorId = "nova",
        string actorKind = "app",
        string route = "/apps/nova/chat/discussion-1",
        string? parentExecutionId = null,
        string? subjectId = null)
    {
        var identity = JsonSerializer.Serialize(new
        {
            app = new { id = appId, name = appId },
            actor = new { kind = actorKind, id = actorId, name = actorId },
            beneficiary = new { kind = "user", id = beneficiaryId },
            context = new[] { new { kind = "browser", route } },
            parentExecutionId,
        });
        return ExecutionContext(subjectId ?? beneficiaryId, identity);
    }

    private static DefaultHttpContext ExecutionContext(string subjectId, string identity)
    {
        return Context(new ClaimsIdentity([
            new Claim("sub", subjectId),
            new Claim("token_use", "execution"),
            new Claim("execution_identity", identity),
        ], "Bearer"));
    }

    private static DefaultHttpContext Context(ClaimsIdentity identity)
        => new() { User = new ClaimsPrincipal(identity) };
}
