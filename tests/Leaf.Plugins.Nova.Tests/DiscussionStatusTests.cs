using Leaf.Plugins.Nova;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class DiscussionStatusTests
{
    [Theory]
    [InlineData("Active", "chat", DiscussionStatus.Thinking)]
    [InlineData("Idle", "chat", DiscussionStatus.Idle)]
    [InlineData("Starting", "chat", DiscussionStatus.Idle)]
    [InlineData("Stopped", "chat", DiscussionStatus.Stopped)]
    [InlineData("Error", "chat", DiscussionStatus.Stopped)]
    [InlineData("Stopped", "live", DiscussionStatus.Idle)]
    public void Session_status_projects_to_the_discussion_indicator(
        string sessionStatus, string discussionType, string expected)
    {
        Assert.Equal(expected, DiscussionStatus.FromSessionStatus(sessionStatus, discussionType));
    }
}
