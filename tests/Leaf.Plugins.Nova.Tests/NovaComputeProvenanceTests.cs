using System.Text.Json.Nodes;
using Leaf.Plugins.Nova;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class NovaComputeProvenanceTests
{
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
            null, null, null, null, "active");

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
    }

    [Fact]
    public async Task MissingNovaPluginEntityFailsInsteadOfInventingPresentation()
    {
        var store = new FakeEntityStore();
        var agent = new AgentInfo(
            "agent-id", "nova", "Nova", null, null, "C:\\Nova", null,
            null, null, null, null, "active");

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

        public Task DeleteAsync(Guid id, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
