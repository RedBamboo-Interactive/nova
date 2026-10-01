using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class DelegationActivityTests
{
    [Theory]
    [InlineData("Active", 0, "empty", null, "running")]
    [InlineData("Starting", 0, "empty", null, "starting")]
    [InlineData("Idle", 2, "ready", null, "queued")]
    [InlineData("Idle", 1, "waiting_for_session", "user_stopped", "blocked")]
    [InlineData("Idle", 1, "delivering", null, "queued")]
    [InlineData("Idle", 0, "empty", "active_turn", "running")]
    [InlineData("Idle", 1, "waiting_for_session", "session_starting", "starting")]
    [InlineData("Idle", 0, "empty", null, "finished")]
    [InlineData("Idle", 2, "failed", null, "failed")]
    [InlineData("Stopped", 0, "empty", null, "finished")]
    [InlineData("Error", 1, "ready", null, "failed")]
    [InlineData("Ended", 0, "empty", null, "finished")]
    public void UsesCanonicalRuntimeAndQueueWithoutChangingParentState(
        string status, int depth, string queue, string? blocked, string expected)
        => Assert.Equal(expected, DelegationActivity.ProjectStatus(
            new(status, null, null, "owner", "agent", false, null, depth, queue, blocked)));

    [Theory]
    [InlineData("maintenance_restart", "waiting_to_resume")]
    [InlineData("orphaned_on_restart", "waiting_to_resume")]
    [InlineData("user_stopped", "finished")]
    public void AcceptedQueuedRecoveryDiffersFromExplicitStop(string reason, string expected)
        => Assert.Equal(expected, DelegationActivity.ProjectStatus(
            new("Stopped", reason, null, "owner", "agent", false, null, 1, "waiting_for_session", reason)));

    [Fact]
    public async Task LegacyMarkerSurvivesReloadAndSettlesFromCanonicalStateWithoutCallback()
    {
        var f = new Fixture();
        f.AddMarker("worker");
        f.Gateway.Set("worker", "Active");
        var active = await f.Read();
        Assert.Equal(1, active[f.Parent.Id].OngoingCount);
        Assert.Equal("running", Assert.Single(active[f.Parent.Id].Sessions).Status);
        Assert.Equal("idle", f.Parent.Status);
        var pageReads = f.Pages.Count;
        f.Gateway.Set("worker", "Idle");
        Assert.Equal(0, (await f.Read())[f.Parent.Id].OngoingCount);
        Assert.Equal(pageReads, f.Pages.Count); // Status changes do not rescan history.
        f.Reload();
        Assert.Empty((await f.Read())[f.Parent.Id].Sessions);
        Assert.True(f.Pages.Count > pageReads);
    }

    [Fact]
    public async Task DuplicateContinuationsCountOnceAndOldCompletionCannotClearNewWork()
    {
        var f = new Fixture();
        f.AddMarker("worker", repository: "Nova");
        f.Gateway.Set("worker", "Idle");
        Assert.Equal(0, (await f.Read())[f.Parent.Id].OngoingCount);
        f.AddMarker("worker", repository: null); // Same session, a new accepted prompt.
        f.AddMarker("worker", repository: null); // Another accepted continuation.
        f.AddRecord("user", "event:delegate:worker", "old completion", new { sessionId = "worker", callbackId = "old", status = "Idle" });
        f.Gateway.Set("worker", "Idle", depth: 1, queue: "waiting_for_session", blocked: "maintenance_drain");
        var queued = (await f.Read())[f.Parent.Id];
        Assert.Equal(1, queued.OngoingCount);
        var session = Assert.Single(queued.Sessions);
        Assert.Equal("queued", session.Status);
        Assert.Equal("Nova", session.Repository);
        f.Gateway.Set("worker", "Active");
        Assert.Equal("running", Assert.Single((await f.Read())[f.Parent.Id].Sessions).Status);
        f.Gateway.Set("worker", "Stopped", reason: "user_stopped");
        Assert.Equal(0, (await f.Read())[f.Parent.Id].OngoingCount);
    }

    [Fact]
    public async Task UnavailableOrIncompleteReadNeverFabricatesCompletionOrResurrectsSettledWork()
    {
        var f = new Fixture();
        f.AddMarker("worker");
        f.Gateway.Responses["worker"] = (503, "{}");
        var missing = (await f.Read())[f.Parent.Id];
        Assert.False(missing.Available);
        Assert.Equal(0, missing.OngoingCount);
        Assert.Equal(1, missing.UnknownCount);
        Assert.Equal("unavailable", Assert.Single(missing.Sessions).Status);
        f.Gateway.Set("worker", "Active");
        Assert.Equal(1, (await f.Read())[f.Parent.Id].OngoingCount);
        f.Gateway.Set("worker", "Idle", depth: null, queue: null);
        Assert.Equal(1, (await f.Read())[f.Parent.Id].OngoingCount);
        f.Gateway.Set("worker", "Idle");
        Assert.Equal(0, (await f.Read())[f.Parent.Id].OngoingCount);
        f.Gateway.Responses["worker"] = (503, "{}");
        var unavailable = (await f.Read())[f.Parent.Id];
        Assert.False(unavailable.Available);
        Assert.Empty(unavailable.Sessions);
        f.AddMarker("worker");
        var unresolved = (await f.Read())[f.Parent.Id];
        Assert.Equal(0, unresolved.OngoingCount);
        Assert.Equal(1, unresolved.UnknownCount); // New admission is unresolved, not known running.
    }

    [Fact]
    public async Task MalformedMarkersAndHistoricalCallbacksAreNotMembership()
    {
        var f = new Fixture();
        f.AddRecord("system", "event:automation", "event", new { sessionId = "unrelated", status = "started" });
        f.AddRecord("user", "event:delegation", "event", new { sessionId = "unrelated", status = "started" });
        f.AddRecord("system", "event:delegate:unrelated", "completed", new { sessionId = "unrelated", status = "Idle" });
        f.AddRecord("system", "event:delegation", "malformed", new { sessionId = "../private", status = "started" });
        var result = (await f.Read())[f.Parent.Id];
        Assert.Equal(0, result.OngoingCount);
        Assert.Empty(f.Gateway.Reads);
    }

    [Fact]
    public async Task IndexesBeyondFirstPageAndReadsEachDistinctSessionOnceAcrossParents()
    {
        var f = new Fixture();
        for (var i = 0; i < 1001; i++) f.AddRecord("assistant", "event:automation", "history", new { });
        f.AddMarker("worker");
        var second = Parent("second");
        f.Records.Add(Marker(f.Records.Count + 1, second.EntityId, "worker"));
        f.Gateway.Set("worker", "Starting");
        var result = await f.Activity.ReadAsync([f.Parent, second with { MessageCount = 1 }], Human());
        Assert.Equal(1, result[f.Parent.Id].OngoingCount);
        Assert.Equal(1, result[second.Id].OngoingCount);
        Assert.Single(f.Gateway.Reads);
        Assert.Contains(f.Pages, page => page.After >= 1000);
        var oldPages = f.Pages.Count;
        await f.Read();
        Assert.Equal(oldPages, f.Pages.Count);
        f.AddMarker("worker");
        await f.Read();
        Assert.True(f.Pages.Last().After > 1000);
    }

    [Theory]
    [InlineData("other-owner", "agent", false, false)]
    [InlineData("owner", "other-agent", false, false)]
    [InlineData("owner", "agent", true, false)]
    [InlineData("owner", "agent", true, true)]
    public async Task FiltersCanonicalOwnerAgentAndConfidentialScope(
        string owner, string agent, bool childConfidential, bool parentConfidential)
    {
        var f = new Fixture();
        f.Parent = f.Parent with { Confidential = parentConfidential };
        f.AddMarker("worker");
        f.Gateway.Set("worker", "Active", owner: owner, agent: agent, confidential: childConfidential);
        Assert.Equal(parentConfidential ? 1 : 0, (await f.Read())[f.Parent.Id].OngoingCount);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public async Task DeniedReadDoesNotReusePreviouslyVisibleWork(int code)
    {
        var f = new Fixture();
        f.AddMarker("worker");
        f.Gateway.Set("worker", "Active");
        Assert.Equal(1, (await f.Read())[f.Parent.Id].OngoingCount);
        f.Gateway.Responses["worker"] = (code, "{}");
        Assert.Equal(0, (await f.Read())[f.Parent.Id].OngoingCount);
        f.Gateway.Responses["worker"] = (503, "{}");
        Assert.Equal(0, (await f.Read())[f.Parent.Id].OngoingCount);
    }

    [Fact]
    public async Task ChecksParentAuthorityBeforeReadingAnyMarkersOrSessions()
    {
        var f = new Fixture();
        f.AddMarker("worker");
        f.Gateway.Set("worker", "Active");
        Assert.Empty(await f.Activity.ReadAsync([f.Parent], Human("other-owner")));
        Assert.Empty(await f.Activity.ReadAsync([f.Parent], Human("local-user", "LocalDefault")));
        Assert.Empty(await f.Activity.ReadAsync([f.Parent], AgentContext("other-agent")));
        Assert.Empty(await f.Activity.ReadAsync([f.Parent with { Status = "archived" }], Human()));
        Assert.Empty(f.Gateway.Reads);
        Assert.Empty(f.Pages);
        Assert.Equal(1, (await f.Activity.ReadAsync([f.Parent], AgentContext("agent")))[f.Parent.Id].OngoingCount);
    }

    [Fact]
    public async Task FreshnessAndInFlightCoalescingBoundBurstCallsAndRefreshOnlyChangedSession()
    {
        var f = new Fixture();
        f.AddMarker("worker");
        for (var i = 0; i < 40; i++)
        {
            f.AddMarker($"old-{i}");
            f.Gateway.Set($"old-{i}", "Idle");
        }
        f.Gateway.Set("worker", "Active");
        var initial = await f.Activity.ReadAsync([f.Parent], Human());
        Assert.Equal(1, initial[f.Parent.Id].OngoingCount);
        Assert.Equal(41, f.Gateway.Reads.Count);
        var pages = f.Pages.Count;
        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => f.Activity.ReadAsync([f.Parent], Human())));
        Assert.Equal(41, f.Gateway.Reads.Count);
        Assert.Equal(pages, f.Pages.Count);
        f.Advance(1);
        f.Gateway.Set("worker", "Idle");
        var settled = await f.Activity.ReadAsync([f.Parent], Human(), changedSessionIds: new HashSet<string> { "worker" });
        Assert.Equal(0, settled[f.Parent.Id].OngoingCount);
        Assert.Equal(42, f.Gateway.Reads.Count);
        f.AddMarker("worker");
        f.Gateway.Set("worker", "Idle", depth: 1, queue: "ready");
        Assert.Equal(1, (await f.Activity.ReadAsync([f.Parent], Human()))[f.Parent.Id].OngoingCount);
        Assert.Equal(43, f.Gateway.Reads.Count); // New marker bypasses the terminal cache.
    }

    [Fact]
    public async Task ColdOutageDoesNotCountHistoricalUnknownsAsOngoingAndCachesAreAuthorityScoped()
    {
        var f = new Fixture();
        f.AddMarker("old-worker");
        f.Gateway.Set("old-worker", "Idle");
        await f.Read();
        f.Reload();
        f.Gateway.Responses["old-worker"] = (503, "{}");
        var cold = (await f.Read())[f.Parent.Id];
        Assert.Equal(0, cold.OngoingCount);
        Assert.Equal(1, cold.UnknownCount);
        Assert.False(cold.Available);
        Assert.Null(Assert.Single(cold.Sessions).LastKnownStatus);
        f.Gateway.Set("old-worker", "Active");
        var otherIdentity = await f.Activity.ReadAsync([f.Parent], AgentContext("agent"));
        Assert.Equal(1, otherIdentity[f.Parent.Id].OngoingCount); // Cannot reuse the human's failed snapshot.
    }

    [Theory]
    [InlineData("user_id", "other-owner")]
    [InlineData("owner_agent_id", "other-agent")]
    [InlineData("confidential", "true")]
    public async Task CachedActivityNeverGrantsAccessAfterCanonicalScopeRevocation(string field, string value)
    {
        var f = new Fixture();
        f.AddMarker("worker");
        f.Gateway.Set("worker", "Active");
        var visible = await f.Activity.ReadAsync([f.Parent], Human());
        Assert.Equal("Worker title", Assert.Single(visible[f.Parent.Id].Sessions).Title);
        f.Gateway.Canonical["worker"].Data[field] = field == "confidential" ? JsonValue.Create(true) : JsonValue.Create(value);
        var revoked = (await f.Activity.ReadAsync([f.Parent], Human()))[f.Parent.Id];
        Assert.Equal(0, revoked.OngoingCount);
        Assert.Empty(revoked.Sessions);
        Assert.Equal("idle", f.Parent.Status);
    }

    [Theory]
    [InlineData("Active", null, "Idle", 0)]
    [InlineData("Stopped", "user_stopped", "Active", 1)]
    public async Task LiveLifecycleWinsOverStalePersistedEntityStatus(
        string storedStatus, string? storedStopReason, string liveStatus, int expectedCount)
    {
        var f = new Fixture();
        f.AddMarker("worker");
        f.Gateway.Set("worker", liveStatus);
        f.Gateway.Canonical["worker"].Data["status"] = storedStatus;
        f.Gateway.Canonical["worker"].Data["stop_reason"] = storedStopReason;
        var activity = (await f.Read())[f.Parent.Id];
        Assert.Equal(expectedCount, activity.OngoingCount);
        Assert.Equal("idle", f.Parent.Status);
    }

    [Fact]
    public async Task ExplicitFreshReadDiscoversResumedWorkBehindTerminalCache()
    {
        var f = new Fixture();
        f.AddMarker("worker");
        f.Gateway.Set("worker", "Idle");
        Assert.Equal(0, (await f.Activity.ReadAsync([f.Parent], Human()))[f.Parent.Id].OngoingCount);
        f.Advance(1);
        f.Gateway.Set("worker", "Active");
        Assert.Equal(1, (await f.Activity.ReadAsync([f.Parent], Human(), fresh: true))[f.Parent.Id].OngoingCount);
        Assert.Equal(2, f.Gateway.Reads.Count);
    }

    [Theory]
    [InlineData("waiting_for_session", "resume_authority_unavailable", "blocked")]
    [InlineData("failed", "delivery_outcome_unknown", "failed")]
    [InlineData("waiting_for_session", "deployment_target_failed", "failed")]
    [InlineData("waiting_for_session", "deployment_target_pending", "queued")]
    [InlineData("waiting_for_session", "deployment_target_unknown", "blocked")]
    [InlineData("waiting_for_session", "recovery_revalidate", "waiting_to_resume")]
    public void QueueBlockAndUncertainFailureRemainVisibleWithoutClaimingRunning(
        string queue, string error, string expected)
        => Assert.Equal(expected, DelegationActivity.ProjectStatus(new(
            "Stopped", "maintenance_restart", null, "owner", "agent", false, null, 1, queue, "maintenance_restart", error)));

    [Fact]
    public async Task FailedAcceptedWorkKeepsDistinctCountAndCodeLinkMembership()
    {
        var f = new Fixture();
        f.AddMarker("worker");
        f.AddMarker("worker");
        f.Gateway.Set("worker", "Idle", depth: 1, queue: "failed");
        var activity = (await f.Read())[f.Parent.Id];
        Assert.Equal(1, activity.OngoingCount);
        Assert.Equal("failed", Assert.Single(activity.Sessions).Status);
        Assert.Equal("worker", activity.Sessions[0].SessionId);
        Assert.Equal("idle", f.Parent.Status);
    }

    private sealed class Fixture
    {
        public DiscussionRead Parent = DelegationActivityTests.Parent("parent");
        public readonly List<DiscussionMessage> Records = [];
        public readonly List<(Guid Discussion, long After)> Pages = [];
        public readonly Gateway Gateway = new();
        private readonly IDiscussions messages;
        private readonly IEntityStore entities;
        public DelegationActivity Activity { get; private set; }
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public void Advance(int seconds) => now = now.AddSeconds(seconds);
        public Fixture()
        {
            messages = CallbackDeliveryTests.Proxy.Create<IDiscussions>((method, args) =>
            {
                Assert.Equal("GetMessagesAsync", method);
                var id = (Guid)args[0]!;
                var after = (long)args[2]!;
                Pages.Add((id, after));
                return Task.FromResult<IReadOnlyList<DiscussionMessage>>(Records.Where(m => m.DiscussionId == id && m.Id > after)
                    .OrderBy(m => m.Id).Take((int)args[1]!).ToArray());
            });
            entities = CallbackDeliveryTests.Proxy.Create<IEntityStore>((method, args) => {
                Assert.Equal("QueryAsync", method);
                var query = (EntityQuery)args[0]!;
                Assert.Equal("ai-session", query.TypeSlug);
                return Task.FromResult<IReadOnlyList<LeafEntity>>(Gateway.Canonical.Values
                    .Where(entity => query.DataEquals!.All(pair => entity.Data[pair.Key]?.ToString() == pair.Value?.ToString()))
                    .OrderBy(entity => entity.Id).Where(entity => query.AfterId is null || entity.Id.CompareTo(query.AfterId.Value) > 0)
                    .Take(query.Limit).ToArray());
            });
            Activity = new(messages, new RedComputeClient(Gateway), entities) { Now = () => now };
        }
        public void Reload() => Activity = new(messages, new RedComputeClient(Gateway), entities) { Now = () => now };
        public Task<IReadOnlyDictionary<string, DiscussionDelegationActivity>> Read()
        {
            Advance(31);
            return Activity.ReadAsync([Parent], Human());
        }
        public void AddMarker(string id, string? repository = "Nova")
        {
            Gateway.EnsureMetadata(id);
            Records.Add(Marker(Records.Count + 1, Parent.EntityId, id, repository));
            Parent = Parent with { MessageCount = Parent.MessageCount + 1, LastActivity = DateTime.UtcNow };
        }
        public void AddRecord(string role, string source, string content, object data)
        {
            Records.Add(new(Records.Count + 1, Parent.EntityId, role, content,
                new() { ["source"] = source, ["parts_json"] = Parts(data) }, DateTimeOffset.UtcNow));
            Parent = Parent with { MessageCount = Parent.MessageCount + 1, LastActivity = DateTime.UtcNow };
        }
    }

    private sealed class Gateway : IComputeGateway
    {
        public readonly ConcurrentDictionary<string, (int Code, string Body)> Responses = new();
        public readonly ConcurrentDictionary<string, LeafEntity> Canonical = new();
        public readonly ConcurrentBag<string> Reads = [];
        public void Set(string id, string status, int? depth = 0, string? queue = "empty", string? blocked = null,
            string? reason = null, string owner = "owner", string agent = "agent", bool confidential = false)
        {
            SetMetadata(id, status, reason, owner, agent, confidential);
            Responses[id] = (200, JsonSerializer.Serialize(new {
                session = new { id, status, stopReason = reason, title = "Worker title", userId = owner,
                    ownerAgentId = agent, confidential, repositoryId = "repo-id" },
                inputQueue = new { depth, state = queue, blockedReason = blocked }, messages = Array.Empty<object>() }));
        }
        public void EnsureMetadata(string id)
        {
            if (!Canonical.ContainsKey(id)) SetMetadata(id, "Idle", null, "owner", "agent", false);
        }
        private void SetMetadata(string id, string status, string? reason, string owner, string agent, bool confidential)
            => Canonical[id] = new(Guid.NewGuid(), "ai-session", $"session-{id}", "Worker title",
                new() { ["session_id"] = id, ["status"] = status, ["stop_reason"] = reason, ["user_id"] = owner,
                    ["owner_agent_id"] = agent, ["confidential"] = confidential, ["repository"] = "repo-id" },
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "test");
        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, ComputeProvenance? provenance = null, CancellationToken ct = default)
        {
            await Task.Yield();
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.EndsWith("?tail=1", request.RequestUri!.OriginalString);
            var id = request.RequestUri.OriginalString.Split('/')[3].Split('?')[0];
            Reads.Add(id);
            var response = Responses[id];
            return new HttpResponseMessage((HttpStatusCode)response.Code) { Content = new StringContent(response.Body) };
        }
    }

    private static string Parts(object data) => JsonSerializer.Serialize(new[] { new { type = "event_data", data } });
    private static DiscussionMessage Marker(long id, Guid parent, string session, string? repository = "Nova")
        => new(id, parent, "system", "delegated", new() { ["source"] = "event:delegation",
            ["parts_json"] = Parts(new { sessionId = session, repository, status = "started" }) }, DateTimeOffset.UtcNow);
    private static DiscussionRead Parent(string id) => new(id, "Parent", "parent-session", "idle",
        DateTime.UtcNow, DateTime.UtcNow, 0, null, "owner", Guid.NewGuid(), "agent");
    private static DefaultHttpContext Human(string owner = "owner", string auth = "test")
        => new() { User = new(new ClaimsIdentity([new("sub", owner)], auth)) };
    private static DefaultHttpContext AgentContext(string agent) => new() { User = new(new ClaimsIdentity([
        new("sub", "owner"), new("token_use", "execution"), new("execution_identity", JsonSerializer.Serialize(new {
            actor = new { kind = "agent", id = agent }, beneficiary = new { kind = "user", id = "owner" } })) ], "Bearer")) };
}
