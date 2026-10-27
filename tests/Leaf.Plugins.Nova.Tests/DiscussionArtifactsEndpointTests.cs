using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Plugins.Nova.Endpoints;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

/// <summary>
/// <c>GET /discussions/{id}/artifacts</c> is a thin authorized proxy of RedCompute's derived
/// artifact list: same access as history-page, an empty page before a session exists, assistant
/// image targets moved to the discussion's output proxy, and every refusal kept as a refusal.
/// </summary>
public sealed class DiscussionArtifactsEndpointTests
{
    private const string Owner = "user-owner";
    private const string Agent = "agent-nova";
    private const string DiscussionId = "abcd1234";
    private const string SessionId = "session-1";

    private const string PageJson = """
        {"sessionId":"session-1","epoch":"e1","throughSequence":9,"extractorVersion":1,"items":[
          {"key":"payload:7","kind":"image","origin":"assistant-image","role":"assistant",
           "target":"/ai-session/sessions/session-1/messages/7/output","payloadRef":{"recordId":7,"kind":"assistant-image"},
           "first":{"messageUid":"u1","recordId":7,"sequence":7,"timestamp":"2026-10-27T10:00:00Z"},
           "last":{"messageUid":"u1","recordId":7,"sequence":7,"timestamp":"2026-10-27T10:00:00Z"},"occurrences":1},
          {"key":"asset:a.png","kind":"image","origin":"assistant-text","role":"assistant","target":"/api/assets/a.png",
           "first":{"messageUid":"u2","recordId":8,"sequence":8,"timestamp":"2026-10-27T10:01:00Z"},
           "last":{"messageUid":"u2","recordId":8,"sequence":8,"timestamp":"2026-10-27T10:01:00Z"},"occurrences":1},
          {"key":"payload:other","kind":"image","origin":"assistant-image","role":"assistant",
           "target":"/ai-session/sessions/another-session/messages/3/output",
           "first":{"messageUid":"u0","recordId":3,"sequence":3,"timestamp":"2026-10-27T09:00:00Z"},
           "last":{"messageUid":"u0","recordId":3,"sequence":3,"timestamp":"2026-10-27T09:00:00Z"},"occurrences":1},
          {"key":"attachment:x","kind":"document","origin":"user-attachment","role":"user","target":"/ai-session/input-attachments/x/report.pdf",
           "first":{"messageUid":"u3","recordId":9,"sequence":9,"timestamp":"2026-10-27T10:02:00Z"},
           "last":{"messageUid":"u3","recordId":9,"sequence":9,"timestamp":"2026-10-27T10:02:00Z"},"occurrences":1}
        ],"page":{"nextCursor":"next"}}
        """;

    [Fact]
    public async Task UnknownDiscussionIsNotFoundAndNeverReachesRedCompute()
    {
        var fixture = new Fixture();

        var (status, _) = await fixture.GetAsync("missing", Human(Owner));

        Assert.Equal(404, status);
        Assert.Empty(fixture.Gateway.Paths);
    }

    [Fact]
    public async Task AccessMatchesHistoryPageForOrdinaryDiscussions()
    {
        var fixture = new Fixture();
        fixture.Add(owner: Owner);

        Assert.Equal(200, (await fixture.GetAsync(DiscussionId, Human(Owner))).Status);
        var denied = await fixture.GetAsync(DiscussionId, Human("someone-else"));

        Assert.Equal(403, denied.Status);
        Assert.Single(fixture.Gateway.Paths);
    }

    [Fact]
    public async Task ConfidentialDiscussionHidesItselfFromEveryoneButItsOwnerAndOwningAgent()
    {
        var fixture = new Fixture();
        fixture.Add(owner: Owner, confidential: true);

        Assert.Equal(404, (await fixture.GetAsync(DiscussionId, Human("someone-else"))).Status);
        Assert.Equal(404, (await fixture.GetAsync(DiscussionId, LocalDefault())).Status);
        Assert.Empty(fixture.Gateway.Paths);

        Assert.Equal(200, (await fixture.GetAsync(DiscussionId, Human(Owner))).Status);
        Assert.Equal(200, (await fixture.GetAsync(DiscussionId, AgentExecution(Agent, Owner))).Status);
        Assert.Equal(404, (await fixture.GetAsync(DiscussionId, AgentExecution("another-agent", Owner))).Status);
        Assert.Equal(2, fixture.Gateway.Paths.Count);
    }

