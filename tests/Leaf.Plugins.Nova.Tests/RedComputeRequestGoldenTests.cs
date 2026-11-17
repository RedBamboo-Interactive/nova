using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Plugins.Nova.Endpoints;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

/// <summary>
/// Placement by reference, Layer 1 (Phase 2 design, section 6): the exact RedCompute request every Nova
/// creation path sends today, recorded once on the unchanged code. While callers move to
/// <c>placement</c>, the legacy members of every body must stay byte-identical to these goldens;
/// <c>placement</c> is the only member a later commit may add. Goldens live in
/// tests/Goldens/RedComputeRequests; set NOVA_GOLDEN_RECORD=1 to (re)write them.
/// <para>
/// Stage A (N1): every body carries <c>placement</c> as its last member, appended after the legacy ones. The
/// golden is compared with the body cut before that member (so the legacy goldens are unchanged files), the
/// placement is pinned per path, must pass RedCompute's parser rules (<see cref="PlacementRules"/>), and names
/// exactly the scratch allocation the path made.
/// </para>
/// Windows only: the paths are compared byte for byte, separators included.
/// </summary>
public sealed class RedComputeRequestGoldenTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "nova-request-golden-" + Guid.NewGuid().ToString("N"));
    private readonly string scratch;
    private readonly string workspace;
    private readonly string checkout;
    private readonly LeafEntity repository;
    private readonly LeafEntity codexAgent;

    public RedComputeRequestGoldenTests()
    {
        scratch = Path.Combine(root, "scratch");
        workspace = Path.Combine(root, "workspace");
        checkout = Path.Combine(root, "songes");
        Directory.CreateDirectory(scratch);
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(checkout);
        repository = new LeafEntity(Guid.Parse("7e1e0000-0000-4000-8000-000000000001"), "repository", "songes",
            "songes-carmins", new JsonObject { ["status"] = "active", ["local_path"] = checkout },
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "system");
        codexAgent = new LeafEntity(Guid.Parse("7e1e0000-0000-4000-8000-000000000002"), "agent", "codex-agent",
            "Codex Agent", new JsonObject { ["provider"] = "codex" },
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "system");
    }

    public void Dispose() { try { Directory.Delete(root, true); } catch { } }

    // ── MessagePipeline.TryCreateSessionAsync: every discussion session ─────────────────────────

    [WindowsGoldenFact]
    public Task Discussion_session_in_the_agent_workspace()
        => DiscussionAsync("discussion-mounted-workspace", mounted: true);

    private const string DiscussionPlacement =
        """{"workspace":{"agentId":"<agent-id>"},"scratch":{"owner":"nova","executionKey":"target"}}""";

    [WindowsGoldenFact]
    public Task Discussion_session_in_a_disposable_workspace()
        => DiscussionAsync("discussion-disposable-workspace", mounted: false);

    [WindowsGoldenFact]
    public Task Resident_unattended_discussion_session()
        => DiscussionAsync("discussion-heartbeat-resident-unattended", mounted: true,
            keepResident: true, unattended: true, parentJobId: "11111111-2222-4333-8444-555555555555");

    [WindowsGoldenFact]
    public Task External_conversation_session_without_a_discussion()
        => DiscussionAsync("discussion-external-no-discussion-id", mounted: true, discussionId: null,
            deliveredAsOneMessage: true, maxAccessLevel: "auto", developerInstructions: "Discord rules.");

    private async Task DiscussionAsync(string name, bool mounted, bool keepResident = false, bool unattended = false,
        string? parentJobId = null, string? discussionId = "target", bool deliveredAsOneMessage = false,
        string? maxAccessLevel = null, string? developerInstructions = null)
    {
        using var f = new CallbackDeliveryTests.Fixture();
        var gateway = new Recorder();
        var scratchSpace = new RecordingScratch(scratch);
        var pipeline = new MessagePipeline(null!, null!, f.Entities, null!, new RedComputeClient(gateway), f.Agents,
            new AgentWorkspaces(f.Agents, new FixedWorkspace(mounted ? workspace : null)), scratchSpace,
            null!, null!, NullLogger<MessagePipeline>.Instance);

        var sessionId = await pipeline.TryCreateSessionAsync(f.AgentId.ToString(), "test-owner",
            discussionId: discussionId, parentJobId: parentJobId, keepResident: keepResident, unattended: unattended,
            deliveredAsOneMessage: deliveredAsOneMessage, maxAccessLevel: maxAccessLevel,
            developerInstructions: developerInstructions);

        Assert.Equal("session-1", sessionId);
        Verify(name, Capture(gateway, scratchSpace), f.AgentId);
        var key = scratchSpace.Calls.Single().Key;
        AssertPlacement(PlacementRules.Route.Session, f.AgentId,
            DiscussionPlacement.Replace("\"target\"", "\"" + key + "\""));
    }

    // ── "Run on" (U1): the discussion's node_id reaches placement.nodeId, and only then ─────────

    private const string NodeList =
        """{"nodes":[{"id":"node_01ZERO","isLocal":true},{"id":"node_01STUDIO","isLocal":false}],"legacyNodeId":"node_01LEGACY"}""";

    private static readonly string[] Registry = ["node_01ZERO", "node_01STUDIO"];

    [WindowsGoldenFact]
    public async Task Automatic_reads_no_node_list_and_sends_the_golden_body()
    {
        var gateway = await RunOnAsync(runOnNodeId: null, nodeList: NodeList);
        Assert.Empty(gateway.NodeReads);
    }

    [WindowsGoldenFact]
    public async Task A_listed_node_comes_first_in_the_placement_and_nothing_else_changes()
    {
        var gateway = await RunOnAsync("node_01STUDIO", NodeList,
            """{"nodeId":"node_01STUDIO","workspace":{"agentId":"<agent-id>"},"scratch":{"owner":"nova","executionKey":"target"}}""");
        // Read for the discussion's owner: the Agent counts as the person it works for.
        Assert.Single(gateway.NodeReads);
    }

    [WindowsGoldenFact]
    public Task A_node_RedCompute_no_longer_lists_is_never_sent()
        => RunOnAsync("node_01GONE", NodeList);

    [WindowsGoldenFact]
    public Task A_node_list_that_cannot_be_read_starts_automatically()
        => RunOnAsync("node_01STUDIO", nodeList: null);

    [WindowsGoldenFact]
    public Task A_node_list_of_another_shape_starts_automatically()
        => RunOnAsync("node_01STUDIO", """[{"id":"node_01STUDIO"}]""");

    [Fact]
    public void A_session_start_waits_at_most_three_seconds_for_the_node_list()
        => Assert.Equal(TimeSpan.FromSeconds(3), MessagePipeline.DefaultNodeListTimeout);

    [WindowsGoldenFact]
    public async Task A_node_list_that_hangs_starts_automatically_once_the_bound_runs_out()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var gateway = await RunOnAsync("node_01STUDIO", NodeList, hang: true, bound: TimeSpan.FromMilliseconds(200));

        // The same body and placement as Automatic, after the bound and not the client's own timeout.
        Assert.Single(gateway.NodeReads);
        Assert.Single(gateway.Creations);
        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(10));
    }

    [WindowsGoldenFact]
    public async Task The_callers_cancellation_during_a_hanging_node_list_still_cancels_the_start()
    {
        using var f = new CallbackDeliveryTests.Fixture();
        var gateway = new Recorder { NodeList = NodeList, HangNodeList = true };
        var pipeline = new MessagePipeline(null!, null!, f.Entities, null!, new RedComputeClient(gateway), f.Agents,
            new AgentWorkspaces(f.Agents, new FixedWorkspace(workspace)), new RecordingScratch(scratch),
            null!, null!, NullLogger<MessagePipeline>.Instance) { NodeListTimeout = TimeSpan.FromMinutes(5) };
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.TryCreateSessionAsync(
            f.AgentId.ToString(), "test-owner", ct: caller.Token, discussionId: "target", runOnNodeId: "node_01STUDIO"));

        // Cancelled by its caller, it is not taken for an unreadable list: no session starts automatically.
        Assert.Single(gateway.NodeReads);
        Assert.Empty(gateway.Creations);
    }

    /// <summary>
    /// A discussion session with <paramref name="runOnNodeId"/>: the legacy members are the discussion golden's,
    /// byte for byte, and the placement is <paramref name="expectedPlacement"/> (default: Automatic's).
    /// </summary>
    private async Task<Recorder> RunOnAsync(string? runOnNodeId, string? nodeList, string? expectedPlacement = null,
        bool hang = false, TimeSpan? bound = null)
    {
        using var f = new CallbackDeliveryTests.Fixture();
        var gateway = new Recorder { NodeList = nodeList, HangNodeList = hang };
        var scratchSpace = new RecordingScratch(scratch);
        var pipeline = new MessagePipeline(null!, null!, f.Entities, null!, new RedComputeClient(gateway), f.Agents,
            new AgentWorkspaces(f.Agents, new FixedWorkspace(workspace)), scratchSpace,
            null!, null!, NullLogger<MessagePipeline>.Instance) { NodeListTimeout = bound ?? MessagePipeline.DefaultNodeListTimeout };

        var sessionId = await pipeline.TryCreateSessionAsync(f.AgentId.ToString(), "test-owner",
            discussionId: "target", runOnNodeId: runOnNodeId);

        Assert.Equal("session-1", sessionId);
        Verify("discussion-mounted-workspace", Capture(gateway, scratchSpace), f.AgentId);
        var placement = Assert.Single(placements);
        Assert.Equal((expectedPlacement ?? DiscussionPlacement).Replace("<agent-id>", f.AgentId.ToString("D")),
            placement!.ToJsonString());
        Assert.Null(PlacementRules.Problem(new JsonObject { ["placement"] = placement.DeepClone() },
            PlacementRules.Route.Session, registry: Registry));
        if (runOnNodeId is not null && nodeList is not null && nodeList.StartsWith('{'))
            Assert.Single(gateway.NodeReads);
        return gateway;
    }

    // ── NovaSessionActionHandler: the nova-session automation action ───────────────────────────

    [WindowsGoldenFact]
    public Task Nova_session_automation()
        => AutomationAsync("automation-nova-session", codex: false, flowNode: null);

    [WindowsGoldenFact]
    public Task Nova_session_automation_in_a_workflow_node_on_codex()
        => AutomationAsync("automation-nova-session-codex-flow-node", codex: true, flowNode: "node/run 1");

    private async Task AutomationAsync(string name, bool codex, string? flowNode)
    {
        using var f = new CallbackDeliveryTests.Fixture(extraEntities: [codexAgent]);
        var gateway = new Recorder();
        var scratchSpace = new RecordingScratch(scratch);
        var agentId = codex ? codexAgent.Id : f.AgentId;
        var handler = new NovaSessionActionHandler(f.Agents,
            new AgentWorkspaces(f.Agents, new FixedWorkspace(workspace)), f.Store, f.Injector,
            new RedComputeClient(gateway), f.Entities, scratchSpace);
        var automation = new LeafEntity(Guid.Parse("7e1e0000-0000-4000-8000-0000000000a1"), "automation",
            "nightly-review", "Nightly review",
            new JsonObject { ["agent"] = agentId.ToString(), ["prompt"] = "Review the day." },
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "system");

        var result = await handler.ExecuteAsync(new AutomationActionContext
        {
            Automation = automation,
            Beneficiary = new ComputeBeneficiary("system", Reason: "test"),
            AttemptJobId = Guid.Parse("7e1e0000-0000-4000-8000-0000000000b1"),
            CorrelationId = "test",
            NodeId = flowNode,
            ActionConfig = new JsonObject { ["qualityMode"] = "standard" },
        }, CancellationToken.None);

        Assert.NotNull(result);
        Verify(name, Capture(gateway, scratchSpace), agentId);
        var (owner, key) = scratchSpace.Calls.Single();
        AssertPlacement(PlacementRules.Route.Execute, agentId,
            $$"""{"workspace":{"agentId":"<agent-id>"},"scratch":{"owner":"{{owner}}","executionKey":"{{key}}"},"scratchAs":"add-dir"}""");
    }

    // ── DelegateEndpoints.HandleDelegateAsync ─────────────────────────────────────────────────

    [WindowsGoldenFact]
    public Task Delegation_to_a_repository_checkout()
        => DelegateAsync("delegate-repository", new DelegateRequest { Repository = "songes", Prompt = "work on it" });

    [WindowsGoldenFact]
    public Task Delegation_to_a_repository_by_id()
        => DelegateAsync("delegate-repository-by-id",
            new DelegateRequest { Repository = repository.Id.ToString(), Prompt = "work on it" });

    [WindowsGoldenFact]
    public Task Delegation_to_an_existing_worktree()
        => DelegateAsync("delegate-branch-existing-worktree",
            new DelegateRequest { Repository = "songes", Branch = "laurent/x", Prompt = "work on it" },
            code => code.Items.Add(FakeCode.Worktree(WorktreeFolder("laurent/x"), "laurent/x")));

    [WindowsGoldenFact]
    public Task Delegation_to_a_new_worktree_created_by_Code()
        => DelegateAsync("delegate-branch-new-worktree",
            new DelegateRequest { Repository = "songes", Branch = "feature/new", Base = "develop", Prompt = "work on it" });

    [WindowsGoldenFact]
    public Task Delegation_to_a_branch_checked_out_in_the_checkout()
        => DelegateAsync("delegate-branch-is-checkout",
            new DelegateRequest { Repository = "songes", Branch = "laurent/outils", Prompt = "work on it" },
            code => code.Items[0] = FakeCode.Worktree(checkout, "laurent/outils", isCheckout: true));

    [WindowsGoldenFact]
    public Task Delegation_by_compatibility_project_path()
        => DelegateAsync("delegate-project-path-compat",
            new DelegateRequest { ProjectPath = checkout, Prompt = "work on it" });

    [WindowsGoldenFact]
    public Task Delegation_to_an_agent_workspace()
        => DelegateAsync("delegate-agent-workspace",
            new DelegateRequest { Agent = "nova", Prompt = "think about it", QualityTier = "deep" });

    [WindowsGoldenFact]
    public Task Delegation_with_provider_and_model()
        => DelegateAsync("delegate-repository-provider-model",
            new DelegateRequest { Repository = "songes", Prompt = "work on it", Provider = "codex", Model = "gpt-5" });

    private const string RepositoryPlacement = """{"repository":{"repositoryId":"7e1e0000-0000-4000-8000-000000000001"}}""";

    private static string BranchPlacement(string branch)
        => """{"repository":{"repositoryId":"7e1e0000-0000-4000-8000-000000000001","branch":"<branch>"}}""".Replace("<branch>", branch);

    private static string ExpectedDelegatePlacement(string name) => name switch
    {
        "delegate-branch-existing-worktree" => BranchPlacement("laurent/x"),
        "delegate-branch-new-worktree" => BranchPlacement("feature/new"),
        "delegate-branch-is-checkout" => BranchPlacement("laurent/outils"),
        "delegate-agent-workspace" => """{"workspace":{"agentId":"<agent-id>"}}""",
        _ => RepositoryPlacement,
    };

    private async Task DelegateAsync(string name, DelegateRequest request, Action<FakeCode>? arrange = null)
    {
        using var f = new CallbackDeliveryTests.Fixture(extraEntities: [repository]);
        var gateway = new Recorder();
        var code = new FakeCode(checkout);
        arrange?.Invoke(code);
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "test-owner")], "test")),
        };
        ctx.Request.Headers.Authorization = "Bearer caller-token";

        var result = await DelegateEndpoints.HandleDelegateAsync(ctx, request, new RedComputeClient(gateway), f.Agents,
            new AgentWorkspaces(f.Agents, new FixedWorkspace(workspace)), f.Store, f.Entities, f.Injector,
            new CodeWorktreeClient(new HttpClient(code)));

        Assert.Equal(200, ((IStatusCodeHttpResult)result).StatusCode ?? 200);
        var capture = Capture(gateway, scratchSpace: null);
        capture["codeRequests"] = new JsonArray(code.Requests
            .Select(r => (JsonNode)new JsonObject
            {
                ["method"] = r.Method,
                ["path"] = r.Path,
                ["body"] = r.Body is null ? null : JsonNode.Parse(r.Body.Value.GetRawText()),
            }).ToArray());
        Verify(name, capture, f.AgentId);
        AssertPlacement(PlacementRules.Route.Session, f.AgentId, ExpectedDelegatePlacement(name));
    }

    private string WorktreeFolder(string branch) => Path.Combine(checkout + ".worktrees", branch.Replace('/', '-'));

    // ── capture and verification ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The requests that create work (session create, one-shot execute), in order, with the headers that
    /// key them, plus every scratch allocation the path asked the kernel for.
    /// </summary>
    private JsonObject Capture(Recorder gateway, RecordingScratch? scratchSpace)
    {
        var capture = new JsonObject
        {
            ["requests"] = new JsonArray(gateway.Creations.Select(r => (JsonNode)new JsonObject
            {
                ["method"] = r.Method,
                ["path"] = r.Path,
                ["headers"] = new JsonObject(r.Headers.OrderBy(h => h.Key, StringComparer.Ordinal)
                    .Select(h => KeyValuePair.Create(h.Key, (JsonNode?)h.Value))),
                ["body"] = LegacyBody(r.Body),
            }).ToArray()),
        };
        if (scratchSpace is not null)
            capture["scratchAllocations"] = new JsonArray(scratchSpace.Calls
                .Select(c => (JsonNode)new JsonObject { ["owner"] = c.Owner, ["executionKey"] = StableKey(c.Key) }).ToArray());
        return capture;
    }

    private const string PlacementMember = ",\"placement\":";

    private readonly List<JsonObject?> placements = [];

    /// <summary>
    /// The body as the legacy golden knows it: the bytes before <c>placement</c>, which must be the last member
    /// (appended after every legacy one). The placement is kept for <see cref="AssertPlacement"/>.
    /// </summary>
    private JsonNode LegacyBody(string raw)
    {
        var body = JsonNode.Parse(raw)!.AsObject();
        if (!body.TryGetPropertyValue("placement", out var placement))
        {
            placements.Add(null);
            return body;
        }
        Assert.Equal("placement", body.Last().Key);
        var cut = raw.LastIndexOf(PlacementMember, StringComparison.Ordinal);
        Assert.True(cut > 0, "placement is not a member after the legacy ones: " + raw);
        Assert.EndsWith("}}", raw);
        var legacy = JsonNode.Parse(raw[..cut] + "}")!.AsObject();
        var withoutPlacement = body.DeepClone().AsObject();
        withoutPlacement.Remove("placement");
        Assert.True(JsonNode.DeepEquals(withoutPlacement, legacy), "the bytes before placement are not the legacy body: " + raw);
        placements.Add(placement!.AsObject());
        return legacy;
    }

    /// <summary>
    /// The one request's placement: exactly <paramref name="expected"/> (members in this order), accepted by
    /// RedCompute's parser rules for <paramref name="route"/>.
    /// </summary>
    private void AssertPlacement(PlacementRules.Route route, Guid agentId, string expected)
    {
        var placement = Assert.Single(placements);
        Assert.NotNull(placement);
        Assert.Equal(expected.Replace("<agent-id>", agentId.ToString("D")), placement!.ToJsonString());
        Assert.Null(PlacementRules.Problem(new JsonObject { ["placement"] = placement.DeepClone() }, route));
    }

    /// <summary>
    /// A session with neither a discussion nor a parent job gets a fresh random key on every creation
    /// (MessagePipeline: <c>Guid.NewGuid().ToString("N")</c>); the fixture's own ids all start 7e1e0000.
    /// </summary>
    private static string StableKey(string key)
        => Guid.TryParseExact(key, "N", out _) && !key.StartsWith("7e1e0000", StringComparison.Ordinal)
            ? "<random-guid-n>"
            : key;

    private void Verify(string name, JsonObject capture, Guid agentId)
    {
        var text = Canonical(capture)
            .Replace(Escape(root), "<root>", StringComparison.OrdinalIgnoreCase)
            .Replace(root, "<root>", StringComparison.OrdinalIgnoreCase)
            .Replace(root.Replace('\\', '/'), "<root>", StringComparison.OrdinalIgnoreCase)
            .Replace(agentId.ToString(), "<agent-id>", StringComparison.OrdinalIgnoreCase)
            .Replace(agentId.ToString("N"), "<agent-id>", StringComparison.OrdinalIgnoreCase)
            // Raw string literals carry the checkout's line ends (core.autocrlf): not part of the contract.
            .Replace("\\r\\n", "\\n", StringComparison.Ordinal);
        // The automation prompt stamps the run's local date and time.
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"Current date: \d{4}-\d{2}-\d{2} \(\w+\), \d{2}:\d{2} local time\.", "Current date: <now>.");
        RequestGoldens.Verify(name, text);
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\");

    private static string Canonical(JsonNode node)
        => Sort(node)!.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }).Replace("\r\n", "\n") + "\n";

    private static JsonNode? Sort(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => KeyValuePair.Create(pair.Key, Sort(pair.Value)))),
        JsonArray array => new JsonArray(array.Select(Sort).ToArray()),
        null => null,
        _ => node.DeepClone(),
    };

    // ── fakes ─────────────────────────────────────────────────────────────────────────────────

    private sealed record Seen(string Method, string Path, Dictionary<string, string> Headers, string Body);

    /// <summary>RedCompute as a new session or a one-shot sees it; every call that creates work is recorded.</summary>
    private sealed class Recorder : IComputeGateway
    {
        public List<Seen> Creations { get; } = [];

        /// <summary>RedCompute's GET /nodes answer; null answers 503 (the list cannot be read).</summary>
        public string? NodeList { get; set; }

        /// <summary>GET /nodes never answers.</summary>
        public bool HangNodeList { get; set; }

        /// <summary>Each GET /nodes, with the beneficiary it was read for.</summary>
        public List<string?> NodeReads { get; } = [];

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            ComputeProvenance? provenance = null, CancellationToken ct = default)
        {
            var raw = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var path = request.RequestUri!.ToString();
            if (request.Method == HttpMethod.Get && path == "/nodes")
            {
                NodeReads.Add(provenance?.OnBehalfOf.Id);
                // A RedCompute that never answers: only the caller's token ends the wait.
                if (HangNodeList) await Task.Delay(Timeout.Infinite, ct);
                return NodeList is null ? new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") } : Json(NodeList);
            }
            if (request.Method == HttpMethod.Post && path is "/ai-session/sessions" or "/ai-session/execute")
            {
                var headers = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var header in new[] { "X-Job-Name", "X-Idempotency-Key" })
                    if (request.Headers.TryGetValues(header, out var values))
                        headers[header] = string.Join(",", values);
                Creations.Add(new(request.Method.Method, path, headers, raw));
                return Json(path == "/ai-session/sessions"
                    ? """{"id":"session-1"}"""
                    : """{"success":true,"text":"{\"success\":true,\"summary\":\"done\"}"}""");
            }
            if (path.EndsWith("/callback")) return Json("""{"registered":true}""");
            if (path.EndsWith("/supervisor")) return Json(raw);
            if (path.EndsWith("/message")) return Json("""{"accepted":true}""");
            return Json("{}");
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    }

    /// <summary>The kernel's scratch allocation for one test folder, with the environment the kernel builds.</summary>
    private sealed class RecordingScratch(string path) : IAgentScratchSpace
    {
        public List<(string Owner, string Key)> Calls { get; } = [];

        public AgentScratchAllocation PrepareExecution(string owner, string executionKey)
        {
            Calls.Add((owner, executionKey));
            return new(path, new Dictionary<string, string>
            {
                ["REDLEAF_SCRATCH_ROOT"] = path,
                ["REDLEAF_SCRATCH_DIR"] = path,
                ["TEMP"] = path,
                ["TMP"] = path,
                ["TMPDIR"] = path,
            });
        }
    }

    private sealed class FixedWorkspace(string? path) : IAgentWorkspacePathResolver
    {
        public Task<string?> ResolveAsync(Guid agentId, CancellationToken ct = default) => Task.FromResult(path);
    }

    /// <summary>Code's worktree API for one checkout (the same contract WorktreeBranchTests fakes).</summary>
    private sealed class FakeCode(string checkout) : HttpMessageHandler
    {
        public sealed record Request(string Method, string Path, JsonElement? Body);

        public List<JsonElement> Items { get; } = [Worktree(checkout, "main", isCheckout: true)];
        public List<Request> Requests { get; } = [];

        public static JsonElement Worktree(string path, string branch, bool isCheckout = false)
            => JsonSerializer.SerializeToElement(new
            {
                path = path.Replace('\\', '/'), branch, head = "abc", isCheckout, detached = false, locked = false,
                missing = false,
            });

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var raw = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            JsonElement? body = raw.Length == 0 ? null : JsonSerializer.Deserialize<JsonElement>(raw);
            Requests.Add(new(request.Method.Method, request.RequestUri!.AbsolutePath, body));
            if (request.Method == HttpMethod.Get)
                return Json(new { ok = true, repositoryId = "r", defaultBranch = "main", branchWork = "allowed", items = Items });
            var branch = body!.Value.GetProperty("branch").GetString()!;
            var entry = Worktree(Path.Combine(checkout + ".worktrees", branch.Replace('/', '-')), branch);
            Items.Add(entry);
            return Json(new { ok = true, entry, items = Items });
        }

        private static HttpResponseMessage Json(object value)
            => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}

