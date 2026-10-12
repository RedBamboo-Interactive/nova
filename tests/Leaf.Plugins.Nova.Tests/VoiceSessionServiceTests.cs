using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class VoiceSessionServiceTests
{
    [Fact]
    public void Initial_history_prefers_canonical_assistant_final_and_preserves_turn_order()
    {
        var snapshot = new SessionSnapshot("Idle", null, null,
        [
            Message(1, "user", "question", "user-1"),
            Message(2, "assistant", "working", "assistant-1", "commentary"),
            Message(3, "assistant", "answer", "assistant-1", "final"),
            Message(4, "user", "follow-up", "user-2"),
        ]);

        var history = VoiceSessionService.BuildInitialHistory(snapshot);

        Assert.Collection(history,
            item => { Assert.Equal("user", item.Role); Assert.Equal("question", item.Content); },
            item => { Assert.Equal("assistant", item.Role); Assert.Equal("answer", item.Content); },
            item => { Assert.Equal("user", item.Role); Assert.Equal("follow-up", item.Content); });
    }

    [Fact]
    public void Initial_history_is_bounded_from_the_newest_turns()
    {
        var snapshot = new SessionSnapshot("Idle", null, null,
            Enumerable.Range(1, 30).Select(index =>
                Message(index, index % 2 == 0 ? "assistant" : "user", $"turn-{index}", $"uid-{index}"))
            .ToList());

        var history = VoiceSessionService.BuildInitialHistory(snapshot);

        Assert.Equal(24, history.Count);
        Assert.Equal("turn-7", history[0].Content);
        Assert.Equal("turn-30", history[^1].Content);
    }

    [Fact]
    public async Task Agent_without_a_live_voice_keeps_the_provider_default()
    {
        var agent = Entity("agent", "nova", []);

        var binding = await VoiceSessionVoiceResolver.ResolveAsync(new FakeEntityStore(agent), Info(agent));

        Assert.Null(binding);
    }

    [Fact]
    public async Task Agent_live_voice_resolves_its_provider_and_opaque_voice_id()
    {
        var provider = Entity("provider", "openai-live", new JsonObject
        {
            ["capabilities"] = new JsonArray("voice-session"),
        });
        var voice = Entity("voice-session-voice", "openai-live-marin", new JsonObject
        {
            ["provider"] = provider.Id.ToString(),
            ["provider_voice_id"] = "marin",
        });
        var agent = Entity("agent", "nova", new JsonObject
        {
            [VoiceSessionVoiceResolver.AgentField] = voice.Id.ToString(),
        });

        var binding = await VoiceSessionVoiceResolver.ResolveAsync(
            new FakeEntityStore(agent, voice, provider), Info(agent));

        Assert.NotNull(binding);
        Assert.Equal("openai-live", binding.ProviderSlug);
        Assert.Equal("marin", binding.ProviderVoiceId);
    }

    [Fact]
    public async Task Configured_live_voice_rejects_a_provider_without_voice_session_support()
    {
        var provider = Entity("provider", "tts-only", new JsonObject
        {
            ["capabilities"] = new JsonArray("tts"),
        });
        var voice = Entity("voice-session-voice", "wrong-voice", new JsonObject
        {
            ["provider"] = provider.Slug,
            ["provider_voice_id"] = "voice-key",
        });
        var agent = Entity("agent", "nova", new JsonObject
        {
            [VoiceSessionVoiceResolver.AgentField] = voice.Slug,
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            VoiceSessionVoiceResolver.ResolveAsync(new FakeEntityStore(agent, voice, provider), Info(agent)));

        Assert.Contains("does not support Voice Session", error.Message);
    }

    [Fact]
    public async Task Configured_missing_live_voice_fails_instead_of_silently_using_the_default()
    {
        var agent = Entity("agent", "nova", new JsonObject
        {
            [VoiceSessionVoiceResolver.AgentField] = "missing-voice",
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            VoiceSessionVoiceResolver.ResolveAsync(new FakeEntityStore(agent), Info(agent)));

        Assert.Contains("was not found", error.Message);
    }

    private static SessionMessage Message(long id, string role, string content, string uid, string? phase = null)
        => new() { Id = id, Sequence = id, Role = role, EventType = "text", Content = content,
            MessageUid = uid, Phase = phase };

    private static AgentInfo Info(LeafEntity agent) => new(
        agent.Id.ToString(), agent.Slug, agent.Name, null, null, null, null, null, null, null, null, null);

    private static LeafEntity Entity(string typeSlug, string slug, JsonObject data) => new(
        Guid.NewGuid(), typeSlug, slug, slug, data, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "system");

    private sealed class FakeEntityStore(params LeafEntity[] entities) : IEntityStore
    {
        public Task<LeafEntity?> GetAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(entities.FirstOrDefault(entity => entity.Id == id));

        public Task<LeafEntity?> GetBySlugAsync(string slug, CancellationToken ct = default)
            => Task.FromResult(entities.FirstOrDefault(entity => entity.Slug == slug));

        public Task<LeafEntity?> GetBySlugAsync(string typeSlug, string slug, CancellationToken ct = default)
            => Task.FromResult(entities.FirstOrDefault(entity => entity.TypeSlug == typeSlug && entity.Slug == slug));

        public Task<IReadOnlyList<LeafEntity>> QueryAsync(EntityQuery query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LeafEntity>>(entities
                .Where(entity => entity.TypeSlug == query.TypeSlug)
                .Skip(query.Offset).Take(query.Limit).ToList());

        public Task<LeafEntity> CreateAsync(string typeSlug, string name, JsonObject? data = null,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LeafEntity> UpsertBySlugAsync(string typeSlug, string slug, string name,
            JsonObject? data = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LeafEntity> PatchAsync(Guid id, JsonObject dataPatch, string? name = null,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LeafEntity> ReplaceDataAsync(Guid id, JsonObject data, string? name = null,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
