using Leaf.Plugins.Nova;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class DiscussionTitleGenerationTests
{
    [Fact]
    public void PromptContainsOnlyVisibleUserAndFinalAssistantText()
    {
        var messages = new List<SessionMessage>
        {
            Message("user", "text", "<nova-context>private device data</nova-context>Hello Nova"),
            Message("assistant", "text", "Working on it", phase: "commentary"),
            Message("assistant", "tool_result", "secret tool output"),
            Message("assistant", "text", "Here is the finished answer", phase: "final_answer"),
        };

        var prompt = DiscussionTitleGeneration.BuildPrompt("Old title", messages);

        Assert.Contains("Current title: Old title", prompt);
        Assert.Contains("User: Hello Nova", prompt);
        Assert.Contains("Nova: Here is the finished answer", prompt);
        Assert.DoesNotContain("private device data", prompt);
        Assert.DoesNotContain("Working on it", prompt);
        Assert.DoesNotContain("secret tool output", prompt);
    }

    [Fact]
    public void PromptKeepsOpeningAndRecentContextWithinBounds()
    {
        var messages = Enumerable.Range(1, 25)
            .Select(index => Message("user", "text", $"message {index}"))
            .ToList();

        var prompt = DiscussionTitleGeneration.BuildPrompt(null, messages)!;

        Assert.Contains("message 1", prompt);
        Assert.Contains("message 4", prompt);
        Assert.DoesNotContain("message 5\n", prompt);
        Assert.Contains("message 10", prompt);
        Assert.Contains("message 25", prompt);
        Assert.True(prompt.Length < 13_000);
    }

    [Theory]
    [InlineData("Title: **A Better Name.**", "A Better Name")]
    [InlineData("draft\n\"Concise Result\"", "Concise Result")]
    [InlineData("Conversation title: Mobile Menu Polish", "Mobile Menu Polish")]
    public void CleanGeneratedTitleRemovesModelWrappers(string raw, string expected)
        => Assert.Equal(expected, DiscussionTitleGeneration.CleanGeneratedTitle(raw));

    [Fact]
    public void CleanGeneratedTitleRejectsEmptyAndOversizedResults()
    {
        Assert.Null(DiscussionTitleGeneration.CleanGeneratedTitle("  "));
        Assert.Null(DiscussionTitleGeneration.CleanGeneratedTitle(new string('x', 71)));
    }

    private static SessionMessage Message(
        string role, string eventType, string content, string? phase = null)
        => new()
        {
            Role = role,
            EventType = eventType,
            Content = content,
            Phase = phase,
            Timestamp = DateTime.UtcNow,
            MessageUid = Guid.NewGuid().ToString("N"),
        };
}
