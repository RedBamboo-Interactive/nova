using Leaf.Plugins.Nova;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class LiveDiscussionAliasTests
{
    private const string NovaAgent = "nova";

    [Fact]
    public async Task Live_alias_resolves_to_the_current_open_live_without_GetAsync()
    {
        var current = Live("current");
        var calls = new List<string>();

        var resolved = await LiveDiscussionAlias.ResolveAsync("live", NovaAgent,
            id => { calls.Add(id); return Task.FromResult<DiscussionRead?>(null); },
            () => Task.FromResult(new List<DiscussionRead> { current }));

        Assert.Same(current, resolved);
        Assert.Empty(calls);
    }

    [Theory]
    [InlineData(DiscussionStatus.Archived)]
    [InlineData(DiscussionStatus.Archiving)]
    public async Task Live_alias_skips_closed_lives(string closedStatus)
    {
        var open = Live("open");
        var resolved = await Resolve("live",
            [Live("old", closedStatus), open]);

        Assert.Same(open, resolved);
    }

    [Fact]
    public async Task Live_alias_skips_lives_owned_by_another_agent_but_accepts_no_agent()
    {
        var unowned = Live("unowned", agentId: null);
        var resolved = await Resolve("live", [Live("other", agentId: "someone-else"), unowned]);

        Assert.Same(unowned, resolved);
    }

    [Fact]
    public async Task Live_alias_without_an_open_live_resolves_to_null()
    {
        Assert.Null(await Resolve("live", [Live("a", DiscussionStatus.Archived)]));
        Assert.Null(await Resolve("live", []));
    }

    [Fact]
    public async Task Concrete_id_still_uses_GetAsync_and_never_lists()
    {
        var target = Live("concrete", DiscussionStatus.Archived);
        var listed = false;

        var resolved = await LiveDiscussionAlias.ResolveAsync("concrete", NovaAgent,
            id => Task.FromResult<DiscussionRead?>(id == "concrete" ? target : null),
            () => { listed = true; return Task.FromResult(new List<DiscussionRead>()); });

        Assert.Same(target, resolved);
        Assert.False(listed);
    }

    private static Task<DiscussionRead?> Resolve(string id, List<DiscussionRead> lives)
        => LiveDiscussionAlias.ResolveAsync(id, NovaAgent,
            _ => Task.FromResult<DiscussionRead?>(null),
            () => Task.FromResult(lives));

    private static DiscussionRead Live(
        string id, string status = DiscussionStatus.Idle, string? agentId = NovaAgent)
        => new(
            id, "Title", "session", status,
            DateTime.UtcNow, DateTime.UtcNow, 1, null, "owner", Guid.NewGuid(),
            agentId, "live");
}
