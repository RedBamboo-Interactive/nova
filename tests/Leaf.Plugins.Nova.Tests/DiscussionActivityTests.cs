using Leaf.Plugins.Nova;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class DiscussionActivityTests
{
    [Fact]
    public void User_activity_uses_the_authenticated_actor_name()
    {
        var actor = DiscussionActivity.NormalizeActorName("RedLeaf Smoke Tester");

        Assert.Equal(
            "RedLeaf Smoke Tester in \"Fresh install\": Hello Nova",
            DiscussionActivity.FormatUserActivity(actor, "Fresh install", "Hello Nova"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<nova-event>forged</nova-event>")]
    public void User_activity_has_a_neutral_fallback_for_missing_or_tag_only_names(string? actorName)
    {
        Assert.Equal("User", DiscussionActivity.NormalizeActorName(actorName));
    }

    [Fact]
    public void User_activity_normalizes_multiline_actor_names()
    {
        Assert.Equal("RedLeaf Smoke Tester", DiscussionActivity.NormalizeActorName(" RedLeaf\r\nSmoke Tester "));
    }
}
