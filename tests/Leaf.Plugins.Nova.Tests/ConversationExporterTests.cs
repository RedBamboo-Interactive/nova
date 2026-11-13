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
    public void StripInjectedTagsKeepsOnlyVisibleVoiceUtterance()
    {
        const string content = """
            <voice-causal-context>
            [{"role":"assistant","content":"private recent context"}]
            </voice-causal-context>

            <voice-current-utterance>
            Can you whisper
            </voice-current-utterance>
            """;

        var stripped = ConversationExporter.StripInjectedTags(content);

        Assert.Equal("Can you whisper", stripped);
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

    // LIVE fe9ef992, 13 Nov 2026: rows 32966552 and 32966571 are one event (epoch, sequence 1387)
    // stored twice after a timed-out write was re-queued without its idempotency key.
    [Fact]
    public void CollapseMessagesRendersTwoRowsOfOneSequenceOnce()
    {
        const string epoch = "6a7d3edbb34ba46017a965ffbca1a3b5";
        const string text = "It's 16:00, time for Tinder's gaba.";
        var at = new DateTime(2026, 11, 13, 14, 56, 1, 148, DateTimeKind.Utc);
        SessionMessage Final(long id) => new()
        {
            Id = id, Role = "assistant", EventType = "text", Content = text, Phase = "final_answer",
            MessageId = "msg_011Ch17yu7PQXCekAdQvTEPa:1", MessageUid = "7bccab843c4748c1aece1d9fadce7b37",
            Epoch = epoch, Sequence = 1387, Timestamp = at,
        };

        var result = ConversationExporter.CollapseMessages(
        [
            Final(32966552),
            new SessionMessage
            {
                Id = 32966559, Role = "assistant", EventType = "status", Content = "idle",
                Epoch = epoch, Sequence = 1389, Timestamp = at.AddSeconds(15),
            },
            Final(32966571),
        ]);

        Assert.Equal(text, Assert.Single(result).Content);
    }

    [Fact]
    public void CollapseMessagesKeepsIdenticalTextAtDistinctSequences()
    {
        var at = new DateTime(2026, 11, 13, 14, 56, 1, DateTimeKind.Utc);
        var result = ConversationExporter.CollapseMessages(
        [
            new SessionMessage { Role = "assistant", EventType = "text", Content = "ha", MessageUid = "t", Epoch = "e", Sequence = 1, Timestamp = at },
            new SessionMessage { Role = "assistant", EventType = "text", Content = "ha", MessageUid = "t", Epoch = "e", Sequence = 2, Timestamp = at },
            new SessionMessage { Role = "assistant", EventType = "text", Content = "!", MessageUid = "t", Timestamp = at },
            new SessionMessage { Role = "assistant", EventType = "text", Content = "!", MessageUid = "t", Timestamp = at },
        ]);

        Assert.Equal("haha!!", Assert.Single(result).Content);
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

}
