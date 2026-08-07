using Leaf.Plugins.Nova;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class HeartbeatTurnContractTests
{
    [Fact]
    public void Assistant_tail_requires_new_nonblank_conversational_text()
    {
        var messages = new List<SessionMessage>
        {
            Message("assistant", "text", "old spoken turn"),
            Message("user", "text", "heartbeat tick"),
            Message("assistant", "thinking", "private reasoning"),
            Message("assistant", "tool_use", "tool payload"),
            Message("assistant", "text", "  \n"),
        };

        Assert.Null(HeartbeatService.FindAssistantTailAfter(messages, 1));

        messages.Add(Message("assistant", "text", "I checked the day and fixed the loose thread."));

        Assert.Equal(
            "I checked the day and fixed the loose thread.",
            HeartbeatService.FindAssistantTailAfter(messages, 1));
    }

    [Fact]
    public void Assistant_tail_is_scoped_to_the_captured_transcript_boundary()
    {
        var messages = new List<SessionMessage>
        {
            Message("assistant", "text", "yesterday's answer"),
            Message("user", "text", "new heartbeat tick"),
            Message("assistant", "tool_result", "done"),
        };

        Assert.Null(HeartbeatService.FindAssistantTailAfter(messages, messages.Count));
    }

    [Fact]
    public void Heartbeat_prompts_make_the_visible_session_reply_mandatory()
    {
        var config = new HeartbeatConfig(null, null, "deep", 15, 15);

        Assert.Contains("conversation in the Heartbeat tab", HeartbeatPrompts.Morning("digest", config));
        Assert.Contains("Heartbeat tab is a conversation", HeartbeatPrompts.Tick("digest", null, config));
        Assert.Contains("Do not use", HeartbeatPrompts.SpokenCompletionRequired);
    }

    private static SessionMessage Message(string role, string eventType, string content) => new()
    {
        Role = role,
        EventType = eventType,
        Content = content,
    };
}
