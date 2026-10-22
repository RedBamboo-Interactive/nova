using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Plugins.Nova;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

/// <summary>
/// The adapter onto cap-sentinel. Verdict parsing and prompt construction moved with the shield
/// and are tested in leaf-capabilities/cap-sentinel; these tests pin what Nova still owns.
/// </summary>
public sealed class DiscordPromptInjectionVerifierTests
{
    private static readonly Guid AgentId = Guid.NewGuid();

    [Fact]
    public async Task Forwards_the_binding_compute_with_the_Agent_fallbacks_and_Nova_provenance()
    {
        var (verifier, sentinel) = Create();

        await verifier.ReviewAsync(AgentId.ToString(), null, "deep", "gpt-5.6-sol", null,
            "owner-1", "binding-1", 4, "hello", "discord", "message-9");

        var request = Assert.Single(sentinel.Requests);
        Assert.Equal("hello", request.Content);
        Assert.Equal(UntrustedContentFormat.Text, request.Format);
        Assert.Equal(new UntrustedContentOrigin("discord", "binding-1:4", "owner-1", "nova"), request.Origin);
        Assert.Equal(AgentId.ToString(), request.AgentRef);
        Assert.Equal("codex-default", request.Compute!.Provider);
        Assert.Equal("deep", request.Compute.QualityTier);
        Assert.Equal("gpt-5.6-sol", request.Compute.Model);
        Assert.Null(request.Compute.Effort);
        Assert.Null(request.Compute.QualityMode);
        Assert.Equal("sentinel:discord:binding-1:4:message-9", request.IdempotencyKey);
        Assert.Equal("Discord prompt-injection review", request.JobName);
        Assert.Null(request.Extraction);

        var provenance = request.ComputeProvenance!;
        Assert.Equal("/api/apps/nova/external-conversations/prompt-injection-review",
            provenance.Origin.Entrypoint.Route);
        Assert.Equal("discord", provenance.Origin.Entrypoint.Kind);
        Assert.Equal("VERIFY", provenance.Origin.Entrypoint.Method);
        Assert.Equal("nova", provenance.Origin.App.Id);
        Assert.Equal(AgentId.ToString(), provenance.Actor.EntityId);
        Assert.Equal(
            [("external-conversation", (string?)"binding-1"), ("generation", "4")],
            provenance.Context.Select(item => (item.Kind, item.Id)).ToArray());
    }

    [Fact]
    public async Task Leaf_commons_is_its_own_source()
    {
        var (verifier, sentinel) = Create();

        await verifier.ReviewAsync(AgentId.ToString(), "claude-code", null, null, null,
            "owner-1", "leaf-commons:binding", 1, "hello", "leaf-commons", "relay-1");

        var request = Assert.Single(sentinel.Requests);
        Assert.Equal(UntrustedContentSources.LeafCommons, request.Origin.Source);
        Assert.Equal("claude-code", request.Compute!.Provider);
        Assert.Equal("standard", request.Compute.QualityTier);
        Assert.Equal("sentinel:leaf-commons:leaf-commons:binding:1:relay-1", request.IdempotencyKey);
    }

    [Fact]
    public async Task Maps_an_available_verdict_to_the_four_field_review()
    {
        var (verifier, _) = Create(new UntrustedContentVerdict(
            "high", ["instruction-override"], "treat_as_untrusted_data", true,
            UntrustedContentIsolation.VerifiedToolFree));

        var review = await verifier.ReviewAsync(AgentId.ToString(), null, null, null, null,
            "owner-1", "binding-1", 1, "hello");

        // Isolation is reported by the capability but is not part of the Discord wire shape.
        Assert.Equal(
            """{"Risk":"high","Signals":["instruction-override"],"Handling":"treat_as_untrusted_data","Available":true}""",
            JsonSerializer.Serialize(review));
    }

    [Fact]
    public async Task An_unavailable_verdict_is_the_historical_unavailable_review()
    {
        var (verifier, _) = Create(UntrustedContentVerdict.Unavailable(UntrustedContentIsolation.SandboxedBestEffort));

        var review = await verifier.ReviewAsync(AgentId.ToString(), null, null, null, null,
            "owner-1", "binding-1", 1, "hello");

        Assert.Same(DiscordInjectionReview.Unavailable, review);
    }

