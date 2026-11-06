using Leaf.Plugins.Nova;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class DiscussionOwnershipTests
{
    private const string UserId = "28a07bf5-3a37-47d4-a9ab-bdc09221d547";
    private const string SecondUserId = "9b0c4f1e-6d2a-4e8b-9f3c-1a2b3c4d5e6f";

    [Fact]
    public void LazyLocalDeliveryDiscussionIsClaimedByFirstAuthenticatedReply()
    {
        // Owner unknown: the old rule, whoever signed replies first.
        var owner = DiscussionOwnership.ResolveForSessionStart("local-user", UserId, needsSession: true, null);

        Assert.Equal(UserId, owner);
    }

    [Fact]
    public void LazyLocalDeliveryDiscussionIsClaimedByTheInstallationOwner()
        => Assert.Equal(UserId,
            DiscussionOwnership.ResolveForSessionStart("local-user", UserId, needsSession: true, UserId));

    [Theory]
    [InlineData("local-user")]
    [InlineData(null)]
    public void ASecondSignedUserNeverTakesOverAnUnownedOrLocalDiscussion(string? discussionOwnerId)
        => Assert.Equal(discussionOwnerId,
            DiscussionOwnership.ResolveForSessionStart(discussionOwnerId, SecondUserId, needsSession: true, UserId));

    [Theory]
    [InlineData("local-user", null, true, "local-user")]
    [InlineData("local-user", "local-user", true, "local-user")]
    [InlineData(UserId, "another-user", true, UserId)]
    [InlineData("local-user", UserId, false, "local-user")]
    public void ExistingOrAuthoredOwnershipIsPreserved(
        string discussionOwnerId, string? replyingUserId, bool needsSession, string expected)
    {
        Assert.Equal(expected, DiscussionOwnership.ResolveForSessionStart(
            discussionOwnerId, replyingUserId, needsSession, null));
        Assert.Equal(expected, DiscussionOwnership.ResolveForSessionStart(
            discussionOwnerId, replyingUserId, needsSession, UserId));
    }
}
