using System.Text.Json.Nodes;
using Leaf.Plugins.Nova;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class NovaComputeProvenanceTests
{
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
