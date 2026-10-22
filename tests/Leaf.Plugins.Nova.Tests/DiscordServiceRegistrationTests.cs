using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class DiscordServiceRegistrationTests
{
    [Fact]
    public void External_conversation_services_resolve_with_the_plugin_keyed_entity_store()
    {
        var plugin = new NovaAppPlugin();
        var services = new ServiceCollection();
        plugin.ConfigureServices(services, new PluginContext
        {
            Manifest = plugin.Manifest,
            PluginDirectory = AppContext.BaseDirectory,
            DevMode = true,
        });

        services.AddLogging();
        services.AddKeyedSingleton<IEntityStore>(NovaAppPlugin.PluginId, new NoIoEntityStore());
        services.AddSingleton<IAgentScratchSpace>(new NoIoScratchSpace());
        // Provided by cap-sentinel, which Nova's manifest requires.
        services.AddSingleton<IUntrustedContentReview>(new NoIoSentinel());
        services.AddSingleton(Uninitialized<RedComputeClient>());
        services.AddSingleton(Uninitialized<AgentDirectory>());
        services.AddSingleton(Uninitialized<MessagePipeline>());

        var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<DiscordPromptInjectionVerifier>());
        var external = provider.GetRequiredService<ExternalAgentConversationProvider>();
        Assert.Same(external, provider.GetRequiredService<IExternalAgentConversationProvider>());
    }

    [Fact]
    public void Nova_requires_the_sentinel_capability_that_provides_the_shared_shield()
        => Assert.Contains("sentinel", new NovaAppPlugin().Manifest.Requires);

    private static T Uninitialized<T>() where T : class
        => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    private sealed class NoIoSentinel : IUntrustedContentReview
    {
        public Task<UntrustedContentReviewResult> ReviewAsync(
            UntrustedContentReviewRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public UntrustedContentScreening Screen(
            string content, UntrustedContentFormat format = UntrustedContentFormat.Text,
            string? ownerDisplayName = null)
            => throw new NotSupportedException();
    }

    private sealed class NoIoScratchSpace : IAgentScratchSpace
    {
        public AgentScratchAllocation PrepareExecution(string owner, string executionKey)
            => throw new NotSupportedException();
    }

    private sealed class NoIoEntityStore : IEntityStore
    {
        public Task<LeafEntity?> GetAsync(Guid id, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<LeafEntity?> GetBySlugAsync(string slug, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<LeafEntity>> QueryAsync(EntityQuery query, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<LeafEntity> CreateAsync(string typeSlug, string name, JsonObject? data = null,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LeafEntity> UpsertBySlugAsync(string typeSlug, string slug, string name,
            JsonObject? data = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LeafEntity> PatchAsync(Guid id, JsonObject dataPatch, string? name = null,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LeafEntity> ReplaceDataAsync(Guid id, JsonObject data, string? name = null,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
