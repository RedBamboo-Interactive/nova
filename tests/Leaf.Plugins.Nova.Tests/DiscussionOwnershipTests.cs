using Leaf.Plugins.Nova;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class DiscussionOwnershipTests
{
    private const string UserId = "28a07bf5-3a37-47d4-a9ab-bdc09221d547";

    [Fact]
    public void LazyLocalDeliveryDiscussionIsClaimedByFirstAuthenticatedReply()
    {
        var owner = DiscussionOwnership.ResolveForSessionStart("local-user", UserId, needsSession: true);

        Assert.Equal(UserId, owner);
    }

    [Theory]
    [InlineData("local-user", null, true, "local-user")]
    [InlineData("local-user", "local-user", true, "local-user")]
    [InlineData(UserId, "another-user", true, UserId)]
    [InlineData("local-user", UserId, false, "local-user")]
    public void ExistingOrAuthoredOwnershipIsPreserved(
        string discussionOwnerId, string? replyingUserId, bool needsSession, string expected)
    {
        var owner = DiscussionOwnership.ResolveForSessionStart(
            discussionOwnerId, replyingUserId, needsSession);

        Assert.Equal(expected, owner);
    }
}