    [Fact]
    public async Task DiscussionWithoutASessionReturnsAnEmptyPageWithoutCallingRedCompute()
    {
        var fixture = new Fixture();
        fixture.Add(owner: Owner, sessionId: null);

        var (status, body) = await fixture.GetAsync(DiscussionId, Human(Owner));

        Assert.Equal(200, status);
        var page = JsonNode.Parse(body)!.AsObject();
        Assert.Null(page["sessionId"]);
        Assert.Null(page["epoch"]);
        Assert.Equal(0, page["throughSequence"]!.GetValue<long>());
        Assert.Null(page["extractorVersion"]);
        Assert.Empty(page["items"]!.AsArray());
        Assert.Null(page["page"]!["nextCursor"]);
        Assert.Empty(fixture.Gateway.Paths);
    }

    [Fact]
    public async Task AssistantImageTargetsMoveToTheDiscussionOutputProxyAndEverythingElsePassesThrough()
    {
        var fixture = new Fixture();
        fixture.Add(owner: Owner);
        fixture.Gateway.Respond(200, PageJson);

        var (status, body) = await fixture.GetAsync(DiscussionId, Human(Owner));

        Assert.Equal(200, status);
        var page = JsonNode.Parse(body)!.AsObject();
        var items = page["items"]!.AsArray().Select(item => item!.AsObject()).ToList();
        Assert.Equal($"/api/apps/nova/discussions/{DiscussionId}/messages/7/output", items[0]["target"]!.GetValue<string>());
        Assert.Equal("/api/assets/a.png", items[1]["target"]!.GetValue<string>());
        // A target that names another session is not this discussion's to proxy.
        Assert.Equal("/ai-session/sessions/another-session/messages/3/output", items[2]["target"]!.GetValue<string>());
        Assert.Equal("/ai-session/input-attachments/x/report.pdf", items[3]["target"]!.GetValue<string>());
        // Nothing but target changes.
        var original = JsonNode.Parse(PageJson)!.AsObject();
        original["items"]![0]!["target"] = items[0]["target"]!.GetValue<string>();
        Assert.True(JsonNode.DeepEquals(original, page));
    }

    [Fact]
    public void RewriteKeepsTheQueryOfAPayloadTarget()
    {
        var rewritten = DiscussionEndpoints.RewriteArtifactTargets(
            """{"items":[{"target":"/ai-session/sessions/s%2F1/messages/12/output?download=true"}]}""",
            "d1", "s/1");

        Assert.Equal(
            "/api/apps/nova/discussions/d1/messages/12/output?download=true",
            JsonNode.Parse(rewritten!)!["items"]![0]!["target"]!.GetValue<string>());
    }

    [Fact]
    public async Task FiltersPagingAndWatermarkReachRedComputeUnchanged()
    {
        var fixture = new Fixture();
        fixture.Add(owner: Owner);

        await fixture.GetAsync(
            DiscussionId, Human(Owner),
            "?kinds=image,video&origins=assistant-image&sinceSequence=41&limit=25&cursor=a%2Bb%3D");

        var path = Assert.Single(fixture.Gateway.Paths);
        Assert.Equal(
            "/ai-session/sessions/session-1/artifacts?kinds=image%2Cvideo&origins=assistant-image&sinceSequence=41&limit=25&cursor=a%2Bb%3D",
            path);
    }

    [Fact]
    public async Task NoFilterSendsNoQueryString()
    {
        var fixture = new Fixture();
        fixture.Add(owner: Owner);

        await fixture.GetAsync(DiscussionId, Human(Owner));

        Assert.Equal("/ai-session/sessions/session-1/artifacts", Assert.Single(fixture.Gateway.Paths));
    }

