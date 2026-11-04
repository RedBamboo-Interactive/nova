using System.Text.Json.Nodes;
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

        Assert.Contains("conversation in the Heartbeat tab", HeartbeatPrompts.Morning("digest", config, "Nova", true));
        Assert.Contains("Heartbeat tab is a conversation", HeartbeatPrompts.Tick("digest", null, config, true));
        Assert.Contains("Leaf Discord community", HeartbeatPrompts.Morning("digest", config, "Nova", true));
        Assert.Contains("Leaf Discord", HeartbeatPrompts.Tick("digest", null, config, true));
        Assert.Contains("Do not use", HeartbeatPrompts.SpokenCompletionRequired);
    }

    [Fact]
    public void Default_heartbeat_prompts_are_installation_local()
    {
        var config = new HeartbeatConfig(null, null, "deep", 15, 15);
        var morning = HeartbeatPrompts.Morning("digest", config, "Nova", true);
        var tick = HeartbeatPrompts.Tick("digest", null, config, true);

        Assert.DoesNotContain("Laurent", morning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Laurent", tick, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("local owner", morning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("local owner", tick, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_agent_with_initiatives_on_is_told_how_to_start_one_and_what_bounds_it()
    {
        var config = new HeartbeatConfig(null, null, "deep", 15, 15);
        var morning = HeartbeatPrompts.Morning("digest", config, "Nova", true);
        var tick = HeartbeatPrompts.Tick("digest", null, config, true);

        foreach (var prompt in new[] { morning, tick })
        {
            Assert.Contains("/discussions with", prompt);
            Assert.Contains("Initiative: <what changes>", prompt);
            Assert.Contains("idempotencyKey", prompt);
            Assert.Contains("standing invitations", prompt.Replace("standing-invitations", "standing invitations"));
            Assert.DoesNotContain("Initiatives are off", prompt);
        }
        Assert.Contains("Do real work", morning);
        Assert.Contains("no restart, rebuild or release", morning);
        Assert.Contains("never change it yourself", morning);
        Assert.Contains("Your identity's limits win", morning);
        Assert.Contains("Pick something and do it", tick);
    }

    [Fact]
    public void An_agent_with_initiatives_off_keeps_nothing_that_commissions_unrequested_work()
    {
        var config = new HeartbeatConfig(null, null, "deep", 15, 15);
        var morning = HeartbeatPrompts.Morning("digest", config, "Nova", false);
        var tick = HeartbeatPrompts.Tick("digest", null, config, false);

        foreach (var prompt in new[] { morning, tick })
        {
            Assert.Contains("Initiatives are off", prompt);
            Assert.Contains("overrides anything an earlier tick said", prompt);
            Assert.DoesNotContain("Initiative: <what changes>", prompt);
        }
        Assert.DoesNotContain("Do real work", morning);
        Assert.DoesNotContain("nobody's asked you to", morning);
        Assert.DoesNotContain("did nothing with it", morning);
        Assert.DoesNotContain("Pick something and do it", tick);
        Assert.DoesNotContain("window for agency", tick);
    }

    [Theory]
    [InlineData("{\"live\":true}", true)]
    [InlineData("{\"initiatives\":true}", true)]
    [InlineData("{\"initiatives\":null}", true)]
    [InlineData("{\"initiatives\":false}", false)]
    public void Only_an_explicit_false_turns_initiatives_off(string data, bool expected)
        => Assert.Equal(expected, HeartbeatService.AreInitiativesEnabled(JsonNode.Parse(data)!.AsObject()));

    [Fact]
    public void The_morning_briefing_addresses_the_agent_it_belongs_to()
    {
        var morning = HeartbeatPrompts.Morning("digest", new HeartbeatConfig(null, null, "deep", 15, 15), "Leni", true);

        Assert.Contains("You are Leni.", morning);
        Assert.DoesNotContain("You are Nova", morning);
    }

    private static SessionMessage Message(string role, string eventType, string content) => new()
    {
        Role = role,
        EventType = eventType,
        Content = content,
    };
}
