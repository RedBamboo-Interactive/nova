using Leaf.Plugins.Nova.Endpoints;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class DiscussionSearchPolicyTests
{
    [Fact]
    public void Heartbeat_content_match_is_excluded_by_archive_scope_but_preserved_when_scope_is_absent()
    {
        var heartbeat = Discussion("heartbeat");

        Assert.False(DiscussionEndpoints.MatchesSearchTypeScope(
            heartbeat, includeLive: true, includeHeartbeats: false));
        Assert.True(DiscussionEndpoints.MatchesSearchTypeScope(
            heartbeat, includeLive: null, includeHeartbeats: null));
    }

    private static DiscussionRead Discussion(string type)
        => new(
            "discussion", "Title", "session", DiscussionStatus.Archived,
            DateTime.UtcNow, DateTime.UtcNow, 1, null, "owner", Guid.NewGuid(),
            "agent", type);
}
