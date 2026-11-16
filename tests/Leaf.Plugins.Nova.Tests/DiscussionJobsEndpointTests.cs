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
/// <c>/discussions/{id}/jobs</c> feeds the chat's job cards. Authorized like history-page, then
/// every job is checked against the discussion itself, because the gateway trusts Nova: its own
/// provenance, its own session, its owner, confidentiality. Lists are always bounded below, a job
/// is re-read by id, and outputs never leave through a RedCompute URL.
/// </summary>
public sealed class DiscussionJobsEndpointTests
{
    private const string Owner = "user-owner";
    private const string Agent = "agent-nova";
    private const string DiscussionId = "abcd1234";
    private const string SessionId = "session-1";
    private static readonly DateTimeOffset Created = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid Image = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Delegate = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Speech = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Secret = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid Stranger = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid Quick = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid Elsewhere = Guid.Parse("77777777-7777-7777-7777-777777777777");

    private static JsonObject Job(Guid id, string capability = "image-gen", string status = "Running",
        string? session = SessionId, string discussion = DiscussionId, string user = Owner,
        bool confidential = false, long? durationMs = null, bool detail = false)
    {
        var context = new JsonArray { new JsonObject { ["kind"] = "discussion", ["id"] = discussion } };
        if (session is not null) context.Add(new JsonObject { ["kind"] = "session", ["id"] = session });
        var job = new JsonObject
        {
            ["id"] = id.ToString(),
            ["capability"] = capability,
            ["providerName"] = "ComfyUI",
            ["status"] = status,
            ["queuedAt"] = "2026-11-16T11:00:00Z",
            ["name"] = $"Job {id.ToString()[..4]}",
            ["userId"] = user,
            ["confidential"] = confidential,
            ["durationMs"] = durationMs,
            ["creationProvenance"] = new JsonObject { ["context"] = context },
        };
        if (detail)
        {
            job["progress"] = 0.62;
            job["stage"] = "executing";
            job["terminal"] = status is "Completed" or "Failed" or "Cancelled";
            job["input"] = new JsonObject { ["prompt"] = "a very long prompt the card never needs" };
            job["outputLocation"] = "C:\\outputs\\secret.png";
            job["outputs"] = new JsonArray
            {
                new JsonObject { ["url"] = $"/image-gen/jobs/{id:D}/output", ["contentType"] = "image/png", ["sizeBytes"] = 42 },
            };
        }
        return job;
    }