    [Theory]
    [InlineData(400, """{"error":"invalid_filter","message":"Unknown kind 'x'"}""", "invalid_filter")]
    [InlineData(403, """{"error":"forbidden","message":"no"}""", "forbidden")]
    [InlineData(404, """{"error":"not_found","message":"Session 'session-1' not found"}""", "not_found")]
    [InlineData(409, """{"error":"transcript_cursor_stale","message":"old epoch"}""", "transcript_cursor_stale")]
    [InlineData(413, """{"error":"transcript_message_too_large","message":"big"}""", "transcript_message_too_large")]
    [InlineData(503, """{"error":"redleaf_unavailable","message":"down"}""", "redleaf_unavailable")]
    public async Task RedComputeRefusalsKeepTheirStatusAndCode(int upstream, string content, string code)
    {
        var fixture = new Fixture();
        fixture.Add(owner: Owner);
        fixture.Gateway.Respond(upstream, content);

        var (status, body) = await fixture.GetAsync(DiscussionId, Human(Owner));

        Assert.Equal(upstream, status);
        Assert.Equal(code, JsonNode.Parse(body)!["error"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Not Found")]
    [InlineData("""{"title":"Not Found","status":404}""")]
    public async Task ARedComputeWithoutTheRouteIsUnavailableNotAnEmptyPanel(string content)
    {
        var fixture = new Fixture();
        fixture.Add(owner: Owner);
        fixture.Gateway.Respond(404, content);

        var (status, body) = await fixture.GetAsync(DiscussionId, Human(Owner));

        Assert.Equal(503, status);
        Assert.Equal("artifacts_unavailable", JsonNode.Parse(body)!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task UnexpectedUpstreamStatusIsABadGatewayNeverAnEmptyList()
    {
        var fixture = new Fixture();
        fixture.Add(owner: Owner);
        fixture.Gateway.Respond(500, "boom");

        var (status, body) = await fixture.GetAsync(DiscussionId, Human(Owner));

        Assert.Equal(502, status);
        Assert.Equal("redcompute_error", JsonNode.Parse(body)!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task UnreachableRedComputeIsServiceUnavailable()
    {
        var fixture = new Fixture();
        fixture.Add(owner: Owner);
        fixture.Gateway.Throw = new HttpRequestException("connection refused");

        var (status, body) = await fixture.GetAsync(DiscussionId, Human(Owner));

        Assert.Equal(503, status);
        Assert.Equal("redcompute_unavailable", JsonNode.Parse(body)!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task MalformedPageIsABadGateway()
    {
        var fixture = new Fixture();
        fixture.Add(owner: Owner);
        fixture.Gateway.Respond(200, """{"items":"nope"}""");

        var (status, body) = await fixture.GetAsync(DiscussionId, Human(Owner));

        Assert.Equal(502, status);
        Assert.Equal("invalid_artifacts_page", JsonNode.Parse(body)!["error"]!.GetValue<string>());
    }

    private static DefaultHttpContext Human(string userId)
        => Context(new ClaimsIdentity([new Claim("sub", userId)], "Bearer"));

    private static DefaultHttpContext LocalDefault()
        => Context(new ClaimsIdentity([new Claim("sub", "local-user")], "LocalDefault"));

    private static DefaultHttpContext AgentExecution(string agentId, string beneficiaryId)
    {
        var identity = JsonSerializer.Serialize(new
        {
            app = new { id = "nova", name = "Nova" },
            actor = new { kind = "agent", id = "nova", entityId = agentId },
            beneficiary = new { kind = "user", id = beneficiaryId },
            context = Array.Empty<object>(),
        });
        return Context(new ClaimsIdentity([
            new Claim("sub", beneficiaryId),
            new Claim("token_use", "execution"),
            new Claim("execution_identity", identity),
        ], "Bearer"));
    }

    private static DefaultHttpContext Context(ClaimsIdentity identity)
        => new() { User = new ClaimsPrincipal(identity) };

    private sealed class Fixture
    {
        public MemoryEntities Entities { get; } = new();
        public DiscussionStore Store { get; }
        public RecordingGateway Gateway { get; } = new();

        public Fixture() => Store = new DiscussionStore(Entities, new NoDiscussions());

        public void Add(string owner, bool confidential = false, string? sessionId = SessionId)
        {
            var data = new JsonObject
            {
                ["discussion_id"] = DiscussionId,
                ["app"] = "nova",
                ["type"] = "chat",
                ["status"] = "idle",
                ["title"] = "Artifacts",
                ["owner_id"] = owner,
                ["agent"] = Agent,
                ["confidential"] = confidential,
            };
            if (sessionId is not null) data["session_id"] = sessionId;
            Entities.Items.Add(new LeafEntity(
                Guid.NewGuid(), "discussion", DiscussionStore.Slug(DiscussionId), "Artifacts", data,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "test"));
        }

        public async Task<(int Status, string Body)> GetAsync(string id, HttpContext ctx, string query = "")
        {
            ctx.Request.QueryString = new QueryString(query);
            ctx.RequestServices = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
            using var body = new MemoryStream();
            ctx.Response.Body = body;

            var result = await DiscussionEndpoints.GetArtifactsAsync(id, ctx, Store, new RedComputeClient(Gateway));
            await result.ExecuteAsync(ctx);

            return (ctx.Response.StatusCode, System.Text.Encoding.UTF8.GetString(body.ToArray()));
        }
    }

    private sealed class RecordingGateway : IComputeGateway
    {
        private int _status = 200;
        private string _content = PageJson;
        public List<string> Paths { get; } = [];
        public Exception? Throw { get; set; }

        public void Respond(int status, string content) => (_status, _content) = (status, content);

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            ComputeProvenance? provenance = null, CancellationToken ct = default)
        {
            Paths.Add(request.RequestUri!.OriginalString);
            if (Throw is not null) throw Throw;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)_status)
            {
                Content = new StringContent(_content, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class MemoryEntities : IEntityStore
    {
        public List<LeafEntity> Items { get; } = [];
        public Task<LeafEntity?> GetAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Items.SingleOrDefault(entity => entity.Id == id));
        public Task<LeafEntity?> GetBySlugAsync(string slug, CancellationToken ct = default) =>
            Task.FromResult(Items.SingleOrDefault(entity => entity.Slug == slug));
        public Task<LeafEntity?> GetBySlugAsync(string typeSlug, string slug, CancellationToken ct = default) =>
            Task.FromResult(Items.SingleOrDefault(entity => entity.TypeSlug == typeSlug && entity.Slug == slug));
        public Task<IReadOnlyList<LeafEntity>> QueryAsync(EntityQuery query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LeafEntity>>(Items.Where(entity => entity.TypeSlug == query.TypeSlug).ToList());
        public Task<LeafEntity> CreateAsync(string typeSlug, string name, JsonObject? data = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<LeafEntity> UpsertBySlugAsync(string typeSlug, string slug, string name, JsonObject? data = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<LeafEntity> PatchAsync(Guid id, JsonObject patch, string? name = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<LeafEntity> ReplaceDataAsync(Guid id, JsonObject data, string? name = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class NoDiscussions : IDiscussions
    {
        public Task<LeafEntity> CreateAsync(string? title, string? agentSlugOrId = null, JsonObject? data = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task PostAsync(Guid discussionId, string role, string content, JsonObject? metadata = null, string? userId = null, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<IReadOnlyList<DiscussionMessage>> GetMessagesAsync(Guid discussionId, int limit = 1000, long afterId = 0, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DiscussionMessage>>([]);
        public Task<IReadOnlyList<DiscussionMessage>> SearchMessagesAsync(string query, int limit = 1000, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DiscussionMessage>>([]);
        public Task ClearMessagesAsync(Guid discussionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetReactionAsync(Guid discussionId, ReactionChange change, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<LeafRecord>> GetReactionsAsync(Guid discussionId, DateTimeOffset? since = null, int limit = 1000, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LeafRecord>>([]);
        public IDisposable Subscribe(Guid discussionId, Func<DiscussionMessage, Task> onMessage) => new EmptyDisposable();
        private sealed class EmptyDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}
