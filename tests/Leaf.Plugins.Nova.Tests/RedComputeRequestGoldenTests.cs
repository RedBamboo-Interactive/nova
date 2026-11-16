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
    }

    private string WorktreeFolder(string branch) => Path.Combine(checkout + ".worktrees", branch.Replace('/', '-'));

    // ── capture and verification ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The requests that create work (session create, one-shot execute), in order, with the headers that
    /// key them, plus every scratch allocation the path asked the kernel for.
    /// </summary>
    private static JsonObject Capture(Recorder gateway, RecordingScratch? scratchSpace)
    {
        var capture = new JsonObject
        {
            ["requests"] = new JsonArray(gateway.Creations.Select(r => (JsonNode)new JsonObject
            {
                ["method"] = r.Method,
                ["path"] = r.Path,
                ["headers"] = new JsonObject(r.Headers.OrderBy(h => h.Key, StringComparer.Ordinal)
                    .Select(h => KeyValuePair.Create(h.Key, (JsonNode?)h.Value))),
                ["body"] = JsonNode.Parse(r.Body),
            }).ToArray()),
        };
        if (scratchSpace is not null)
            capture["scratchAllocations"] = new JsonArray(scratchSpace.Calls
                .Select(c => (JsonNode)new JsonObject { ["owner"] = c.Owner, ["executionKey"] = StableKey(c.Key) }).ToArray());
        return capture;
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

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            ComputeProvenance? provenance = null, CancellationToken ct = default)
        {
            var raw = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var path = request.RequestUri!.ToString();
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
