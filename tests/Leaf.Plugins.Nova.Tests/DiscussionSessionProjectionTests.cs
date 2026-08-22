using System.Text.Json;
using Leaf.Plugins.Nova;
using Leaf.Plugins.Nova.Endpoints;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class DiscussionSessionProjectionTests
{
    [Fact]
    public void DiscussionInfoCarriesTheHiddenBootstrapIdentity()
    {
        var discussion = new DiscussionRead(
            "discussion-1", "Meet Nova", "session-1", "idle",
            DateTime.UtcNow, DateTime.UtcNow, 0, null, "owner-1", Guid.NewGuid(),
            "agent-1", SetupBootstrapMessageUid: "setup-bootstrap");

        var json = JsonSerializer.Serialize(DiscussionStore.ToInfo(discussion));

        Assert.Contains("\"setupBootstrapMessageUid\":\"setup-bootstrap\"", json);
    }

    [Fact]
    public void ToolUseIsVisibleAndKeepsItsInvocation()
    {
        var message = new ConversationExporter.CollapsedMessage
        {
            Role = "assistant",
            EventType = "tool_use",
            ToolName = "Bash",
            ToolInput = "{\"command\":\"pwd\"}",
        };

        Assert.True(DiscussionEndpoints.IsVisibleSessionMessage(message));
        var json = JsonSerializer.Serialize(
            DiscussionEndpoints.MapSessionMessageParts(message, ""));
        Assert.Contains("\"type\":\"tool_use\"", json);
        Assert.Contains("\"toolName\":\"Bash\"", json);
        Assert.Contains("pwd", json);
    }

    [Fact]
    public void PayloadBackedToolResultIsVisibleWithoutInlineContent()
    {
        using var payload = JsonDocument.Parse(
            """{"recordId":42,"kind":"tool-output","length":12,"contentType":"text/plain","encoding":"utf-8","sha256":"abc","available":true}""");
        var message = new ConversationExporter.CollapsedMessage
        {
            Role = "assistant",
            EventType = "tool_result",
            PayloadRef = payload.RootElement.Clone(),
        };

        Assert.True(DiscussionEndpoints.IsVisibleSessionMessage(message));
        var json = JsonSerializer.Serialize(
            DiscussionEndpoints.MapSessionMessageParts(message, ""));
        Assert.Contains("\"type\":\"tool_result\"", json);
        Assert.Contains("\"recordId\":42", json);
    }

    [Fact]
    public void TextPartCarriesItsCodexMessagePhase()
    {
        var message = new ConversationExporter.CollapsedMessage
        {
            Role = "assistant",
            EventType = "text",
            Content = "Done",
            Phase = "final_answer",
        };

        var json = JsonSerializer.Serialize(
            DiscussionEndpoints.MapSessionMessageParts(message, message.Content));

        Assert.Contains("\"phase\":\"final_answer\"", json);
    }
}
