using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class DiscussionGroupingTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-19T14:00:00Z");
    private static GroupingInput[] Inputs => [new("a", "Lands of Lore inventory", "agent", []), new("b", "Lands of Lore icons", "agent", [])];
    private const string Output = """{"assignments":[{"discussionId":"a","group":"Lands of Lore","label":"Inventory","icon":"layout"},{"discussionId":"b","group":"Lands of Lore","label":"Icons","icon":"image"}],"groups":[{"name":"Lands of Lore","icon":"mountains","thumbnailDiscussionId":"b"}]}""";
    private const string SingletonOutput = """{"assignments":[{"discussionId":"a","group":"Lands of Lore","label":"Inventory","icon":"layout"}],"groups":[{"name":"Lands of Lore","icon":"mountains","thumbnailDiscussionId":"a"}]}""";
    private static DefaultHttpContext Human(string subject = "owner") => new() { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", subject)], "Bearer")) };

    [Fact]
    public void Strict_ids_and_lengths_prevent_model_invented_rows()
    {
        Assert.Throws<JsonException>(() => DiscussionGroupingPolicy.Parse(Output.Replace("\"a\"", "\"unknown\""), Inputs, null, Now));
        Assert.Throws<JsonException>(() => DiscussionGroupingPolicy.Parse(Output.Replace("\"b\"", "\"a\""), Inputs, null, Now));
        Assert.Throws<JsonException>(() => DiscussionGroupingPolicy.Parse(Output.Replace("Inventory", new string('x', 91)), Inputs, null, Now));
        Assert.Throws<JsonException>(() => DiscussionGroupingPolicy.Parse("[]", Inputs, null, Now));
        Assert.Throws<JsonException>(() => DiscussionGroupingPolicy.Parse("{\"assignments\":false}", Inputs, null, Now));
    }

    [Fact]
    public void A_recognizable_project_with_one_discussion_keeps_its_heading()
    {
        var result = DiscussionGroupingPolicy.Parse(SingletonOutput, Inputs, null, Now);
        Assert.Equal("Lands of Lore", Assert.Single(result.Assignments).Group);
        Assert.Equal("Lands of Lore", Assert.Single(result.GroupOrder));
        Assert.Equal("a", Assert.Single(result.Groups!).ThumbnailDiscussionId);
    }

    [Fact]
    public void Existing_meaningful_memberships_and_group_order_survive_additions()
    {
        var previous = DiscussionGroupingPolicy.Parse(Output, Inputs, null, Now);
        var inputs = Inputs.Append(new("c", "Cloud nodes", "agent", [])).ToArray();
        var result = DiscussionGroupingPolicy.Parse(Output.Replace("Lands of Lore", "Games"), inputs, previous, Now);
        Assert.Equal(["Lands of Lore"], result.GroupOrder);
        Assert.All(result.Assignments, a => Assert.Equal("Lands of Lore", a.Group));
    }

    [Fact]
    public void Redundant_project_prefixes_are_removed_and_labels_can_improve_without_regrouping()
    {
        var first = DiscussionGroupingPolicy.Parse(Output.Replace("Inventory", "Lands of Lore: Inventory"), Inputs, null, Now);
        Assert.Equal("Inventory", first.Assignments.Single(a => a.DiscussionId == "a").Label);
        var next = DiscussionGroupingPolicy.Parse(Output.Replace("Inventory", "HUD and inventory"), Inputs, first, Now);
        Assert.Equal("HUD and inventory", next.Assignments.Single(a => a.DiscussionId == "a").Label);
        Assert.All(next.Assignments, a => Assert.Equal("Lands of Lore", a.Group));
        var exact = DiscussionGroupingPolicy.Parse(Output.Replace("Inventory", "Lands of Lore"), Inputs, null, Now);
        Assert.Equal("Lands of Lore", exact.Assignments.Single(a => a.DiscussionId == "a").Label);
        var prefixWord = DiscussionGroupingPolicy.Parse(Output.Replace("Inventory", "Lands of Lorekeepers"), Inputs, null, Now);
        Assert.Equal("Lands of Lorekeepers", prefixWord.Assignments.Single(a => a.DiscussionId == "a").Label);
    }

    [Fact]
    public void A_changed_title_or_removed_member_invalidates_cached_derived_metadata()
    {
        var previous = DiscussionGroupingPolicy.Parse(Output, Inputs, null, Now);
        Assert.True(DiscussionGroupingPolicy.StillReadable(previous, Inputs.Append(new("c", "New", "agent", [])).ToArray()));
        Assert.False(DiscussionGroupingPolicy.StillReadable(previous, Inputs.Take(1).ToArray()));
        Assert.False(DiscussionGroupingPolicy.StillReadable(previous, [Inputs[0] with { Title = "Private subject" }, Inputs[1]]));
    }

    [Fact]
    public void Unknown_icons_are_replaced_without_accepting_classes_or_urls()
    {
        var result = DiscussionGroupingPolicy.Parse(Output.Replace("layout", "https://evil.test/img"), Inputs, null, Now);
        Assert.Equal("chats-circle", result.Assignments[0].Icon);
        Assert.Equal("Lands of Lore inventory", result.Assignments[0].SourceTitle);
    }

    [Fact]
    public void Groups_have_their_own_stable_visual_identity()
    {
        var first = DiscussionGroupingPolicy.Parse(Output, Inputs, null, Now);
        var visual = Assert.Single(first.Groups!);
        Assert.Equal("mountains", visual.Icon);
        Assert.Equal("b", visual.ThumbnailDiscussionId);
        var refreshed = DiscussionGroupingPolicy.Parse(Output.Replace("mountains", "code")
            .Replace("\"thumbnailDiscussionId\":\"b\"", "\"thumbnailDiscussionId\":\"a\""), Inputs, first, Now);
        Assert.Equal(visual, Assert.Single(refreshed.Groups!));
    }

    [Fact]
    public void Group_visuals_reject_unknown_duplicate_and_cross_group_members()
    {
        Assert.Throws<JsonException>(() => DiscussionGroupingPolicy.Parse(Output.Replace("\"thumbnailDiscussionId\":\"b\"", "\"thumbnailDiscussionId\":\"foreign\""), Inputs, null, Now));
        var extra = Inputs.Append(new("c", "Unrelated", "agent", [])).ToArray();
        Assert.Throws<JsonException>(() => DiscussionGroupingPolicy.Parse(Output.Replace("\"thumbnailDiscussionId\":\"b\"", "\"thumbnailDiscussionId\":\"c\""), extra, null, Now));
        var parsed = JsonNode.Parse(Output)!;
        var groups = parsed["groups"]!.AsArray();
        groups.Add(groups[0]!.DeepClone());
        Assert.Throws<JsonException>(() => DiscussionGroupingPolicy.Parse(parsed.ToJsonString(), Inputs, null, Now));
        parsed["groups"] = new JsonArray(42);
        Assert.Throws<JsonException>(() => DiscussionGroupingPolicy.Parse(parsed.ToJsonString(), Inputs, null, Now));
        parsed["groups"] = new JsonArray();
        Assert.Throws<JsonException>(() => DiscussionGroupingPolicy.Parse(parsed.ToJsonString(), Inputs, null, Now));
        var fallback = DiscussionGroupingPolicy.Parse(Output.Replace("mountains", "invalid"), Inputs, null, Now);
        Assert.Equal("chats-circle", Assert.Single(fallback.Groups!).Icon);
    }

    [Fact]
    public async Task Legacy_snapshots_refresh_once_for_explicit_group_visuals()
    {
        var world = new World();
        await world.Refresh();
        world.Records.Rows[0].Data["projection"]!.AsObject().Remove("groups");
        Assert.Null((await world.Service().ReadAsync("owner", Human(), default)).SourceFingerprint);
        var result = await world.Service().RefreshAsync("owner", Human(), default);
        Assert.Single(result.Groups!);
        Assert.Equal(2, world.Gateway.Calls);
    }

    [Fact]
    public async Task Reads_do_no_inference_and_success_survives_a_new_service_instance()
    {
        var world = new World();
        Assert.Empty((await world.Read()).Assignments);
        Assert.Equal(0, world.Gateway.Calls);
        var result = await world.Refresh();
        Assert.Equal(2, result.Assignments.Length);
        Assert.Equal(1, world.Gateway.Calls);
        Assert.Single(world.Records.Rows);
        var restored = world.Service();
        Assert.Equal(result.Assignments, (await restored.ReadAsync("owner", Human(), default)).Assignments);
        await restored.RefreshAsync("owner", Human(), default);
        Assert.Equal(1, world.Gateway.Calls);
        Assert.Equal("Lands of Lore inventory", world.Entities.Items.First(e => e.TypeSlug == "discussion").Data["title"]!.ToString());
    }

    [Fact]
    public async Task Canonical_cache_owner_does_not_impersonate_a_local_browser_compute_beneficiary()
    {
        var local = new World();
        // The endpoint already resolved its canonical cache key to owner.
        var localContext = Human("local-user");
        localContext.Items[InstallationOwner.RequestItem] = "owner";
        await local.Service().RefreshAsync("owner", localContext, default);
        Assert.Equal("system", local.Gateway.Provenance!.OnBehalfOf.Kind);
        Assert.Equal("owner", Assert.Single(local.Records.Rows).UserId);
        var signedOwner = new World();
        await signedOwner.Refresh();
        Assert.Equal("user", signedOwner.Gateway.Provenance!.OnBehalfOf.Kind);
        Assert.Equal("owner", signedOwner.Gateway.Provenance!.OnBehalfOf.Id);
    }

    [Fact]
    public async Task Concurrent_clients_share_one_completion_and_one_snapshot()
    {
        var world = new World();
        await Task.WhenAll(world.Refresh(), world.Refresh(), world.Refresh());
        Assert.Equal(1, world.Gateway.Calls);
        Assert.Single(world.Records.Rows);
        Assert.Contains("\"qualityTier\":\"fast\"", world.Gateway.Body);
        Assert.Contains("\"provider\":\"codex-default\"", world.Gateway.Body);
        Assert.Contains("\"requireToolFree\":true", world.Gateway.Body);
        Assert.NotNull(world.Gateway.Provenance);
    }

    [Fact]
    public async Task Unauthorized_confidential_archived_and_special_rows_never_enter_inference()
    {
        var world = new World();
        world.Add("private", "TOP SECRET", confidential: true);
        world.Add("foreign", "SOMEONE ELSE", owner: "other");
        world.Add("archive", "ANCIENT", status: "archived");
        world.Add("live", "LIVE PERSONAL", type: "live");
        foreach (var id in new[] { "private", "foreign", "archive", "live" })
            world.AddMessage(id, $"Excluded opening for {id}");
        await world.Refresh();
        Assert.Empty(world.Messages.Reads);
        Assert.DoesNotContain("Excluded opening", world.Gateway.Body);
        Assert.DoesNotContain("TOP SECRET", world.Gateway.Body);
        Assert.DoesNotContain("SOMEONE ELSE", world.Gateway.Body);
        Assert.DoesNotContain("ANCIENT", world.Gateway.Body);
        Assert.DoesNotContain("LIVE PERSONAL", world.Gateway.Body);
        Assert.Empty((await world.Service().ReadAsync("other", Human("other"), default)).Assignments);
    }

    [Fact]
    public async Task Generic_title_classification_uses_only_bounded_human_context_and_does_not_store_it()
    {
        var world = new World();
        world.Entities.Items.First(e => e.TypeSlug == "discussion").Data["title"] = "Where should this live?";
        world.AddMessage("a", "HIDDEN EVENT", "agent-note");
        world.AddMessage("a", "QUEUED TEXT", "queued-user-message");
        world.AddMessage("a", "ASSISTANT TEXT", role: "assistant");
        world.AddMessage("a", "<nova-context>INJECTED CONTEXT</nova-context> I want to start a story roleplay. " + new string('x', 400) + "TRUNCATED TAIL");
        world.AddMessage("a", "My character is the navigator.");
        world.AddMessage("a", "LATER TURN");
        world.Gateway.Output = SingletonOutput.Replace("Lands of Lore", "Roleplay").Replace("Inventory", "Starting a story");

        await world.Read();
        Assert.Empty(world.Messages.Reads);
        var result = await world.Refresh();
        Assert.Equal("Roleplay", Assert.Single(result.GroupOrder));
        Assert.Equal("Where should this live?", Assert.Single(result.Assignments).SourceTitle);
        Assert.Contains("I want to start a story roleplay", world.Gateway.Body);
        Assert.Contains("My character is the navigator", world.Gateway.Body);
        foreach (var excluded in new[] { "HIDDEN EVENT", "QUEUED TEXT", "ASSISTANT TEXT", "INJECTED CONTEXT", "TRUNCATED TAIL", "LATER TURN" })
            Assert.DoesNotContain(excluded, world.Gateway.Body);
        Assert.DoesNotContain("My character is the navigator", Assert.Single(world.Records.Rows).Data.ToJsonString());
        await world.Read();
        await world.Refresh();
        Assert.Single(world.Messages.Reads);
    }

    [Fact]
    public async Task Rechecks_access_after_reading_context_before_sending_inference()
    {
        var world = new World();
        world.AddMessage("a", "CONTENT MADE PRIVATE DURING READ");
        world.Messages.AfterRead = () => world.Entities.Items.First(e => e.TypeSlug == "discussion").Data["confidential"] = true;
        await world.Refresh();
        Assert.Equal(0, world.Gateway.Calls);
        Assert.Empty(world.Records.Rows);
    }

    [Fact]
    public async Task Rechecks_access_after_inference_and_does_not_store_a_raced_result()
    {
        var world = new World();
        world.Gateway.BeforeReturn = () => world.Entities.Items.First(e => e.TypeSlug == "discussion").Data["confidential"] = true;
        var result = await world.Refresh();
        Assert.Empty(result.Assignments);
        Assert.Empty(world.Records.Rows);
    }

    [Fact]
    public async Task Disabling_grouping_cancels_an_inflight_pass_without_saving_its_result()
    {
        var world = new World();
        using var cancelled = new CancellationTokenSource();
        // Simulate a provider racing cancellation and still returning valid output.
        world.Gateway.BeforeReturn = cancelled.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            world.Service().RefreshAsync("owner", Human(), cancelled.Token));
        Assert.Empty(world.Records.Rows);
        Assert.Equal(0, (await world.Read()).RetryAfterSeconds);

        world.Gateway.BeforeReturn = null;
        var replacement = await world.Refresh();
        Assert.Equal(replacement.Fingerprint, replacement.SourceFingerprint);
        Assert.Equal(2, world.Gateway.Calls);
        Assert.Single(world.Records.Rows);
    }

    [Fact]
    public async Task Newly_confidential_member_invalidates_saved_group_names_on_read()
    {
        var world = new World();
        await world.Refresh();
        world.Entities.Items.First(e => e.TypeSlug == "discussion").Data["confidential"] = true;
        var result = await world.Read();
        Assert.Empty(result.Assignments);
        Assert.Empty(result.GroupOrder);
    }

    [Fact]
    public async Task Malformed_or_failed_model_output_backs_off_without_mutating_discussions()
    {
        var world = new World();
        world.Gateway.Output = "Not JSON";
        var result = await world.Refresh();
        Assert.Empty(result.Assignments);
        Assert.True(result.RetryAfterSeconds > 0);
        await world.Refresh();
        Assert.Equal(1, world.Gateway.Calls);
        Assert.Empty(world.Records.Rows);
    }

    [Fact]
    public async Task Anonymous_endpoints_do_not_read_or_refresh_a_projection()
    {
        var world = new World();
        var context = new DefaultHttpContext();
        var read = await Endpoints.DiscussionGroupingEndpoints.ReadAsync(context, world.Service());
        var refresh = await Endpoints.DiscussionGroupingEndpoints.RefreshAsync(context, world.Service());
        Assert.Equal(401, ((IStatusCodeHttpResult)read).StatusCode);
        Assert.Equal(401, ((IStatusCodeHttpResult)refresh).StatusCode);
        Assert.Equal(0, world.Gateway.Calls);
        Assert.Empty(world.Records.Rows);
    }

    [Fact]
    public async Task Empty_list_skips_inference_and_one_discussion_can_get_a_project()
    {
        var world = new World();
        world.Entities.Items.RemoveAll(e => e.TypeSlug == "discussion" && e.Data["discussion_id"]!.ToString() == "b");
        world.Gateway.Output = SingletonOutput;
        var one = await world.Refresh();
        Assert.Equal(one.Fingerprint, one.SourceFingerprint);
        Assert.Single(one.GroupOrder);
        world.Entities.Items.RemoveAll(e => e.TypeSlug == "discussion");
        var none = await world.Refresh();
        Assert.Equal(none.Fingerprint, none.SourceFingerprint);
        Assert.Empty(none.GroupOrder);
        Assert.Equal(1, world.Gateway.Calls);
    }
    private sealed class World
    {
        public Entities Entities { get; } = new();
        public Records Records { get; } = new();
        public Gateway Gateway { get; } = new();
        public OpeningMessages Messages { get; } = new();
        private readonly string _agentId = Guid.NewGuid().ToString();
        private readonly DiscussionGrouping _service;
        public World()
        {
            Add("a", "Lands of Lore inventory"); Add("b", "Lands of Lore icons");
            Entities.Items.Add(new(Guid.Parse(_agentId), "agent", "nova", "Nova", new JsonObject { ["provider"] = "codex-default" }, Now, Now, null));
            Entities.Items.Add(new(Guid.NewGuid(), "plugin", "nova", "Nova", new JsonObject(), Now, Now, null));
            _service = Service();
        }
        public void Add(string id, string title, string owner = "owner", bool confidential = false, string status = "idle", string type = "chat") => Entities.Items.Add(new(Guid.NewGuid(), "discussion", DiscussionStore.Slug(id), title,
            new JsonObject { ["discussion_id"] = id, ["app"] = "nova", ["title"] = title, ["type"] = type, ["status"] = status, ["owner_id"] = owner, ["agent"] = _agentId, ["confidential"] = confidential }, Now, Now, null));
        public void AddMessage(string id, string content, string source = "user-message", string role = "user")
        {
            var entity = Entities.Items.Single(e => e.Slug == DiscussionStore.Slug(id));
            Messages.Rows.Add(new(Messages.Rows.Count + 1, entity.Id, role, content, new JsonObject { ["source"] = source }, Now));
            entity.Data["message_count"] = Messages.Rows.Count(m => m.DiscussionId == entity.Id);
        }
        public DiscussionGrouping Service() => new(new DiscussionStore(Entities, Messages), Records,
            new AgentDirectory(Entities, new Events()), new RedComputeClient(Gateway), Entities, Messages);
        public Task<DiscussionGroupingView> Read() => _service.ReadAsync("owner", Human(), default);
        public Task<DiscussionGroupingView> Refresh() => _service.RefreshAsync("owner", Human(), default);
    }

    private sealed class Gateway : IComputeGateway
    {
        public int Calls; public string Body = ""; public string Output = DiscussionGroupingTests.Output;
        public Action? BeforeReturn; public ComputeProvenance? Provenance;
        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, ComputeProvenance? provenance = null, CancellationToken ct = default)
        {
            Calls++; Body = await request.Content!.ReadAsStringAsync(ct); Provenance = provenance;
            await Task.Yield(); BeforeReturn?.Invoke();
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { success = true, text = Output }), Encoding.UTF8, "application/json") };
        }
    }
    private sealed class Records : IRecordStreams
    {
        public List<LeafRecord> Rows { get; } = [];
        public Task AppendAsync(string stream, JsonObject data, Guid? entityId = null, string? userId = null, CancellationToken ct = default)
        { Rows.Add(new(Rows.Count + 1, stream, entityId, userId, data, DateTimeOffset.UtcNow)); return Task.CompletedTask; }
        public Task<IReadOnlyList<LeafRecord>> QueryAsync(string stream, RecordQuery? query = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LeafRecord>>(Rows.Where(r => r.Stream == stream && r.UserId == query?.UserId).Reverse().Take(query?.Limit ?? 100).ToArray());
    }
    private sealed class Events : IPluginEvents
    {
        public Task PublishAsync(string type, JsonObject data, CancellationToken ct = default) => throw new InvalidOperationException("Grouping must not publish discussion changes");
        public IDisposable Subscribe(string type, Func<PluginEvent, Task> handler) => new EmptyDisposable();
    }
    private sealed class EmptyDisposable : IDisposable { public void Dispose() { } }
    private sealed class Entities : IEntityStore
    {
        public List<LeafEntity> Items { get; } = [];
        public Task<LeafEntity?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Items.FirstOrDefault(e => e.Id == id));
        public Task<LeafEntity?> GetBySlugAsync(string slug, CancellationToken ct = default) => Task.FromResult(Items.FirstOrDefault(e => e.Slug == slug));
        public Task<IReadOnlyList<LeafEntity>> QueryAsync(EntityQuery query, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LeafEntity>>(Items.Where(e => e.TypeSlug == query.TypeSlug
            && (query.DataEquals is null || query.DataEquals.All(f => e.Data[f.Key]?.ToString() == f.Value?.ToString()))).ToArray());
        public Task<LeafEntity> CreateAsync(string typeSlug, string name, JsonObject? data = null, CancellationToken ct = default) => throw new InvalidOperationException("No entity mutations");
        public Task<LeafEntity> UpsertBySlugAsync(string typeSlug, string slug, string name, JsonObject? data = null, CancellationToken ct = default) => throw new InvalidOperationException("No entity mutations");
        public Task<LeafEntity> PatchAsync(Guid id, JsonObject dataPatch, string? name = null, CancellationToken ct = default) => throw new InvalidOperationException("No entity mutations");
        public Task<LeafEntity> ReplaceDataAsync(Guid id, JsonObject data, string? name = null, CancellationToken ct = default) => throw new InvalidOperationException("No entity mutations");
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => throw new InvalidOperationException("No entity mutations");
    }
    private sealed class OpeningMessages : IDiscussions
    {
        public List<DiscussionMessage> Rows { get; } = [];
        public List<Guid> Reads { get; } = [];
        public Action? AfterRead;
        public Task<LeafEntity> CreateAsync(string? title, string? agentSlugOrId = null, JsonObject? data = null, CancellationToken ct = default) => throw new InvalidOperationException("No transcript access");
        public Task PostAsync(Guid discussionId, string role, string content, JsonObject? metadata = null, string? userId = null, CancellationToken ct = default) => throw new InvalidOperationException("No transcript access");
        public Task<IReadOnlyList<DiscussionMessage>> GetMessagesAsync(Guid discussionId, int limit = 1000, long afterId = 0, CancellationToken ct = default)
        {
            Assert.Equal(12, limit);
            Assert.Equal(0, afterId);
            Reads.Add(discussionId);
            IReadOnlyList<DiscussionMessage> page = Rows.Where(m => m.DiscussionId == discussionId).Take(limit).ToArray();
            AfterRead?.Invoke();
            return Task.FromResult(page);
        }
        public Task<IReadOnlyList<DiscussionMessage>> SearchMessagesAsync(string query, int limit = 1000, CancellationToken ct = default) => throw new InvalidOperationException("No transcript access");
        public Task ClearMessagesAsync(Guid discussionId, CancellationToken ct = default) => throw new InvalidOperationException("No transcript access");
        public Task SetReactionAsync(Guid discussionId, ReactionChange change, CancellationToken ct = default) => throw new InvalidOperationException("No transcript access");
        public Task<IReadOnlyList<LeafRecord>> GetReactionsAsync(Guid discussionId, DateTimeOffset? since = null, int limit = 1000, CancellationToken ct = default) => throw new InvalidOperationException("No transcript access");
        public IDisposable Subscribe(Guid discussionId, Func<DiscussionMessage, Task> onMessage) => throw new InvalidOperationException("No transcript access");
    }
}
