using Leaf.Plugins.Nova;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class ConversationUnreadTests
{
    [Fact]
    public void Stale_read_acknowledgement_cannot_clear_a_newer_revision()
    {
        Assert.Equal(1, ConversationRevision.Acknowledge(
            currentRevision: 2, currentReadRevision: 0, throughRevision: 1));
    }

    [Fact]
    public void Read_acknowledgement_is_monotonic_and_clamped()
    {
        Assert.Equal(2, ConversationRevision.Acknowledge(
            currentRevision: 2, currentReadRevision: 1, throughRevision: 99));
        Assert.Equal(1, ConversationRevision.Acknowledge(
            currentRevision: 2, currentReadRevision: 1, throughRevision: 0));
    }

    [Fact]
    public void Latest_visible_assistant_turn_wins()
    {
        var messages = new List<SessionMessage>
        {
            Message("user", "text", "question", "user-1", 1),
            Message("assistant", "thinking", "private", "turn-1", 2),
            Message("assistant", "text", "first answer", "turn-1", 3),
            Message("assistant", "tool_use", "{}", "turn-2", 4),
            Message("assistant", "text", "second answer", "turn-2", 5),
        };

        var uid = ConversationUnread.FindLatestAssistantUid(messages, new HashSet<string>());

        Assert.Equal("turn-2", uid);
    }

    [Fact]
    public void Empty_and_tool_only_turns_do_not_notify()
    {
        var messages = new List<SessionMessage>
        {
            Message("assistant", "tool_use", "{}", "tool-only", 1),
            Message("assistant", "text", "   ", "empty", 2),
        };

        Assert.Null(ConversationUnread.FindLatestAssistantUid(messages, new HashSet<string>()));
    }

    [Fact]
    public void Injected_proactive_message_is_not_counted_again()
    {
        var messages = new List<SessionMessage>
        {
            Message("assistant", "text", "generated", "turn-1", 1),
            Message("assistant", "text", "proactive", "nova-message-1", 2),
        };

        var uid = ConversationUnread.FindLatestAssistantUid(messages,
            new HashSet<string>(["nova-message-1"], StringComparer.Ordinal));

        Assert.Equal("turn-1", uid);
    }

    private static SessionMessage Message(
        string role, string eventType, string content, string uid, int minute)
        => new()
        {
            Role = role,
            EventType = eventType,
            Content = content,
            MessageUid = uid,
            Timestamp = new DateTime(2026, 8, 16, 12, minute, 0, DateTimeKind.Utc),
        };
}
