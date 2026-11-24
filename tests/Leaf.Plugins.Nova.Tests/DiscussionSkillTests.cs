using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Plugins.Nova.Endpoints;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

// Skills attached to one discussion on demand: who may attach what, the approval card, the note the
// Agent acts on, the scratch copy, and that nothing leaks into another discussion.
public sealed class DiscussionSkillTests : IDisposable
{
    private const string Owner = "test-owner";
    private readonly string scratchRoot = Path.Combine(Path.GetTempPath(), "nova-skills-" + Guid.NewGuid().ToString("N"));
    private readonly LeafEntity optional = Skill("mail", "Mail", "Read the owner's mail.", "Use the Mail extension.",
        new JsonArray(new JsonObject { ["path"] = "references/api.md", ["kind"] = "text", ["content"] = "GET /mail" }));
    private readonly LeafEntity other = Skill("outfit", "Outfit", "Daily outfit.", "Pick an outfit.");

    public void Dispose() { try { Directory.Delete(scratchRoot, true); } catch { } }

    private static LeafEntity Skill(string slug, string name, string description, string instructions, JsonArray? files = null)
        => new(Guid.NewGuid(), "agent-skill", slug, name, new JsonObject
        {
            ["description"] = description, ["instructions"] = instructions, ["files"] = files ?? new JsonArray(),
        }, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "system");

    private sealed class Scratch(string root) : IAgentScratchSpace
    {
        public AgentScratchAllocation PrepareExecution(string owner, string executionKey)
        {
            var path = Path.Combine(root, owner, executionKey);
            Directory.CreateDirectory(path);
            return new AgentScratchAllocation(path, new Dictionary<string, string>());
        }
    }

    private sealed class World : IDisposable
    {
        public CallbackDeliveryTests.Fixture F { get; }
        public DiscussionSkillService Service { get; }
        public List<string> Published { get; } = [];
        public IPluginEvents Events { get; }

        public World(string scratchRoot, LeafEntity optional, LeafEntity other)
        {
            F = new CallbackDeliveryTests.Fixture(extraEntities: [optional, other]);
            F.Entities.PatchAsync(F.AgentId, new JsonObject { ["optional_skills"] = new JsonArray(optional.Slug) }).GetAwaiter().GetResult();
            Service = new DiscussionSkillService(F.Store, F.Entities, F.Agents, new Scratch(scratchRoot), F.Injector,
                NullLogger<DiscussionSkillService>.Instance);
            Events = CallbackDeliveryTests.Proxy.Create<IPluginEvents>((method, args) =>
            {
                if (method == "PublishAsync") Published.Add((string)args[0]!);
                return Task.CompletedTask;
            });
        }

        public Task<DiscussionRead> Target() => F.Store.GetAsync("target")!;

        public void Dispose() => F.Dispose();
    }

    private World NewWorld() => new(scratchRoot, optional, other);

    private static ClaimsPrincipal Signed(object identity) => new(new ClaimsIdentity(
    [
        new Claim("sub", Owner),
        new Claim("name", "Laurent Becherel"),
        new Claim("token_use", "execution"),
        new Claim("execution_identity", JsonSerializer.Serialize(identity)),
    ], "test"));

    private static ClaimsPrincipal Person() => Signed(new
    {
        app = new { id = "nova", name = "Nova" },
        actor = new { kind = "app", id = "nova", name = "Nova" },
        beneficiary = new { kind = "user", id = Owner },
        context = new[] { new { kind = "browser", route = "/apps/nova" } },
    });

    // The loopback browser: local-user, signed as the Nova app at a browser, benefiting the system.
    private static ClaimsPrincipal LoopbackPerson() => new(new ClaimsIdentity(
    [
        new Claim("sub", "local-user"),
        new Claim("name", "Local User"),
        new Claim("token_use", "execution"),
        new Claim("execution_identity", JsonSerializer.Serialize(new
        {
            app = new { id = "nova", name = "Nova" },
            actor = new { kind = "app", id = "nova", name = "Nova" },
            beneficiary = new { kind = "system", reason = "Browser work requested through the loopback LocalDefault identity" },
            context = new[] { new { kind = "browser", route = "/apps/nova" } },
        })),
    ], "test"));

    private static ClaimsPrincipal Agent(World world, string? entityId = null) => Signed(new
    {
        app = new { id = "nova", name = "Nova" },
        actor = new { kind = "agent", id = "nova", entityId = entityId ?? world.F.AgentId.ToString(), name = "Nova" },
        beneficiary = new { kind = "user", id = Owner },
        context = new[] { new { kind = "discussion", id = "target" } },
    });

