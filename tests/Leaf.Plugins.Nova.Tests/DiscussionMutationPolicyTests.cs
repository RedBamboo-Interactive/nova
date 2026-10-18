using System.Security.Claims;
using Leaf.Plugins.Nova;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class DiscussionMutationPolicyTests
{
    [Fact]
    public void Reopen_happy_path_requires_an_archived_chat_with_a_session()
    {
        Assert.Equal(DiscussionReopenDecision.Reopen,
            DiscussionMutationPolicy.Reopen(Discussion(), Human("owner")));
    }

    [Theory]
    [InlineData(DiscussionStatus.Archiving, "chat", "session", "Archiving")]
    [InlineData(DiscussionStatus.Archived, "live", "session", "UnsupportedType")]
    [InlineData(DiscussionStatus.Archived, "heartbeat", "session", "UnsupportedType")]
    [InlineData(DiscussionStatus.Archived, "chat", null, "MissingSession")]
    [InlineData(DiscussionStatus.Stopped, "chat", "session", "AlreadyOpen")]
    [InlineData(DiscussionStatus.Idle, "chat", "session", "AlreadyOpen")]
    public void Reopen_refusals_and_idempotency_are_explicit(
        string status, string type, string? sessionId, string expected)
    {
        Assert.Equal(expected, DiscussionMutationPolicy.Reopen(
            Discussion(status, type, sessionId), Human("owner")).ToString());
    }

    [Fact]
    public void Reopen_rejects_an_unauthorized_owner_and_a_confidential_non_owner()
    {
        Assert.Equal(DiscussionReopenDecision.AccessDenied,
            DiscussionMutationPolicy.Reopen(Discussion(), Human("someone-else")));
        Assert.Equal(DiscussionReopenDecision.AccessDenied,
            DiscussionMutationPolicy.Reopen(Discussion(confidential: true), Human("someone-else")));
    }

    [Theory]
    [InlineData(DiscussionStatus.Archived, true)]
    [InlineData(DiscussionStatus.Archiving, true)]
    [InlineData(DiscussionStatus.Stopped, false)]
    [InlineData(DiscussionStatus.Idle, false)]
    public void Explicit_id_send_guard_rejects_both_closed_states(string status, bool rejected)
    {
        Assert.Equal(rejected, DiscussionMutationPolicy.RejectSend(Discussion(status)));
    }

    private static DiscussionRead Discussion(
        string status = DiscussionStatus.Archived,
        string type = "chat",
        string? sessionId = "session",
        bool confidential = false)
        => new(
            "discussion", "Title", sessionId, status,
            DateTime.UtcNow, DateTime.UtcNow, 1, null, "owner", Guid.NewGuid(),
            "agent", type, Confidential: confidential);

    private static DefaultHttpContext Human(string userId)
        => new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", userId)], "Bearer")),
        };
}