/// <summary>A golden fact: byte-level path comparison only holds on the platform the goldens were recorded on.</summary>
public sealed class WindowsGoldenFactAttribute : FactAttribute
{
    public WindowsGoldenFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Request goldens are recorded on Windows (path bytes, separators included)";
    }
}

internal static class RequestGoldens
{
    public const string RecordVariable = "NOVA_GOLDEN_RECORD";

    private static bool Recording => Environment.GetEnvironmentVariable(RecordVariable) == "1";

    public static void Verify(string name, string text)
    {
        var directory = Path.Combine(RepoRoot(), "tests", "Goldens", "RedComputeRequests");
        var path = Path.Combine(directory, name + ".json");
        if (Recording)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return;
        }
        Assert.True(File.Exists(path), $"Missing request golden {name}.json; record with {RecordVariable}=1 on the unchanged code");
        var expected = File.ReadAllText(path).Replace("\r\n", "\n");
        if (expected == text) return;
        var dump = Path.Combine(Path.GetTempPath(), "nova-request-goldens-actual", name + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(dump)!);
        File.WriteAllText(dump, text, new UTF8Encoding(false));
        Assert.Fail($"Request golden {name} changed. Expected {path}, actual written to {dump}.");
    }

    private static string RepoRoot([CallerFilePath] string file = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(file)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "plugin.json")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("plugin.json not found above " + file);
    }
}
