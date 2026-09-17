using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class ReactionContextTests
{
    [Fact]
    public void SessionTranscriptMessagesSupplyMissingReactionPreviews()
    {
        var previews = new Dictionary<string, string>();

        MessagePipeline.AddReactionPreview(
            previews,
            "assistant-turn",
            "A normal assistant response from the canonical compute transcript.");

        Assert.Equal(
            "A normal assistant response from the canonical compute tr...",
            previews["assistant-turn"]);
    }

    [Fact]
    public void DirectDiscussionMessagePreviewKeepsPrecedence()
    {
        var previews = new Dictionary<string, string>
        {
            ["shared"] = "Persisted discussion message",
        };

        MessagePipeline.AddReactionPreview(
            previews, "shared", "Transcript fallback must not replace it");

        Assert.Equal("Persisted discussion message", previews["shared"]);
    }

    [Fact]
    public void SessionTranscriptPrefersFinalAnswerOverEarlierTurnRecords()
    {
        var previews = new Dictionary<string, string>();
        var messages = new[]
        {
            new SessionMessage
            {
                Role = "assistant", EventType = "text", Content = "I am checking that now",
                Phase = "commentary", MessageUid = "turn-1", Timestamp = DateTime.UtcNow,
            },
            new SessionMessage
            {
                Role = "assistant", EventType = "tool_result", Content = "internal tool output",
                MessageUid = "turn-1", Timestamp = DateTime.UtcNow.AddMilliseconds(1),
            },
            new SessionMessage
            {
                Role = "assistant", EventType = "text", Content = "The verified final answer",
                Phase = "final_answer", MessageUid = "turn-1", Timestamp = DateTime.UtcNow.AddMilliseconds(2),
            },
        };

        MessagePipeline.AddSessionReactionPreviews(previews, messages);

        Assert.Equal("The verified final answer", previews["turn-1"]);
    }

    [Fact]
    public async Task DirectDiscussionPreviewDoesNotFetchComputeTranscript()
    {
        var discussionId = Guid.NewGuid();
        var reactions = new LeafRecord[]
        {
            new(1, "message-reactions", discussionId, "owner-1", new JsonObject
            {
                ["action"] = "add",
                ["emoji"] = "👍",
                ["actor_name"] = "Laurent",
                ["message_key"] = "direct-message",
            }, DateTimeOffset.UtcNow),
        };
        var discussions = TestProxy.Create<IDiscussions>((method, _) => method switch
        {
            "GetReactionsAsync" => Task.FromResult<IReadOnlyList<LeafRecord>>(reactions),
            "GetMessagesAsync" => Task.FromResult<IReadOnlyList<DiscussionMessage>>(
            [
                new DiscussionMessage(1, discussionId, "user", "Persisted message", new JsonObject
                {
                    ["uid"] = "direct-message",
                }, DateTimeOffset.UtcNow),
            ]),
            _ => throw new NotSupportedException(method),
        });
        var compute = new RedComputeClient(new RejectingGateway());
        var pipeline = new MessagePipeline(
            null!, discussions, null!, null!, compute, null!, null!, null!, null!, null!,
            NullLogger<MessagePipeline>.Instance);
        var discussion = new DiscussionRead(
            "discussion-1", null, "session-1", DiscussionStatus.Idle,
            DateTime.UtcNow, DateTime.UtcNow, 1, null, "owner-1", discussionId, null);

        var lines = await pipeline.GetRecentReactionLinesAsync(
            discussion, DateTime.MinValue, CancellationToken.None);

        Assert.Equal(@"Laurent reacted 👍 to: ""Persisted message""", Assert.Single(lines));
    }

    [Fact]
    public async Task ReactionContextResolvesCanonicalComputeTranscriptMessage()
    {
        var discussionId = Guid.NewGuid();
        var reactions = new LeafRecord[]
        {
            new(1, "message-reactions", discussionId, "owner-1", new JsonObject
            {
                ["action"] = "add",
                ["emoji"] = "🔥",
                ["actor_name"] = "Laurent",
                ["message_key"] = "assistant-turn",
            }, DateTimeOffset.UtcNow),
        };
        var discussions = TestProxy.Create<IDiscussions>((method, _) => method switch
        {
            "GetReactionsAsync" => Task.FromResult<IReadOnlyList<LeafRecord>>(reactions),
            "GetMessagesAsync" => Task.FromResult<IReadOnlyList<DiscussionMessage>>([]),
            _ => throw new NotSupportedException(method),
        });
        var compute = new RedComputeClient(new SessionGateway());
        var pipeline = new MessagePipeline(
            null!, discussions, null!, null!, compute, null!, null!, null!, null!, null!,
            NullLogger<MessagePipeline>.Instance);
        var discussion = new DiscussionRead(
            "discussion-1", null, "session-1", DiscussionStatus.Idle,
            DateTime.UtcNow, DateTime.UtcNow, 1, null, "owner-1", discussionId, null);

        var lines = await pipeline.GetRecentReactionLinesAsync(
            discussion, DateTime.MinValue, CancellationToken.None);

        Assert.Equal(
            @"Laurent reacted 🔥 to: ""Canonical assistant answer""",
            Assert.Single(lines));
    }

    private sealed class SessionGateway : IComputeGateway
    {
        public Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            ComputeProvenance? provenance = null,
            CancellationToken ct = default)
        {
            Assert.Equal(
                "/ai-session/sessions/session-1?tail=500",
                request.RequestUri?.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"session":{"status":"Idle"},"messages":[{
                      "id":1,
                      "role":"assistant",
                      "eventType":"text",
                      "content":"Canonical assistant answer",
                      "messageUid":"assistant-turn",
                      "timestamp":"2026-09-17T10:00:00Z"
                    }]}
                    """),
            });
        }
    }

    private sealed class RejectingGateway : IComputeGateway
    {
        public Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            ComputeProvenance? provenance = null,
            CancellationToken ct = default) =>
            throw new Xunit.Sdk.XunitException("The compute transcript should not be requested");
    }

    private class TestProxy : DispatchProxy
    {
        private Func<string, object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => Handler(method!.Name);

        public static T Create<T>(Func<string, object?[], object?> handler) where T : class
        {
            var proxy = DispatchProxy.Create<T, TestProxy>();
            ((TestProxy)(object)proxy).Handler = method => handler(method, []);
            return proxy;
        }
    }
}
