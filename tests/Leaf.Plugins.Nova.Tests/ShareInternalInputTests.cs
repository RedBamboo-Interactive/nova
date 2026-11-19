using System.Text.Json.Nodes;
using Leaf.Plugins.Nova;
using Leaf.Sdk.Services;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class ShareInternalInputTests
{
    private const string Frame = "<nova-event source=\"agent-note\" from-agent=\"Nova\">Private brief</nova-event>";
    private const string Admission = """{"inputMessageUids":["brief"],"metadata":{"novaEvent":true,"senderAgentId":"nova"}}""";

    [Theory]
    [InlineData(null, true)]
    [InlineData(Admission, true)]
    [InlineData("{\"metadata\":{\"app\":\"nova\",\"internalMessage\":false}}", false)]
    [InlineData("{\"metadata\":{\"novaEvent\":true,\"app\":\"nova\"}}", false)]
    [InlineData("{\"inputMessageUids\":[\"one\",\"two\"],\"metadata\":{\"novaEvent\":true}}", false)]
    [InlineData("{}", false)]
    [InlineData("{", false)]
    [InlineData("null", false)]
    public void AdmissionPreservesAuthoredFramesAndAmbiguousInputs(string? admission, bool internalInput)
        => Assert.Equal(internalInput, NovaShareEnricher.IsInternalInput(Frame, admission));

    [Fact]
    public async Task TranscriptOmitsOpeningBriefAndKeepsTheFinalAndTypedFrame()
    {
        var enricher = new NovaShareEnricher(null!, null!, null!, null!, null!);
        var snapshot = new SessionSnapshot("Idle", null, "Introduction", [
            new() { Role = "user", EventType = "text", Content = Frame, AttachmentsJson = Admission },
            new() { Role = "assistant", EventType = "text", Content = "Hi Ionut!" },
            new() { Role = "user", EventType = "text", Content = Frame,
                AttachmentsJson = """{"metadata":{"app":"nova","internalMessage":false}}""" },
        ]);
        var result = await enricher.ProjectMessagesAsync([], snapshot, "Nova", default);
        Assert.Equal(2, result.Count);
        Assert.Equal("assistant", result[0].Role);
        Assert.Equal("Hi Ionut!", result[0].Content);
        Assert.Equal("user", result[1].Role);
        Assert.Equal(Frame, result[1].Content);
    }

    [Fact]
    public async Task FallbackOmitsEventOverlayAndTranscriptCopyAndPreservesPersonInput()
    {
        var enricher = new NovaShareEnricher(null!, null!, null!, null!, null!);
        DiscussionMessage Row(string role, string text, string source) =>
            new(1, Guid.Empty, role, text, new JsonObject { ["source"] = source }, DateTimeOffset.UtcNow);
        var rows = new[] {
            Row("user", "Private brief", "event:agent-note"),
            Row("user", Frame, "session-transcript"),
            Row("assistant", "Hi Ionut!", "session-transcript"),
            Row("user", Frame, "user-message"),
        };
        var result = await enricher.ProjectMessagesAsync(rows, null, "Nova", default);
        Assert.Equal(2, result.Count);
        Assert.Equal("Hi Ionut!", result[0].Content);
        Assert.Equal(Frame, result[1].Content);
    }

    [Fact]
    public void AQuotedOrMixedFrameIsNotAnInjectorInput()
    {
        Assert.False(NovaShareEnricher.IsInternalInput("Please explain " + Frame, null));
        Assert.False(NovaShareEnricher.IsInternalInput(Frame + " My own question", Admission));
    }
}
