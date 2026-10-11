using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class VoiceSessionServiceTests
{
    [Fact]
    public void Initial_history_prefers_canonical_assistant_final_and_preserves_turn_order()
    {
        var snapshot = new SessionSnapshot("Idle", null, null,
        [
            Message(1, "user", "question", "user-1"),
            Message(2, "assistant", "working", "assistant-1", "commentary"),
            Message(3, "assistant", "answer", "assistant-1", "final"),
            Message(4, "user", "follow-up", "user-2"),
        ]);

        var history = VoiceSessionService.BuildInitialHistory(snapshot);

        Assert.Collection(history,
            item => { Assert.Equal("user", item.Role); Assert.Equal("question", item.Content); },
            item => { Assert.Equal("assistant", item.Role); Assert.Equal("answer", item.Content); },
            item => { Assert.Equal("user", item.Role); Assert.Equal("follow-up", item.Content); });
    }

    [Fact]
    public void Initial_history_is_bounded_from_the_newest_turns()
    {
        var snapshot = new SessionSnapshot("Idle", null, null,
            Enumerable.Range(1, 30).Select(index =>
                Message(index, index % 2 == 0 ? "assistant" : "user", $"turn-{index}", $"uid-{index}"))
            .ToList());

        var history = VoiceSessionService.BuildInitialHistory(snapshot);

        Assert.Equal(24, history.Count);
        Assert.Equal("turn-7", history[0].Content);
        Assert.Equal("turn-30", history[^1].Content);
    }

    private static SessionMessage Message(long id, string role, string content, string uid, string? phase = null)
        => new() { Id = id, Sequence = id, Role = role, EventType = "text", Content = content,
            MessageUid = uid, Phase = phase };
}