    // The kernel filter stamps who owns the installation on every request; local-user stands for them.
    private static HttpContext Context(ClaimsPrincipal user)
    {
        var ctx = new DefaultHttpContext { User = user };
        ctx.Items[InstallationOwner.RequestItem] = Owner;
        return ctx;
    }

    private static (int Status, JsonElement Body) Read(IResult result)
        => (((IStatusCodeHttpResult)result).StatusCode ?? 200,
            JsonSerializer.SerializeToElement(((IValueHttpResult)result).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    private static async Task<(int Status, JsonElement Body)> Attach(World w, ClaimsPrincipal user, string skill, string? reason = null)
        => Read(await DiscussionSkillEndpoints.AttachAsync(await w.Target(), new AttachSkillRequest { Skill = skill, Reason = reason },
            Context(user), w.Service, w.Events));

    private static async Task<(int Status, JsonElement Body)> Decide(World w, ClaimsPrincipal user, string requestId, string decision)
        => Read(await DiscussionSkillEndpoints.DecideAsync(await w.Target(), requestId, new SkillRequestDecision { Decision = decision },
            Context(user), w.Service, w.F.Store, w.Events));

    private static IEnumerable<string> Notes(World w)
        => w.F.TargetMessages.Where(m => m.Role == "user").Select(m => m.Content);

    [Fact]
    public async Task The_person_attaches_any_library_skill_and_the_agent_gets_its_whole_skill_md()
    {
        using var w = NewWorld();

        var (status, body) = await Attach(w, Person(), "outfit");

        Assert.Equal(200, status);
        Assert.Equal("attached", body.GetProperty("status").GetString());
        var attached = Assert.Single((await w.Target()).AttachedSkills);
        Assert.Equal(("outfit", CapabilityAttachment.ByUser, Owner), (attached.Slug, attached.AttachedBy, attached.AttachedById));
        var file = body.GetProperty("skillFile").GetString()!;
        Assert.Contains("Pick an outfit.", File.ReadAllText(file));
        var note = Assert.Single(Notes(w));
        Assert.Contains("Laurent attached the Skill \"Outfit\"", note);
        Assert.Contains("name: \"outfit\"", note);
        Assert.Contains("Pick an outfit.", note);
        Assert.Equal(["discussion.changed"], w.Published);
    }

    [Fact]
    public async Task A_loopback_browser_is_recorded_and_named_as_the_discussions_owner()
    {
        using var w = NewWorld();

        var (status, _) = await Attach(w, LoopbackPerson(), "outfit");

        Assert.Equal(200, status);
        var attached = Assert.Single((await w.Target()).AttachedSkills);
        Assert.Equal((CapabilityAttachment.ByUser, Owner), (attached.AttachedBy, attached.AttachedById));
        Assert.StartsWith("Your owner attached the Skill \"Outfit\"", Assert.Single(Notes(w)));
    }

    [Fact]
    public async Task The_agent_attaches_an_optional_skill_itself_with_its_package_files()
    {
        using var w = NewWorld();

        var (status, body) = await Attach(w, Agent(w), "mail");

        Assert.Equal(200, status);
        var attached = Assert.Single((await w.Target()).AttachedSkills);
        Assert.Equal(CapabilityAttachment.ByAgent, attached.AttachedBy);
        var folder = Path.GetDirectoryName(body.GetProperty("skillFile").GetString()!)!;
        Assert.Equal("GET /mail", File.ReadAllText(Path.Combine(folder, "references", "api.md")));
        Assert.StartsWith("You attached the Skill \"Mail\"", Assert.Single(Notes(w)));
    }

    [Fact]
    public async Task Attaching_again_changes_nothing_and_sends_no_second_note()
    {
        using var w = NewWorld();
        await Attach(w, Person(), "outfit");

        var (status, body) = await Attach(w, Agent(w), "outfit");

        Assert.Equal(200, status);
        Assert.False(body.GetProperty("attached").GetBoolean());
        Assert.Equal(CapabilityAttachment.ByUser, Assert.Single((await w.Target()).AttachedSkills).AttachedBy);
        Assert.Single(Notes(w));
    }

    [Fact]
    public async Task A_skill_off_the_optional_list_becomes_one_pending_request_and_attaches_nothing()
    {
        using var w = NewWorld();

        var (status, body) = await Attach(w, Agent(w), "outfit", "Laurent asked what to wear");
        var (again, second) = await Attach(w, Agent(w), "outfit");

        Assert.Equal(202, status);
        Assert.Equal(202, again);
        Assert.Equal("approval_requested", body.GetProperty("status").GetString());
        Assert.Equal(body.GetProperty("request").GetProperty("id").GetString(), second.GetProperty("request").GetProperty("id").GetString());
        var target = await w.Target();
        Assert.Empty(target.AttachedSkills);
        var request = Assert.Single(target.CapabilityRequests!);
        Assert.Equal((CapabilityRequest.Pending, "Laurent asked what to wear"), (request.Status, request.Reason));
        Assert.Empty(Notes(w));
    }

    [Fact]
    public async Task The_person_approves_and_the_agent_gets_the_skill()
    {
        using var w = NewWorld();
        var (_, asked) = await Attach(w, Agent(w), "outfit");
        var id = asked.GetProperty("request").GetProperty("id").GetString()!;

        var (status, _) = await Decide(w, Person(), id, "approve");

        Assert.Equal(200, status);
        var target = await w.Target();
        var attached = Assert.Single(target.AttachedSkills);
        Assert.Equal((CapabilityAttachment.ByAgent, Owner), (attached.AttachedBy, attached.ApprovedById));
        Assert.Equal(CapabilityRequest.Approved, Assert.Single(target.CapabilityRequests!).Status);
        Assert.Contains("Laurent attached the Skill \"Outfit\"", Assert.Single(Notes(w)));
        var (settled, _) = await Decide(w, Person(), id, "approve");
        Assert.Equal(409, settled);
    }

    [Fact]
    public async Task Only_a_person_decides_a_request()
    {
        using var w = NewWorld();
        var (_, asked) = await Attach(w, Agent(w), "outfit");
        var id = asked.GetProperty("request").GetProperty("id").GetString()!;

        var (status, body) = await Decide(w, Agent(w), id, "approve");

        Assert.Equal(403, status);
        Assert.Equal("decision_requires_person", body.GetProperty("error").GetString());
        Assert.Empty((await w.Target()).AttachedSkills);
    }

    [Fact]
    public async Task Declining_tells_the_agent_and_attaches_nothing()
    {
        using var w = NewWorld();
        var (_, asked) = await Attach(w, Agent(w), "outfit");
        var id = asked.GetProperty("request").GetProperty("id").GetString()!;

        var (status, _) = await Decide(w, Person(), id, "decline");

        Assert.Equal(200, status);
        var target = await w.Target();
        Assert.Empty(target.AttachedSkills);
        Assert.Equal(CapabilityRequest.Declined, Assert.Single(target.CapabilityRequests!).Status);
        Assert.Contains("Laurent declined your request to attach the Skill \"Outfit\"", Assert.Single(Notes(w)));
    }

    [Fact]
    public async Task Another_agent_cannot_change_this_discussions_skills()
    {
        using var w = NewWorld();

        var (status, body) = await Attach(w, Agent(w, Guid.NewGuid().ToString()), "mail");

        Assert.True(status is 403 or 404, $"status {status}");
        Assert.Empty((await w.Target()).AttachedSkills);
    }

    [Fact]
    public async Task An_unknown_skill_is_a_404_naming_the_library()
    {
        using var w = NewWorld();

        var (status, body) = await Attach(w, Person(), "no-such-skill");

        Assert.Equal(404, status);
        Assert.Contains("library=true", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Detach_removes_the_copy_and_tells_the_agent_to_stop()
    {
        using var w = NewWorld();
        var (_, body) = await Attach(w, Person(), "mail");
        var folder = Path.GetDirectoryName(body.GetProperty("skillFile").GetString()!)!;

        var result = await w.Service.DetachAsync(await w.Target(), "mail", "Laurent", default);

        Assert.NotNull(result!.Value.Removed);
        Assert.Empty((await w.Target()).AttachedSkills);
        Assert.False(Directory.Exists(folder));
        Assert.Contains("Stop following it here", Notes(w).Last());
        Assert.Null((await w.Service.DetachAsync(await w.Target(), "mail", "Laurent", default))!.Value.Removed);
    }

    [Fact]
    public async Task Another_discussion_of_the_same_agent_sees_none_of_it()
    {
        using var w = NewWorld();
        await Attach(w, Person(), "outfit");

        var live = (await w.F.Store.GetAsync("live"))!;

        Assert.Empty(live.AttachedSkills);
        Assert.Empty(await w.Service.PrepareTurnAsync(live, default));
        Assert.Single(await w.Service.PrepareTurnAsync(await w.Target(), default));
    }

    [Fact]
    public async Task After_a_restart_and_a_cleared_scratch_the_next_turn_writes_it_back_and_names_it()
    {
        using var w = NewWorld();
        var (_, body) = await Attach(w, Person(), "mail");
        var file = body.GetProperty("skillFile").GetString()!;
        Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);

        // A new process has only the stored discussion entity.
        var reloaded = (await w.F.Store.GetAsync("target"))!;
        var lines = await w.Service.PrepareTurnAsync(reloaded, default);

        Assert.True(File.Exists(file));
        var line = Assert.Single(lines);
        Assert.StartsWith("Attached Skill: Mail (mail), at ", line);
        Assert.Contains("attached-skills/mail/SKILL.md", line);
    }

    [Fact]
    public async Task The_turn_context_names_every_attached_skill()
    {
        var lines = new[] { "Attached Skill: Mail (mail), at /s/attached-skills/mail/SKILL.md. Follow it in this discussion." };
        var snapshot = NovaContextBuilder.BuildSnapshot([], [], null, null);

        var full = NovaContextBuilder.BuildFullContext(snapshot, "target", DateTime.UtcNow, ResolvedDeviceFor(), "text", "Nova", attachedSkillLines: lines);
        var delta = NovaContextBuilder.BuildDeltaContext(snapshot, snapshot, "target", DateTime.UtcNow, ResolvedDeviceFor(), "text", "Nova", attachedSkillLines: lines);

        Assert.Contains(lines[0], full);
        Assert.Contains(lines[0], delta);
        await Task.CompletedTask;
    }

    private static ResolvedDevice ResolvedDeviceFor() => new() { Name = "API", Type = "api", Platform = "api" };

    [Fact]
    public async Task A_package_path_that_leaves_the_folder_is_never_written()
    {
        var skill = Skill("evil", "Evil", "x", "y", new JsonArray(
            new JsonObject { ["path"] = "../../escaped.txt", ["kind"] = "text", ["content"] = "no" },
            new JsonObject { ["path"] = "ok.txt", ["kind"] = "text", ["content"] = "yes" }));
        var root = Path.Combine(scratchRoot, "guard");

        var file = await DiscussionSkills.MaterializeAsync(skill, root, (_, _) => Task.FromResult<byte[]?>(null));

        Assert.Equal("yes", File.ReadAllText(Path.Combine(Path.GetDirectoryName(file)!, "ok.txt")));
        Assert.False(File.Exists(Path.Combine(root, "escaped.txt")));
        Assert.False(File.Exists(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../../escaped.txt"))));
    }

    [Fact]
    public async Task Asset_files_come_from_the_asset_store_and_an_unchanged_skill_is_not_rewritten()
    {
        var skill = Skill("art", "Art", "x", "y", new JsonArray(
            new JsonObject { ["path"] = "ref.png", ["kind"] = "asset", ["asset_url"] = "/api/assets/abc.png" }));
        var root = Path.Combine(scratchRoot, "assets");
        var reads = new List<string>();
        Task<byte[]?> ReadAsset(string id, CancellationToken _) { reads.Add(id); return Task.FromResult<byte[]?>([1, 2, 3]); }

        var file = await DiscussionSkills.MaterializeAsync(skill, root, ReadAsset);
        await DiscussionSkills.MaterializeAsync(skill, root, ReadAsset);
        await DiscussionSkills.MaterializeAsync(skill with { UpdatedAt = skill.UpdatedAt.AddMinutes(1) }, root, ReadAsset);

        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(file)!, "ref.png")));
        Assert.Equal(["abc.png", "abc.png"], reads);
    }

    [Fact]
    public void Settled_requests_are_pruned_but_pending_ones_always_stay()
    {
        var requests = Enumerable.Range(0, 14).Select(i => new CapabilityRequest($"r{i}", "skill", "e", "s", "S", null, "a", null,
            DateTimeOffset.UtcNow, i == 0 ? CapabilityRequest.Pending : CapabilityRequest.Declined)).ToList();

        var kept = CapabilityRequest.Read(new JsonObject { [CapabilityRequest.DataKey] = CapabilityRequest.ToJson(requests) });

        Assert.Equal(1 + CapabilityRequest.KeptSettled, kept.Count);
        Assert.Equal("r0", kept[0].Id);
        Assert.Equal("r13", kept[^1].Id);
    }
}