    [Fact]
    public async Task TheListIsAlwaysBoundedAndClampedToTheDiscussionAndThirtyDays()
    {
        var fixture = new Fixture();
        fixture.Add(owner: Owner);

        await fixture.ListAsync(Human(Owner), "?since=2026-01-01T00:00:00Z");
        var path = Assert.Single(fixture.Gateway.Paths);
        // Created 2026-11-10 09:00, minus one minute of slack; well within 30 days of now.
        Assert.Equal("GET /jobs?contextKind=discussion&contextId=abcd1234&since=2026-11-10T08%3A59%3A00.0000000%2B00%3A00&limit=200&offset=0", path);

        var old = new Fixture(created: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        old.Add(owner: Owner);
        await old.ListAsync(Human(Owner), "?since=2026-01-01T00:00:00Z&until=2026-11-16T11:00:00Z");
        Assert.Equal("GET /jobs?contextKind=discussion&contextId=abcd1234&since=2026-10-17T12%3A00%3A00.0000000%2B00%3A00&until=2026-11-16T11%3A00%3A00.0000000%2B00%3A00&limit=200&offset=0",
            Assert.Single(old.Gateway.Paths));
    }

    [Theory]
    [InlineData("", "invalid_since")]
    [InlineData("?since=yesterday", "invalid_since")]
    [InlineData("?since=2026-11-16T10:00:00Z&until=soon", "invalid_until")]
    [InlineData("?since=2026-11-16T10:00:00Z&until=2026-11-16T09:00:00Z", "invalid_range")]
    public async Task AnUnboundedOrReversedListIsRefusedWithoutCallingRedCompute(string query, string code)
    {
        var fixture = new Fixture();
        fixture.Add(owner: Owner);

        var (status, body) = await fixture.ListAsync(Human(Owner), query);

        Assert.Equal(400, status);
        Assert.Equal(code, JsonNode.Parse(body)!["error"]!.GetValue<string>());
        Assert.Empty(fixture.Gateway.Paths);
    }

    [Fact]
    public async Task AWindowThatEndsBeforeTheDiscussionExistedNeedsNoRead()
    {
        var fixture = new Fixture();
        fixture.Add(owner: Owner);

        var (status, body) = await fixture.ListAsync(Human(Owner), "?since=2026-10-01T00:00:00Z&until=2026-11-01T00:00:00Z");

        Assert.Equal(200, status);
        Assert.Empty(JsonNode.Parse(body)!["items"]!.AsArray());
        Assert.Empty(fixture.Gateway.Paths);
    }

    [Fact]
    public async Task OnlyThisDiscussionsOwnJobsComeBackProjectedWithOutputsBehindNova()
    {
        var fixture = new Fixture();
        fixture.Add(owner: Owner);
        fixture.Gateway.List(
            Job(Image),
            Job(Delegate, session: "worker-session"),
            Job(Speech, capability: "tts"),
            Job(Secret, confidential: true),
            Job(Stranger, user: "someone-else"),
            Job(Elsewhere, discussion: "other999"),
            Job(Quick, status: "Completed", durationMs: 800));
        fixture.Gateway.Detail(Job(Image, detail: true));

        var (status, body) = await fixture.ListAsync(Human(Owner), "?since=2026-11-16T10:00:00Z");

        Assert.Equal(200, status);
        var items = JsonNode.Parse(body)!["items"]!.AsArray();
        Assert.Equal([Image.ToString(), Quick.ToString()], items.Select(item => item!["id"]!.GetValue<string>()));

        var image = items[0]!.AsObject();
        Assert.Equal(0.62, image["progress"]!.GetValue<double>());
        Assert.Equal("executing", image["stage"]!.GetValue<string>());
        Assert.Null(image["input"]);
        Assert.Null(image["outputLocation"]);
        Assert.Null(image["creationProvenance"]);
        Assert.Null(image["userId"]);
        Assert.Equal($"/api/apps/nova/discussions/abcd1234/jobs/{Image:D}/output?index=0",
            image["outputs"]![0]!["url"]!.GetValue<string>());

        // One bounded list, one detail for the job that will get a card, none for a quick success.
        Assert.Equal(2, fixture.Gateway.Paths.Count);
        Assert.Contains($"GET /jobs/{Image:D}", fixture.Gateway.Paths);
    }

    [Fact]
    public async Task AConfidentialDiscussionKeepsItsConfidentialJobs()
    {
        var fixture = new Fixture();
        fixture.Add(owner: Owner, confidential: true);
        fixture.Gateway.List(Job(Secret, confidential: true));
        fixture.Gateway.Detail(Job(Secret, confidential: true, detail: true));

        var (_, body) = await fixture.ListAsync(Human(Owner), "?since=2026-11-16T10:00:00Z");

        Assert.Equal(Secret.ToString(), Assert.Single(JsonNode.Parse(body)!["items"]!.AsArray())!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task ADiscussionWithoutASessionClaimsNoSessionsJobs()
    {
        var fixture = new Fixture();
        fixture.Add(owner: Owner, sessionId: null);
        fixture.Gateway.List(Job(Image));

        var (_, body) = await fixture.ListAsync(Human(Owner), "?since=2026-11-16T10:00:00Z");

        Assert.Empty(JsonNode.Parse(body)!["items"]!.AsArray());
    }

    [Fact]
    public async Task LiveShowsTheJobsItsOwnTurnsStartedAndOnlyThose()
    {
        var fixture = new Fixture();
        fixture.Add(owner: Owner, type: "live");
        fixture.Gateway.List(Job(Image), Job(Elsewhere, discussion: "other999"), Job(Delegate, session: "worker-session"));
        fixture.Gateway.Detail(Job(Image, detail: true));

        var (status, body) = await fixture.ListAsync(Human(Owner), "?since=2026-11-16T10:00:00Z");

        Assert.Equal(200, status);
        Assert.Equal([Image.ToString()], JsonNode.Parse(body)!["items"]!.AsArray().Select(item => item!["id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task SomeoneElsesDiscussionIsRefusedBeforeAnyRead()
    {
        var fixture = new Fixture();
        fixture.Add(owner: Owner);
        var secret = new Fixture();
        secret.Add(owner: Owner, confidential: true);

        var (status, _) = await fixture.ListAsync(Human("intruder"), "?since=2026-11-16T10:00:00Z");
        var (hidden, _) = await secret.ListAsync(Human("intruder"), "?since=2026-11-16T10:00:00Z");

        Assert.Equal(403, status);
        Assert.Equal(404, hidden);
        Assert.Empty(fixture.Gateway.Paths);
        Assert.Empty(secret.Gateway.Paths);
    }

    [Fact]
    public async Task ADetailThatNoLongerBelongsIsDroppedAndAFailedDetailKeepsTheSummary()
    {
        var fixture = new Fixture();
        fixture.Add(owner: Owner);
        var other = Guid.Parse("88888888-8888-8888-8888-888888888888");
        fixture.Gateway.List(Job(Image), Job(other));
        fixture.Gateway.Detail(Job(Image, detail: true, session: "worker-session"));
        fixture.Gateway.FailDetail(other, 500);

        var (_, body) = await fixture.ListAsync(Human(Owner), "?since=2026-11-16T10:00:00Z");

        var item = Assert.Single(JsonNode.Parse(body)!["items"]!.AsArray())!;
        Assert.Equal(other.ToString(), item["id"]!.GetValue<string>());
        Assert.Null(item["progress"]);
    }

    [Fact]
    public async Task AFullPageReadsTheNextOneAndStopsAtTheCap()
    {
        var fixture = new Fixture();
        fixture.Add(owner: Owner);
        fixture.Gateway.List(Enumerable.Range(0, DiscussionJobEndpoints.PageSize)
            .Select(_ => Job(Guid.NewGuid(), status: "Completed", durationMs: 100)).ToArray());

        await fixture.ListAsync(Human(Owner), "?since=2026-11-16T10:00:00Z");

        Assert.Equal(DiscussionJobEndpoints.MaxPages, fixture.Gateway.Paths.Count(path => path.StartsWith("GET /jobs?")));
        Assert.EndsWith("offset=400", fixture.Gateway.Paths.Last(path => path.StartsWith("GET /jobs?")));
    }

    [Fact]
    public async Task UnreachableRedComputeIsUnavailableNeverAnEmptyList()
    {
        var fixture = new Fixture();
        fixture.Add(owner: Owner);
        fixture.Gateway.Throw = new HttpRequestException("connection refused");

        var (status, body) = await fixture.ListAsync(Human(Owner), "?since=2026-11-16T10:00:00Z");

        Assert.Equal(503, status);
        Assert.Equal("redcompute_unavailable", JsonNode.Parse(body)!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task OneJobIsReReadByIdAndOnlyWhenItIsThisDiscussions()
    {
        var fixture = new Fixture();
        fixture.Add(owner: Owner);
        fixture.Gateway.Detail(Job(Image, detail: true));
        fixture.Gateway.Detail(Job(Delegate, detail: true, session: "worker-session"));

        var (status, body) = await fixture.GetAsync(Image, Human(Owner));
        Assert.Equal(200, status);
        Assert.Equal(0.62, JsonNode.Parse(body)!["progress"]!.GetValue<double>());
        Assert.Equal([$"GET /jobs/{Image:D}"], fixture.Gateway.Paths);

        var (foreign, foreignBody) = await fixture.GetAsync(Delegate, Human(Owner));
        Assert.Equal(404, foreign);
        Assert.Equal("job_not_found", JsonNode.Parse(foreignBody)!["error"]!.GetValue<string>());

        var (gone, _) = await fixture.GetAsync(Guid.NewGuid(), Human(Owner));
        Assert.Equal(404, gone);
    }

    [Fact]
    public async Task AnOutputStreamsThroughTheDiscussionAndOnlyFromTheJobsOwnRoute()
    {
        var fixture = new Fixture();
        fixture.Add(owner: Owner);
        fixture.Gateway.Detail(Job(Image, detail: true));

        var (status, body) = await fixture.OutputAsync(Image, Human(Owner));
        Assert.Equal(200, status);
        Assert.Equal("png-bytes", body);
        Assert.Contains($"GET /image-gen/jobs/{Image:D}/output", fixture.Gateway.Paths);

        var (missing, _) = await fixture.OutputAsync(Image, Human(Owner), "?index=3");
        Assert.Equal(404, missing);

        var hostile = Job(Delegate, detail: true);
        hostile["outputs"] = new JsonArray { new JsonObject { ["url"] = "/admin/secrets" } };
        fixture.Gateway.Detail(hostile);
        var (refused, refusedBody) = await fixture.OutputAsync(Delegate, Human(Owner));
        Assert.Equal(502, refused);
        Assert.Equal("invalid_job_output", JsonNode.Parse(refusedBody)!["error"]!.GetValue<string>());
        Assert.DoesNotContain("GET /admin/secrets", fixture.Gateway.Paths);
    }

    [Fact]
    public async Task StopIsAPersonsActionOnARunningJobOfThisDiscussion()
    {
        var fixture = new Fixture();
        fixture.Add(owner: Owner);
        fixture.Gateway.Detail(Job(Image, detail: true));
        fixture.Gateway.Detail(Job(Quick, status: "Completed", detail: true));

        var (agent, _) = await fixture.CancelAsync(Image, AgentExecution(Agent, Owner));
        Assert.Equal(403, agent);
        Assert.DoesNotContain($"DELETE /jobs/{Image:D}", fixture.Gateway.Paths);

        var (finished, finishedBody) = await fixture.CancelAsync(Quick, Browser(Owner));
        Assert.Equal(409, finished);
        Assert.Equal("job_finished", JsonNode.Parse(finishedBody)!["error"]!.GetValue<string>());

        var (status, _) = await fixture.CancelAsync(Image, Browser(Owner));
        Assert.Equal(200, status);
        Assert.Contains($"DELETE /jobs/{Image:D}", fixture.Gateway.Paths);
    }

    private static DefaultHttpContext Human(string userId)
        => Context(new ClaimsIdentity([new Claim("sub", userId)], "Bearer"));

    private static DefaultHttpContext AgentExecution(string agentId, string beneficiaryId)
        => Signed(beneficiaryId, new
        {
            app = new { id = "nova", name = "Nova" },
            actor = new { kind = "agent", id = "nova", entityId = agentId },
            beneficiary = new { kind = "user", id = beneficiaryId },
            context = Array.Empty<object>(),
        });

    /// <summary>What Nova's own page holds: the app acting for the person, in a browser.</summary>
    private static DefaultHttpContext Browser(string userId)
        => Signed(userId, new
        {
            app = new { id = "nova", name = "Nova" },
            actor = new { kind = "app", id = "nova", name = "Nova" },
            beneficiary = new { kind = "user", id = userId },
            context = new[] { new { kind = "browser", route = "/apps/nova" } },
        });

    private static DefaultHttpContext Signed(string subject, object identity)
        => Context(new ClaimsIdentity([
            new Claim("sub", subject),
            new Claim("token_use", "execution"),
            new Claim("execution_identity", JsonSerializer.Serialize(identity)),
        ], "Bearer"));

    private static DefaultHttpContext Context(ClaimsIdentity identity)
        => new() { User = new ClaimsPrincipal(identity) };

    private sealed class Fixture
    {
        private readonly DateTimeOffset _created;
        public MemoryEntities Entities { get; } = new();
        public DiscussionStore Store { get; }
        public RecordingGateway Gateway { get; } = new();

        public Fixture(DateTimeOffset? created = null)
        {
            _created = created ?? Created;
            Store = new DiscussionStore(Entities, new NoDiscussions());
        }

        public void Add(string owner, bool confidential = false, string? sessionId = SessionId, string type = "chat")
        {
            var data = new JsonObject
            {
                ["discussion_id"] = DiscussionId,
                ["app"] = "nova",
                ["type"] = type,
                ["status"] = "idle",
                ["title"] = "Jobs",
                ["owner_id"] = owner,
                ["agent"] = Agent,
                ["confidential"] = confidential,
            };
            if (sessionId is not null) data["session_id"] = sessionId;
            Entities.Items.Add(new LeafEntity(
                Guid.NewGuid(), "discussion", DiscussionStore.Slug(DiscussionId), "Jobs", data,
                _created, _created, "test"));
        }

        public Task<(int, string)> ListAsync(HttpContext ctx, string query)
            => RunAsync(ctx, query, client => DiscussionJobEndpoints.ListAsync(DiscussionId, ctx, Store, client, Now));

        public Task<(int, string)> GetAsync(Guid jobId, HttpContext ctx)
            => RunAsync(ctx, "", client => DiscussionJobEndpoints.GetAsync(DiscussionId, jobId, ctx, Store, client));

        public Task<(int, string)> OutputAsync(Guid jobId, HttpContext ctx, string query = "")
            => RunAsync(ctx, query, client => DiscussionJobEndpoints.OutputAsync(DiscussionId, jobId, ctx, Store, client));

        public Task<(int, string)> CancelAsync(Guid jobId, HttpContext ctx)
            => RunAsync(ctx, "", client => DiscussionJobEndpoints.CancelAsync(DiscussionId, jobId, ctx, Store, client));

        private async Task<(int, string)> RunAsync(HttpContext ctx, string query, Func<RedComputeClient, Task<IResult>> call)
        {
            ctx.Request.QueryString = new QueryString(query);
            ctx.RequestServices = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
            using var body = new MemoryStream();
            ctx.Response.Body = body;

            var result = await call(new RedComputeClient(Gateway));
            await result.ExecuteAsync(ctx);

            return (ctx.Response.StatusCode, System.Text.Encoding.UTF8.GetString(body.ToArray()));
        }
    }

    private sealed class RecordingGateway : IComputeGateway
    {
        private JsonObject[] _list = [];
        private readonly Dictionary<Guid, JsonObject> _details = [];
        private readonly Dictionary<Guid, int> _failures = [];
        public List<string> Paths { get; } = [];
        public Exception? Throw { get; set; }

        public void List(params JsonObject[] jobs) => _list = jobs;
        public void Detail(JsonObject job) => _details[Guid.Parse(job["id"]!.GetValue<string>())] = job;
        public void FailDetail(Guid id, int status) => _failures[id] = status;

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            ComputeProvenance? provenance = null, CancellationToken ct = default)
        {
            var path = request.RequestUri!.OriginalString;
            lock (Paths) Paths.Add($"{request.Method} {path}");
            if (Throw is not null) throw Throw;

            if (path.StartsWith("/jobs?", StringComparison.Ordinal))
                return Json(200, new JsonObject { ["items"] = new JsonArray(_list.Select(job => job.DeepClone()).ToArray()) });
            if (path.EndsWith("/output", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("png-bytes", System.Text.Encoding.UTF8, "image/png"),
                });
            if (path.StartsWith("/jobs/", StringComparison.Ordinal) && Guid.TryParse(path["/jobs/".Length..], out var id))
            {
                if (request.Method == HttpMethod.Delete) return Json(200, new JsonObject { ["id"] = id.ToString() });
                if (_failures.TryGetValue(id, out var failure)) return Json(failure, new JsonObject { ["error"] = "boom" });
                return _details.TryGetValue(id, out var job)
                    ? Json(200, job.DeepClone())
                    : Json(404, new JsonObject { ["error"] = "not_found" });
            }
            return Json(404, new JsonObject { ["error"] = "unexpected" });
        }

        private static Task<HttpResponseMessage> Json(int status, JsonNode body)
            => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
            });
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