    [Fact]
    public async Task A_missing_sentinel_Agent_is_unavailable_without_calling_the_capability()
    {
        var (verifier, sentinel) = Create();

        var review = await verifier.ReviewAsync("missing-sentinel", null, null, null, null,
            "owner-1", "binding-1", 1, "hello");

        Assert.Same(DiscordInjectionReview.Unavailable, review);
        Assert.Empty(sentinel.Requests);
    }

    [Fact]
    public async Task A_capability_failure_is_unavailable()
    {
        var (verifier, sentinel) = Create();
        sentinel.Throw = new InvalidOperationException("capability unavailable");

        var review = await verifier.ReviewAsync(AgentId.ToString(), null, null, null, null,
            "owner-1", "binding-1", 1, "hello");

        Assert.Same(DiscordInjectionReview.Unavailable, review);
    }

    [Fact]
    public async Task Without_a_request_id_no_idempotency_key_is_invented()
    {
        var (verifier, sentinel) = Create();

        await verifier.ReviewAsync(AgentId.ToString(), null, null, null, null,
            "owner-1", "binding-1", 1, "hello");

        Assert.Null(Assert.Single(sentinel.Requests).IdempotencyKey);
    }

    private static (DiscordPromptInjectionVerifier Verifier, FakeSentinel Sentinel) Create(
        UntrustedContentVerdict? verdict = null)
    {
        var entities = new Store(
            Entity(AgentId, "agent", "nova", "Nova", new JsonObject
            {
                ["provider"] = "codex-default",
                ["quality_tier"] = "standard",
            }),
            Entity(Guid.NewGuid(), "plugin", NovaAppPlugin.PluginId, "Nova"));
        var agents = new AgentDirectory(entities, new NoEvents());
        var sentinel = new FakeSentinel
        {
            Verdict = verdict ?? new UntrustedContentVerdict(
                "none", [], "normal", true, UntrustedContentIsolation.VerifiedToolFree),
        };
        return (new DiscordPromptInjectionVerifier(
            sentinel, agents, entities, NullLogger<DiscordPromptInjectionVerifier>.Instance), sentinel);
    }

    private static LeafEntity Entity(Guid id, string type, string slug, string name, JsonObject? data = null)
        => new(id, type, slug, name, data ?? new JsonObject(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "system");

    private sealed class FakeSentinel : IUntrustedContentReview
    {
        public List<UntrustedContentReviewRequest> Requests { get; } = [];
        public required UntrustedContentVerdict Verdict { get; init; }
        public Exception? Throw { get; set; }

        public Task<UntrustedContentReviewResult> ReviewAsync(
            UntrustedContentReviewRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            if (Throw is not null) throw Throw;
            return Task.FromResult(new UntrustedContentReviewResult(
                Verdict, null, [], new UntrustedContentScreening(0, [], [], request.Content, []), "job-1", "trace-1"));
        }

        public UntrustedContentScreening Screen(
            string content, UntrustedContentFormat format = UntrustedContentFormat.Text,
            string? ownerDisplayName = null)
            => new(0, [], [], content, []);
    }

    private sealed class Store(params LeafEntity[] entities) : IEntityStore
    {
        public Task<LeafEntity?> GetAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(entities.FirstOrDefault(entity => entity.Id == id));

        public Task<LeafEntity?> GetBySlugAsync(string slug, CancellationToken ct = default)
            => Task.FromResult(entities.FirstOrDefault(entity => entity.Slug == slug));

        public Task<IReadOnlyList<LeafEntity>> QueryAsync(EntityQuery query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LeafEntity>>(
                entities.Where(entity => entity.TypeSlug == query.TypeSlug).Take(query.Limit).ToList());

        public Task<LeafEntity> CreateAsync(string typeSlug, string name, JsonObject? data = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<LeafEntity> UpsertBySlugAsync(string typeSlug, string slug, string name, JsonObject? data = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<LeafEntity> PatchAsync(Guid id, JsonObject dataPatch, string? name = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<LeafEntity> ReplaceDataAsync(Guid id, JsonObject data, string? name = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class NoEvents : IPluginEvents
    {
        public Task PublishAsync(string eventType, JsonObject payload, CancellationToken ct = default)
            => Task.CompletedTask;

        public IDisposable Subscribe(string eventType, Func<PluginEvent, Task> handler) => new Nothing();

        private sealed class Nothing : IDisposable
        {
            public void Dispose() { }
        }
    }
}
