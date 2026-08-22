using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class ConversationExporterTests
{
    [Fact]
    public void StripInjectedTagsRemovesScratchSpaceInstructions()
    {
        const string content = """
            <scratch-space path="S:\Nova\f13bad36">
            Put disposable artifacts here.
            </scratch-space>

            Keep this authored message.
            """;

        var stripped = ConversationExporter.StripInjectedTags(content);

        Assert.Equal("Keep this authored message.", stripped);
    }

    [Fact]
    public void StripInjectedTagsRemovesEveryInternalEnvelope()
    {
        const string content = """
            <nova-context timestamp="now">context</nova-context>
            <nova-prior-message role="assistant">prior</nova-prior-message>
            <scratch-space path="S:\Nova\discussion">scratch</scratch-space>
            Authored text.
            """;

        var stripped = ConversationExporter.StripInjectedTags(content);

        Assert.Equal("Authored text.", stripped);
    }

    [Fact]
    public void CollapseMessagesOrdersAsyncToolRecordsAndKeepsTheirDetails()
    {
        var turnUid = "turn-1";
        var result = ConversationExporter.CollapseMessages(
        [
            new SessionMessage
            {
                Role = "assistant",
                EventType = "tool_result",
                MessageUid = turnUid,
                Timestamp = new DateTime(2026, 8, 16, 15, 25, 29, DateTimeKind.Utc),
                ToolResult = "done",
            },
            new SessionMessage
            {
                Role = "assistant",
                EventType = "tool_use",
                MessageUid = turnUid,
                Timestamp = new DateTime(2026, 8, 16, 15, 25, 28, DateTimeKind.Utc),
                ToolName = "Bash",
                ToolInput = "{\"command\":\"pwd\"}",
            },
        ]);

        Assert.Equal("tool_use", result[0].EventType);
        Assert.Equal("Bash", result[0].ToolName);
        Assert.Equal("tool_result", result[1].EventType);
        Assert.Equal("done", result[1].ToolResult);
    }

    [Fact]
    public void CollapseMessagesKeepsCommentaryAndFinalAnswerSeparate()
    {
        var result = ConversationExporter.CollapseMessages(
        [
            new SessionMessage
            {
                Role = "assistant", EventType = "text", Content = "Working",
                Phase = "commentary", MessageUid = "turn-1",
                Timestamp = new DateTime(2026, 8, 21, 8, 0, 0, DateTimeKind.Utc),
            },
            new SessionMessage
            {
                Role = "assistant", EventType = "text", Content = "Done",
                Phase = "final_answer", MessageUid = "turn-1",
                Timestamp = new DateTime(2026, 8, 21, 8, 0, 1, DateTimeKind.Utc),
            },
        ]);

        Assert.Collection(result,
            commentary =>
            {
                Assert.Equal("Working", commentary.Content);
                Assert.Equal("commentary", commentary.Phase);
            },
            answer =>
            {
                Assert.Equal("Done", answer.Content);
                Assert.Equal("final_answer", answer.Phase);
            });
    }

    [Fact]
    public void SettledTurnSuppressesCommentaryButPreservesToolsAndLegacyText()
    {
        var result = ConversationExporter.SuppressSettledCommentary(
        [
            new() { Role = "assistant", EventType = "text", Content = "Working", Phase = "commentary", MessageUid = "turn-1" },
            new() { Role = "assistant", EventType = "tool_use", ToolName = "Read", MessageUid = "turn-1" },
            new() { Role = "assistant", EventType = "text", Content = "Done", Phase = "final_answer", MessageUid = "turn-1" },
            new() { Role = "assistant", EventType = "text", Content = "Legacy", MessageUid = "legacy" },
        ]);

        Assert.DoesNotContain(result, message => message.Content == "Working");
        Assert.Contains(result, message => message.ToolName == "Read");
        Assert.Contains(result, message => message.Content == "Done");
        Assert.Contains(result, message => message.Content == "Legacy");
    }
}
