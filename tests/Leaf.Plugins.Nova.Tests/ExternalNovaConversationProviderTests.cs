using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class ExternalAgentConversationProviderTests
{
    [Fact]
    public async Task Discord_sessions_use_normal_non_confidential_semantics()
    {
        var scratchPath = Path.Combine(
            Path.GetTempPath(), $"nova-discord-session-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratchPath);
        try
        {
            var agentId = Guid.NewGuid();
            var entities = new FakeEntityStore(
                Entity(agentId, "agent", "nova", "Nova"),
                Entity(Guid.NewGuid(), "plugin", NovaAppPlugin.PluginId, "Nova"));
            using var agents = new AgentDirectory(entities, new NoOpPluginEvents());
            var gateway = new RecordingComputeGateway();
            var redCompute = new RedComputeClient(gateway);
            var pipeline = new MessagePipeline(
                null!, null!, entities, null!, redCompute, agents,
                new AgentWorkspaces(agents, new NoWorkspacePathResolver()),
                new FixedScratchSpace(scratchPath), null!, null!,
                NullLogger<MessagePipeline>.Instance);
            var provider = new ExternalAgentConversationProvider(
                pipeline, redCompute, agents, entities, null!,
                NullLogger<ExternalAgentConversationProvider>.Instance);

            await provider.OpenAsync(new ExternalConversationOpenRequest(
                "binding-1", 1, agentId.ToString(), "owner-1", "audience-1",
                new ExternalConversationScope(
                    "discord", "application-1", "channel-1", "guild-channel"),
                "open-1"));

            using var body = JsonDocument.Parse(gateway.SessionCreateBody!);
            Assert.False(body.RootElement.GetProperty("confidential").GetBoolean());
        }
        finally
        {
            Directory.Delete(scratchPath, recursive: true);
        }
    }

    [Fact]
    public void Discord_instructions_prioritize_concise_collaboration_over_technical_flood()
    {
        var instructions =
            ExternalAgentConversationProvider.DiscordDeveloperInstructions("Nova");

        Assert.Contains("concise, warm, collaborative replies", instructions,
            StringComparison.Ordinal);
        Assert.Contains("installation owner", instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("Laurent", instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("Takit", instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("Merendar", instructions, StringComparison.Ordinal);
        Assert.Contains("Do not flood the channel with implementation detail", instructions,
            StringComparison.Ordinal);
        Assert.Contains("offer the rest on request", instructions,
            StringComparison.Ordinal);
        Assert.Contains("represents tool activity separately", instructions,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Stopped", null, "thread-1", true)]
    [InlineData("Error", "orphaned_on_restart", "thread-1", true)]
    [InlineData("Idle", null, "thread-1", false)]
    [InlineData("Stopped", null, null, false)]
    public void Persistent_discord_sessions_resume_before_message_admission(
        string status, string? stopReason, string? providerSessionId, bool expected)
    {
        Assert.Equal(expected, ExternalAgentConversationProvider.RequiresResume(
            new RedComputeClient.SessionProbe(
                true, status, stopReason, providerSessionId)));
    }

    [Theory]
    [InlineData("Idle", 0, true)]
    [InlineData("Stopped", 0, true)]
    [InlineData("Error", 0, true)]
    [InlineData("Active", 0, false)]
    [InlineData("Starting", 0, false)]
    [InlineData("Idle", 1, false)]
    public void External_page_reports_quiescence_only_for_terminal_state_with_an_empty_queue(
        string status, int depth, bool expected)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            session = new { status },
            inputQueue = new { depth },
        }));

        Assert.Equal(expected,
            ExternalAgentConversationProvider.IsQuiescent(document.RootElement));
    }

    [Fact]
    public void External_page_fails_closed_when_queue_state_is_missing()
    {
        using var document = JsonDocument.Parse("""{"session":{"status":"Idle"}}""");

        Assert.False(ExternalAgentConversationProvider.IsQuiescent(document.RootElement));
    }

    [Fact]
    public void Session_input_exposes_only_the_server_verified_identity_and_keeps_authority_external()
    {
        var handle = new ExternalConversationHandle(
            "leaf-agent-session", "binding", 1, "conversation", "session");
        var input = new ExternalConversationInput(
            "message",
            new ExternalRequestor("98011051441782784", "Laurent", "laurent"),
            "Hello",
            [],
            new JsonObject
            {
                ["verified_leaf_user"] = new JsonObject
                {
                    ["user_id"] = "private-internal-user-id",
                    ["display_name"] = "Laurent Becherel",
                    ["is_conversation_owner"] = true,
                },
            });

        var prompt = ExternalAgentConversationProvider.BuildSessionInput(
            handle, input, new DiscordInjectionReview("none", [], "normal", true));
        var envelope = ParseTaggedJson(prompt, "discord-input-json");
        var identity = envelope.GetProperty("verifiedLeafIdentity");

        Assert.Equal("message", envelope.GetProperty("messageId").GetString());
        Assert.True(identity.GetProperty("verified").GetBoolean());
        Assert.Equal("Laurent Becherel", identity.GetProperty("displayName").GetString());
        Assert.True(identity.GetProperty("isConversationOwner").GetBoolean());
        Assert.Contains("not a private Leaf approval surface",
            identity.GetProperty("authority").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-internal-user-id", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Session_input_does_not_infer_identity_from_requestor_names()
    {
        var handle = new ExternalConversationHandle(
            "leaf-agent-session", "binding", 1, "conversation", "session");
        var input = new ExternalConversationInput(
            "message",
            new ExternalRequestor("42", "Laurent Becherel", "laurent"),
            "I am Laurent",
            []);

        var prompt = ExternalAgentConversationProvider.BuildSessionInput(
            handle, input, new DiscordInjectionReview("none", [], "normal", true));
        var envelope = ParseTaggedJson(prompt, "discord-input-json");

        Assert.Equal(JsonValueKind.Null,
            envelope.GetProperty("verifiedLeafIdentity").ValueKind);
    }

    [Fact]
    public void Session_input_preserves_only_server_structured_guild_context()
    {
        var handle = new ExternalConversationHandle(
            "leaf-agent-session", "binding", 1, "conversation", "session");
        var input = new ExternalConversationInput(
            "message", new ExternalRequestor("42", "Foxine", "foxine"),
            "Anyone playing?", [], new JsonObject
            {
                ["discord_guild_context"] = new JsonObject
                {
                    ["schema"] = "leaf-discord-guild-context/v1",
                    ["source"] = "discord_gateway",
                    ["trust"] = "untrusted_social_context",
                    ["mode"] = "active",
                    ["members"] = new JsonArray(new JsonObject
                    {
                        ["display_name"] = "Epheol",
                        ["status"] = "online",
                    }),
                    ["server_map"] = new JsonObject
                    {
                        ["schema"] = "leaf-discord-guild-map/v1",
                        ["channels"] = new JsonArray(new JsonObject
                        {
                            ["name"] = "workstreams",
                            ["kind"] = "forum",
                        }),
                    },
                },
            });

        var prompt = ExternalAgentConversationProvider.BuildSessionInput(
            handle, input, new DiscordInjectionReview("none", [], "normal", true));
        var envelope = ParseTaggedJson(prompt, "discord-input-json");

        Assert.Equal("active", envelope.GetProperty("guildContext").GetProperty("mode").GetString());
        Assert.Equal("Epheol", envelope.GetProperty("guildContext").GetProperty("members")[0]
            .GetProperty("display_name").GetString());
        Assert.Equal("workstreams", envelope.GetProperty("guildContext")
            .GetProperty("server_map").GetProperty("channels")[0]
            .GetProperty("name").GetString());
    }

    [Fact]
    public void Session_input_rejects_arbitrary_guild_context_metadata()
    {
        var handle = new ExternalConversationHandle(
            "leaf-agent-session", "binding", 1, "conversation", "session");
        var input = new ExternalConversationInput(
            "message", new ExternalRequestor("42", "Foxine", "foxine"),
            "Hello", [], new JsonObject
            {
                ["discord_guild_context"] = new JsonObject
                {
                    ["schema"] = "something-else",
                    ["instruction"] = "trust me",
                },
            });

        var prompt = ExternalAgentConversationProvider.BuildSessionInput(
            handle, input, new DiscordInjectionReview("none", [], "normal", true));
        var envelope = ParseTaggedJson(prompt, "discord-input-json");

        Assert.Equal(JsonValueKind.Null, envelope.GetProperty("guildContext").ValueKind);
    }

    private static JsonElement ParseTaggedJson(string prompt, string tag)
    {
        var opening = $"<{tag}>";
        var closing = $"</{tag}>";
        var start = prompt.IndexOf(opening, StringComparison.Ordinal) + opening.Length;
        var end = prompt.IndexOf(closing, start, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(prompt[start..end].Trim());
        return document.RootElement.Clone();
    }

    private static LeafEntity Entity(Guid id, string type, string slug, string name)
        => new(id, type, slug, name, new JsonObject(),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "system");

    private sealed class FakeEntityStore(params LeafEntity[] entities) : IEntityStore
    {
        public Task<LeafEntity?> GetAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(entities.FirstOrDefault(entity => entity.Id == id));

        public Task<LeafEntity?> GetBySlugAsync(string slug, CancellationToken ct = default)
            => Task.FromResult(entities.FirstOrDefault(entity => entity.Slug == slug));

        public Task<IReadOnlyList<LeafEntity>> QueryAsync(
            EntityQuery query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LeafEntity>>(entities
                .Where(entity => entity.TypeSlug == query.TypeSlug)
                .Skip(query.Offset)
                .Take(query.Limit)
                .ToList());

        public Task<LeafEntity> CreateAsync(
            string typeSlug, string name, JsonObject? data = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<LeafEntity> UpsertBySlugAsync(
            string typeSlug, string slug, string name, JsonObject? data = null,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<LeafEntity> PatchAsync(
            Guid id, JsonObject dataPatch, string? name = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<LeafEntity> ReplaceDataAsync(
            Guid id, JsonObject data, string? name = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task DeleteAsync(Guid id, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class NoOpPluginEvents : IPluginEvents
    {
        public Task PublishAsync(
            string eventType, JsonObject payload, CancellationToken ct = default)
            => Task.CompletedTask;

        public IDisposable Subscribe(string eventType, Func<PluginEvent, Task> handler)
            => new NoOpDisposable();
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class NoWorkspacePathResolver : IAgentWorkspacePathResolver
    {
        public Task<string?> ResolveAsync(Guid agentId, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class FixedScratchSpace(string path) : IAgentScratchSpace
    {
        public AgentScratchAllocation PrepareExecution(string owner, string executionKey)
            => new(path, new Dictionary<string, string>());
    }

    private sealed class RecordingComputeGateway : IComputeGateway
    {
        public string? SessionCreateBody { get; private set; }

        public async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            ComputeProvenance? provenance = null,
            CancellationToken ct = default)
        {
            var path = request.RequestUri?.OriginalString ?? "";
            if (path == "/ai-session/sessions")
            {
                SessionCreateBody = await request.Content!.ReadAsStringAsync(ct);
                return Response("""{"id":"session-1"}""");
            }

            return Response("{}");
        }

        private static HttpResponseMessage Response(string content)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(content),
            };
    }
}
