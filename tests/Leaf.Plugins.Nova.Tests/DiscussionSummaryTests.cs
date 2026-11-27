using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class DiscussionSummaryTests
{
    private static readonly SummaryText Waiting = new("waiting", "Lab release", "The test is ready.", "Nova runs it after handoff.", null);

    [Fact]
    public async Task New_owners_default_on_and_a_saved_owner_opt_out_survives_restart()
    {
        using var h = new Harness();
        Assert.True(await h.Summaries.DefaultAsync(h.Owner, default));
        Assert.True(await h.NewService().DefaultAsync(Guid.NewGuid().ToString(), default));
        await h.Summaries.SetDefaultAsync(h.Owner, false, default);

        var restarted = h.NewService();
        Assert.False(await restarted.DefaultAsync(h.Owner, default));
        Assert.False(DiscussionSummaries.View(await h.ReadAsync(), false).Enabled);
        await restarted.SweepAsync([await h.ReadAsync()], default);
        Assert.Equal(0, h.Compute.Requests);

        var overrideOn = await restarted.SetOverrideAsync(await h.ReadAsync(), h.Person(), true);
        Assert.True(overrideOn?.Enabled);
        var inherited = await restarted.SetOverrideAsync(await h.ReadAsync(), h.Person(), null);
        Assert.False(inherited?.Enabled);
    }

    [Fact]
    public async Task A_discussion_opt_out_wins_over_the_shipped_default()
    {
        using var h = new Harness();
        var overrideOff = await h.Summaries.SetOverrideAsync(await h.ReadAsync(), h.Person(), false);
        Assert.False(overrideOff?.Enabled);
        await h.SweepAsync();
        Assert.Equal(0, h.Compute.Requests);

        var inherited = await h.Summaries.SetOverrideAsync(await h.ReadAsync(), h.Person(), null);
        Assert.True(inherited?.Enabled);
        Assert.Null(inherited?.Override);
    }

    [Fact]
    public async Task One_signed_fast_result_after_the_quiet_window_survives_a_worker_restart()
    {
        using var h = new Harness();
        h.Clock.Now = h.Compute.EndedAt.AddSeconds(179);
        await h.SweepAsync();
        Assert.Equal(0, h.Compute.Generations);
        h.Clock.Now = h.Compute.EndedAt.AddMinutes(3);
        await h.SweepAsync();

        var saved = Assert.IsType<DiscussionSummary>((await h.ReadAsync()).AutoSummary);
        Assert.Equal("fast", saved.Source);
        Assert.Equal(Waiting, saved.Text);
        Assert.Equal(["final"], saved.EvidenceMessageUids!);
        Assert.Equal("epoch", saved.Epoch);
        Assert.Equal(42, saved.ThroughSequence);
        Assert.Equal("fast", h.Compute.RequestBody!["qualityTier"]!.GetValue<string>());
        Assert.True(h.Compute.RequestBody["requireToolFree"]!.GetValue<bool>());
        Assert.DoesNotContain("hidden-tool-output", h.Compute.RequestBody.ToJsonString());
        Assert.Empty(h.F.Records);
        Assert.Equal(0, (await h.ReadAsync()).ConversationRevision);
        Assert.Single(h.Events.Invalidations);
        Assert.Equal("summary", h.Events.Invalidations[0]["change"]!.GetValue<string>());

        await h.NewService().SweepAsync([await h.ReadAsync()], default);
        Assert.Equal(1, h.Compute.Generations);
        Assert.Single(h.Events.Invalidations);
    }

    [Theory]
    [InlineData("working", 0)]
    [InlineData("background", 0)]
    [InlineData("awaiting_answer", 0)]
    [InlineData("idle", 1)]
    public async Task Quiet_runtime_and_empty_queue_are_both_required(string work, int queued)
    {
        using var h = new Harness();
        await h.Summaries.SetDefaultAsync(h.Owner, true, default);
        h.Compute.Work = work;
        h.Compute.Depth = queued;
        await h.SweepAsync();
        Assert.Equal(0, h.Compute.Generations);
        Assert.Null((await h.ReadAsync()).AutoSummary);
    }

    [Theory]
    [InlineData("resumed")]
    [InlineData("new-evidence")]
    [InlineData("disabled")]
    [InlineData("confidential")]
    public async Task In_flight_result_is_discarded_when_its_basis_changes(string change)
    {
        using var h = new Harness();
        await h.Summaries.SetDefaultAsync(h.Owner, true, default);
        h.Compute.BeforeOutput = async () =>
        {
            switch (change)
            {
                case "resumed": h.Compute.Work = "working"; break;
                case "new-evidence": h.Compute.HighWater++; break;
                case "disabled": await h.Summaries.SetDefaultAsync(h.Owner, false, default); break;
                case "confidential": await h.F.Store.PatchAsync(h.F.Target.Id, new JsonObject { ["confidential"] = true }); break;
            }
        };
        await h.SweepAsync();
        Assert.Equal(1, h.Compute.Generations);
        Assert.Null((await h.ReadAsync()).AutoSummary);
        Assert.Empty(h.Events.Invalidations);
    }

    [Fact]
    public async Task Owning_agent_can_correct_and_a_late_generation_cannot_overwrite_it()
    {
        using var h = new Harness();
        var discussion = await h.ReadAsync();
        await h.Summaries.SetDefaultAsync(h.Owner, true, default);
        var ownerAgent = h.Agent(h.F.AgentId);
        Assert.True(DiscussionSummaries.CanAccess(discussion, ownerAgent));
        Assert.False(DiscussionSummaries.CanAccess(discussion, h.Agent(Guid.NewGuid())));
        Assert.False(DiscussionSummaries.CanAccess(discussion, h.Person(Guid.NewGuid().ToString())));
        var edit = Waiting with { Overview = "Storage sent the updated rollout window." };
        var oldVersion = DiscussionSummaries.Version(discussion);
        h.Compute.BeforeOutput = async () =>
        {
            var (code, view) = await h.Summaries.AuthorAsync(discussion, ownerAgent, oldVersion, edit);
            Assert.Equal(200, code);
            Assert.Equal("agent", view?.Summary?.Source);
        };
        await h.SweepAsync();
        Assert.Equal(edit, (await h.ReadAsync()).AutoSummary?.Text);
        Assert.Equal(409, (await h.Summaries.AuthorAsync(discussion, ownerAgent, oldVersion, Waiting)).Status);
        Assert.Single(h.Events.Invalidations);
        Assert.Empty(h.F.Records);
    }

    [Fact]
    public async Task A_bad_model_answer_cannot_charge_indefinitely_for_the_same_settle()
    {
        using var h = new Harness();
        await h.Summaries.SetDefaultAsync(h.Owner, true, default);
        h.Compute.InvalidOutput = true;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await h.SweepAsync();
            h.Clock.Now = h.Clock.Now.AddMinutes(3);
        }
        Assert.Equal(3, h.Compute.Generations);
        Assert.Null((await h.ReadAsync()).AutoSummary);
    }

    [Fact]
    public void Evidence_and_user_action_are_required_for_high_impact_labels()
    {
        var evidence = new[] { new DiscussionSummaries.Evidence("checked", "assistant", "The live test passed.") };
        Assert.False(DiscussionSummaries.TryParse(
            """{"status":"complete","label":"Done","overview":"Shipped","evidenceMessageUids":["invented"]}""", evidence, out _, out _));
        Assert.False(DiscussionSummaries.TryParse(
            """{"status":"needs_you","label":"Your turn","overview":"Waiting","evidenceMessageUids":["checked"]}""", evidence, out _, out _));
        Assert.True(DiscussionSummaries.TryParse(
            """{"status":"complete","label":"Done","overview":"Shipped","evidenceMessageUids":["checked"]}""", evidence, out _, out var accepted));
        Assert.Equal(["checked"], accepted);
    }

    private sealed class Harness : IDisposable
    {
        public CallbackDeliveryTests.Fixture F { get; }
        public string Owner { get; }
        public TestClock Clock { get; } = new();
        public Gateway Compute { get; }
        public RecordingEvents Events { get; } = new();
        public DiscussionSummaries Summaries { get; }
        private readonly Records preferences = new();

        public Harness()
        {
            var owner = CallbackDeliveryTests.Entity("user", "summary-owner", new());
            Owner = owner.Id.ToString();
            F = new(extraEntities: [owner]);
            F.Store.PatchAsync(F.Target.Id, new JsonObject
            {
                ["owner_id"] = Owner, ["status"] = "idle", ["last_activity"] = "2026-11-27T21:00:00Z",
            }).GetAwaiter().GetResult();
            Compute = new(Owner, F.AgentId.ToString());
            Summaries = NewService();
        }

        public DiscussionSummaries NewService() => new(F.Store, F.Entities, preferences, F.Agents,
            new RedComputeClient(Compute), Events, NullLogger<DiscussionSummaries>.Instance, Clock);

        public async Task<DiscussionRead> ReadAsync() => (await F.Store.GetAsync(F.Target.Id))!;
        public async Task SweepAsync() => await Summaries.SweepAsync([await ReadAsync()], default);
        public HttpContext Person(string? owner = null) => new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new("sub", owner ?? Owner)], "test-human")),
        };
        public HttpContext Agent(Guid id)
        {
            var identity = JsonSerializer.Serialize(new
            {
                app = new { id = "nova" }, actor = new { kind = "agent", id = id.ToString(), entityId = id.ToString() },
                beneficiary = new { kind = "user", id = Owner },
            });
            return new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new("sub", Owner), new("token_use", "execution"), new("execution_identity", identity)], "test-agent")),
            };
        }
        public void Dispose() => F.Dispose();
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-11-27T21:05:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Gateway(string owner, string agent) : IComputeGateway
    {
        public DateTimeOffset EndedAt { get; } = DateTimeOffset.Parse("2026-11-27T21:01:00Z");
        public string Work { get; set; } = "idle";
        public int Depth { get; set; }
        public long HighWater { get; set; } = 42;
        public int Requests { get; private set; }
        public int Generations { get; private set; }
        public JsonNode? RequestBody { get; private set; }
        public Func<Task>? BeforeOutput { get; set; }
        public bool InvalidOutput { get; set; }

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, ComputeProvenance? provenance = null,
            CancellationToken ct = default)
        {
            Requests++;
            Assert.NotNull(provenance);
            Assert.Equal(owner, provenance.OnBehalfOf.Id);
            Assert.Equal("startup", provenance.Origin.Entrypoint.Kind);
            var path = request.RequestUri!.OriginalString;
            if (path.EndsWith("?tail=1", StringComparison.Ordinal))
                return Json(new
                {
                    session = new
                    {
                        id = "target-session", workState = Work, status = "Idle", userId = owner, ownerAgentId = agent,
                        confidential = false, lastTurn = new { turnId = "settled", outcome = "completed", endedAt = EndedAt },
                    },
                    inputQueue = new { depth = Depth },
                });
            if (path.Contains("transcript-page", StringComparison.Ordinal))
                return Json(new
                {
                    session = new { id = "target-session", status = "Idle" },
                    page = new { epoch = "epoch", boundaryComplete = true, throughSequence = HighWater },
                    messages = new[]
                    {
                        new SessionMessage { Role = "user", EventType = "user_message", MessageUid = "prompt", Content = "Run the browser test." },
                        new SessionMessage { Role = "assistant", EventType = "tool_result", MessageUid = "final", Content = "hidden-tool-output" },
                        new SessionMessage { Role = "assistant", EventType = "text", Phase = "final_answer", MessageUid = "final", Content = "The test is ready and waiting for the lab." },
                    },
                });
            Assert.Equal("/ai-session/generate", path);
            Generations++;
            RequestBody = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct));
            if (BeforeOutput is not null) await BeforeOutput();
            if (InvalidOutput) return Json(new { success = true, text = "invalid" });
            var output = JsonSerializer.SerializeToNode(Waiting, DiscussionSummary.Json)!.AsObject();
            output["evidenceMessageUids"] = new JsonArray("final");
            return Json(new { success = true, text = output.ToJsonString() });
        }

        private static HttpResponseMessage Json(object value)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(value, DiscussionSummary.Json),
                    System.Text.Encoding.UTF8, "application/json"),
            };
    }

    private sealed class Records : IRecordStreams
    {
        private readonly List<LeafRecord> rows = [];
        public Task AppendAsync(string stream, JsonObject data, Guid? entityId = null, string? userId = null, CancellationToken ct = default)
        {
            rows.Add(new(rows.Count + 1, stream, entityId, userId, data, DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<LeafRecord>> QueryAsync(string stream, RecordQuery? query = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LeafRecord>>(rows.Where(r => r.Stream == stream && r.UserId == query?.UserId)
                .Reverse().Take(query?.Limit ?? 100).ToArray());
    }

    private sealed class RecordingEvents : IPluginEvents
    {
        public List<JsonObject> Invalidations { get; } = [];
        public Task PublishAsync(string type, JsonObject payload, CancellationToken ct = default)
        {
            Assert.Equal("discussion.changed", type);
            Invalidations.Add(payload);
            return Task.CompletedTask;
        }
        public IDisposable Subscribe(string eventType, Func<PluginEvent, Task> handler) => throw new NotSupportedException();
    }
}
