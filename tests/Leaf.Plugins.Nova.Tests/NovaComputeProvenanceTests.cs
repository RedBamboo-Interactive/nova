using System.Security.Claims;
using System.Text.Json.Nodes;
using Leaf.Plugins.Nova;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class NovaComputeProvenanceTests
{
    private const string Owner = "00000000-0000-4000-8000-000000000001";

    [Fact]
    public void The_owner_on_their_own_page_is_the_person_who_caused_it()
        => Assert.Equal(ComputeCauses.Beneficiary, Cause(Plain(Owner, browser: true), Owner));

    [Fact]
    public void A_person_who_is_not_the_owner_is_someone_else()
        => Assert.Equal(ComputeCauses.OtherPerson, Cause(Plain("someone-else", browser: true), Owner));

    [Fact]
    public void The_first_real_person_on_a_local_discussion_becomes_its_owner()
        => Assert.Equal(ComputeCauses.Beneficiary, Cause(Plain(Owner, browser: true), "local-user"));

    [Fact]
    public void A_script_holding_the_owners_token_is_not_the_owner_typing()
        => Assert.Null(Cause(Plain(Owner, browser: false), Owner));

    [Fact]
    public void A_browser_token_on_its_page_is_the_person_and_an_Agents_token_is_the_Agent()
    {
        const string browserToken =
            """{"actor":{"kind":"app"},"beneficiary":{"kind":"user"},"context":[{"kind":"browser"}]}""";
        var onItsPage = Execution(Owner, browserToken);
        onItsPage.Request.Headers["Sec-Fetch-Site"] = "same-origin";
        Assert.Equal(ComputeCauses.Beneficiary, Cause(onItsPage, Owner));
        // The same token replayed from a backend is nobody clicking.
        Assert.Null(Cause(Execution(Owner, browserToken), Owner));
        Assert.Equal(ComputeCauses.Agent, Cause(Execution(Owner,
            """{"actor":{"kind":"agent"},"context":[{"kind":"job-cause","id":"agent"}]}"""), Owner));
        // A session process token issued before causes were recorded is still its Agent.
        Assert.Equal(ComputeCauses.Agent, Cause(Execution(Owner,
            """{"actor":{"kind":"agent"},"context":[]}"""), Owner));
        Assert.Null(Cause(Execution(Owner, """{"actor":{"kind":"app"},"context":[]}"""), Owner));
    }

    [Fact]
    public async Task Provenance_carries_the_cause_it_was_given_and_none_otherwise()
    {
        var store = new FakeEntityStore(Entity(Guid.NewGuid(), "plugin", "nova", "Nova", new JsonObject()));
        var agent = new AgentInfo("agent-id", "nova", "Nova", null, null, "C:\\Nova", null, null, null, null, null);
        var beneficiary = new ComputeBeneficiary("system", Reason: "test");

        Assert.Equal(ComputeCauses.Agent, (await NovaComputeProvenance.CreateAsync(
            store, agent, beneficiary, "/test", [], cause: ComputeCauses.Agent)).Cause);
        Assert.Null((await NovaComputeProvenance.CreateAsync(store, agent, beneficiary, "/test", [])).Cause);
    }

    private static string? Cause(DefaultHttpContext context, string? owner)
        => NovaComputeProvenance.RequestCause(context, owner);

    private static DefaultHttpContext Plain(string subject, bool browser)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", subject)], "Bearer")),
        };
        if (browser) context.Request.Headers["Sec-Fetch-Site"] = "same-origin";
        return context;
    }

    private static DefaultHttpContext Execution(string subject, string identity) => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", subject),
            new Claim("token_use", "execution"),
            new Claim("execution_identity", identity),
        ], "Bearer")),
    };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("local-user")]
    public async Task LocalDiscussionUsesExplicitSystemBeneficiary(string? ownerId)
    {
        var beneficiary = await NovaComputeProvenance.ResolveBeneficiaryAsync(
            new FakeEntityStore(), ownerId);

        Assert.Equal("system", beneficiary.Kind);
        Assert.Equal("Nova work requested through an unauthenticated local discussion",
            beneficiary.Reason);
    }

    [Fact]
    public async Task CapturesNovaPresentationFromPluginAndColorEntities()
    {
        var pluginId = Guid.NewGuid();
        var colorId = Guid.NewGuid();
        var store = new FakeEntityStore(
            Entity(pluginId, "plugin", "nova", "Nova", new JsonObject
            {
                ["icon"] = "ph-star",
                ["color"] = colorId.ToString(),
            }),
            Entity(colorId, "color", "nova", "Nova Magenta", new JsonObject
            {
                ["hex"] = "#C74B7A",
            }));
        var agent = new AgentInfo(
            "agent-id", "nova", "Nova", null, "avatar.png", "C:\\Nova", null,
            null, null, null, null);

        var provenance = await NovaComputeProvenance.CreateAsync(
            store,
            agent,
            new ComputeBeneficiary("system", Reason: "test"),
            "/test",
            []);

        Assert.Equal("nova", provenance.Origin.App.Id);
        Assert.Equal(pluginId.ToString(), provenance.Origin.App.EntityId);
        Assert.Equal("Nova", provenance.Origin.App.NameSnapshot);
        Assert.Equal("ph-star", provenance.Origin.App.IconSnapshot);
        Assert.Equal("#C74B7A", provenance.Origin.App.ColorSnapshot);
        Assert.Equal("agent", provenance.Actor.Kind);
        Assert.Equal("agent-id", provenance.Actor.EntityId);
        Assert.Equal("nova", provenance.Actor.Id);
        Assert.Equal("Nova", provenance.Actor.NameSnapshot);
        Assert.Equal("/api/assets/avatar.png", provenance.Actor.AvatarSnapshot);
    }

    [Fact]
    public async Task MissingNovaPluginEntityFailsInsteadOfInventingPresentation()
    {
        var store = new FakeEntityStore();
        var agent = new AgentInfo(
            "agent-id", "nova", "Nova", null, null, "C:\\Nova", null,
            null, null, null, null);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NovaComputeProvenance.CreateAsync(
                store,
                agent,
                new ComputeBeneficiary("system", Reason: "test"),
                "/test",
                []));

        Assert.Equal("Nova plugin entity is missing", error.Message);
    }

    private static LeafEntity Entity(
        Guid id, string type, string slug, string name, JsonObject data)
        => new(id, type, slug, name, data, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "system");

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
            string typeSlug, string slug, string name, JsonObject? data = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<LeafEntity> PatchAsync(
            Guid id, JsonObject dataPatch, string? name = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<LeafEntity> ReplaceDataAsync(
            Guid id, JsonObject data, string? name = null, CancellationToken ct = default)
        {
            var index = Array.FindIndex(entities, entity => entity.Id == id);
            if (index < 0) throw new KeyNotFoundException($"Entity '{id}' was not found.");

            var existing = entities[index];
            var replacement = existing with
            {
                Data = data.DeepClone() as JsonObject ?? new JsonObject(),
                Name = name ?? existing.Name,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            entities[index] = replacement;
            return Task.FromResult(replacement);
        }

        public Task DeleteAsync(Guid id, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
