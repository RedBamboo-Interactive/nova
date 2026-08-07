using Leaf.Plugins.Nova.Endpoints;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class DiscussionConvergenceTests
{
    [Fact]
    public void AcceptedUserMessageRemainsPendingWhileSessionMirrorLags()
    {
        var pending = DiscussionEndpoints.FindPendingUserMessageUids(
            ["older-user-message"],
            ["older-user-message", "just-accepted-message"]);

        Assert.Equal(["just-accepted-message"], pending);
    }

    [Fact]
    public void MirroredUserMessageStopsUsingPersistedBridgeCopy()
    {
        var pending = DiscussionEndpoints.FindPendingUserMessageUids(
            ["older-user-message", "just-accepted-message"],
            ["older-user-message", "just-accepted-message"]);

        Assert.Empty(pending);
    }

    [Fact]
    public void MissingAndDuplicateUidsCannotCreateBridgeDuplicates()
    {
        var pending = DiscussionEndpoints.FindPendingUserMessageUids(
            [null, "mirrored"],
            [null, "", "pending", "pending", "mirrored"]);

        Assert.Equal(["pending"], pending);
    }
}
