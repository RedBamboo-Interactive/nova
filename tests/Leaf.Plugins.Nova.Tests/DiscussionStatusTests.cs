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
    [InlineData("Stopped", "live", DiscussionStatus.Stopped)]
    public void Session_status_projects_to_the_discussion_indicator(
        string sessionStatus, string discussionType, string expected)
    {
        Assert.Equal(expected, DiscussionStatus.FromSessionStatus(sessionStatus, discussionType));
    }

    [Theory]
    [InlineData("maintenance_restart")]
    [InlineData("orphaned_on_restart")]
    public void Restart_recovery_projects_a_stopped_provider_as_resumable_idle(string stopReason)
    {
        Assert.Equal(DiscussionStatus.Idle,
            DiscussionStatus.FromSessionStatus("Stopped", "chat", stopReason));
    }

    [Fact]
    public void Explicit_stop_remains_terminal()
    {
        Assert.Equal(DiscussionStatus.Stopped,
            DiscussionStatus.FromSessionStatus("Stopped", "chat", "user_stopped"));
    }
}
