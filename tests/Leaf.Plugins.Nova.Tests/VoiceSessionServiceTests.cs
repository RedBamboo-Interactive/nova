using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class VoiceSessionServiceTests
{
    [Fact]
    public async Task Discussion_gate_blocks_replacement_until_prior_close_finishes()
    {
        var gate = new VoiceSessionDiscussionGate();
        var closeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var historyRead = false;

        var close = gate.RunAsync("discussion-1", async () =>
        {
            closeStarted.TrySetResult();
            await releaseClose.Task;
            return true;
        }, CancellationToken.None);
        await closeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var replacement = gate.RunAsync("discussion-1", () =>
        {
            historyRead = true;
            return Task.FromResult(true);
        }, CancellationToken.None);

        Assert.False(historyRead);
        Assert.False(replacement.IsCompleted);
        releaseClose.TrySetResult();

        Assert.True(await close);
        Assert.True(await replacement);
        Assert.True(historyRead);
    }

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
    public void Initial_history_recognizes_provider_final_answer_phase()
    {
        var snapshot = new SessionSnapshot("Idle", null, null,
        [
            Message(1, "user", "question", "user-1"),
            Message(2, "assistant", "checking", "assistant-1", "commentary"),
            Message(3, "assistant", "verified answer", "assistant-1", "final_answer"),
        ]);

        var history = VoiceSessionService.BuildInitialHistory(snapshot);

        Assert.Equal("verified answer", history[^1].Content);
    }

    [Fact]
    public void Voice_prompt_preserves_low_latency_conversation_and_delegates_authoritative_work()
    {
        var prompt = VoiceSessionService.BuildVoiceInstructions(Agent(
            description: "A persistent technical collaborator.",
            identity: """
                # Nova
                ## Who you are
                You are Nova. You stick around and give a damn.
                ## How you talk
                Speak naturally, directly, and with warmth.
                ## Repository topology
                This backend-only section must not enter the voice shell.
                """));

        Assert.Contains("# Role & Objective", prompt);
        Assert.Contains("# Personality & Tone", prompt);
        Assert.Contains("You are Nova. You stick around and give a damn.", prompt);
        Assert.Contains("Speak naturally, directly, and with warmth.", prompt);
        Assert.DoesNotContain("backend-only section", prompt);
        Assert.Contains("immediate low-latency spoken flow", prompt);
        Assert.Contains("Handle greetings, simple acknowledgements, social remarks", prompt);
        Assert.Contains("authoritative Nova for identity, durable memory, active work", prompt);
        Assert.Contains("A short reply, correction, refusal, or choice changes or continues backend work", prompt);
        Assert.Contains("do not independently answer the delegated request", prompt);
        Assert.Contains("Do not say that you are checking", prompt);
        Assert.Contains("before the client confirms a real delegation", prompt);
        Assert.Contains("Never claim an action succeeded", prompt);
        Assert.DoesNotContain("You may briefly say that you are checking", prompt);
        Assert.DoesNotContain("any semantic utterance at all", prompt);
        Assert.DoesNotContain("Always delegate before answering", prompt);
    }

    [Fact]
    public void Voice_persona_is_bounded_and_never_copies_backend_instruction_fields()
    {
        var agent = Agent(
            description: "persona-description",
            identity: "## Who you are\nidentity-marker " + new string('x', 10_000),
            outputProtocol: "output-protocol-marker",
            capabilities: "capabilities-marker",
            memoryInstructions: "memory-instructions-marker");

        var persona = VoiceSessionService.BuildVoicePersona(agent);
        var prompt = VoiceSessionService.BuildVoiceInstructions(agent);

        Assert.True(persona.Length <= VoiceSessionService.VoicePersonaMaxChars);
        Assert.Contains("persona-description", persona);
        Assert.Contains("identity-marker", persona);
        Assert.DoesNotContain("output-protocol-marker", prompt);
        Assert.DoesNotContain("capabilities-marker", prompt);
        Assert.DoesNotContain("memory-instructions-marker", prompt);
    }

    [Fact]
    public void Voice_persona_falls_back_to_a_bounded_unstructured_identity()
    {
        var persona = VoiceSessionService.BuildVoicePersona(Agent(
            description: null,
            identity: "unstructured-identity " + new string('y', 8_000)));

        Assert.StartsWith("unstructured-identity", persona);
        Assert.True(persona.Length <= VoiceSessionService.VoicePersonaMaxChars);
    }

    [Fact]
    public void Voice_persona_excludes_motivation_examples_from_the_live_shell()
    {
        var persona = VoiceSessionService.BuildVoicePersona(Agent(
            description: null,
            identity: """
                ## Who you are
                You are Nova.
                ## The motivation voice
                Open the passport and do it together in ten minutes.
                ## How you talk
                Speak naturally and clearly.
                """));

        Assert.Contains("You are Nova.", persona);
        Assert.Contains("Speak naturally and clearly.", persona);
        Assert.DoesNotContain("passport", persona);
        Assert.DoesNotContain("motivation voice", persona, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Delegated_voice_content_carries_recent_direct_turns_without_changing_the_visible_utterance()
    {
        var turns = new[]
        {
            new VoiceSessionService.VoiceCausalTurn(1, "user", "Surprise me", "u1"),
            new VoiceSessionService.VoiceCausalTurn(2, "assistant", "Let's pick one small thing.", "a1"),
        };

        var content = VoiceSessionService.BuildDelegatedVoiceContent("What did you just suggest?", turns);

        Assert.Contains("voice-causal-context", content);
        Assert.Contains("Surprise me", content);
        Assert.Contains("pick one small thing.", content);
        Assert.Contains("<voice-current-utterance>\nWhat did you just suggest?", content.Replace("\r", ""));
    }

    [Fact]
    public void Delegated_voice_content_is_unchanged_when_no_direct_turns_need_bridging()
    {
        Assert.Equal("What is next?", VoiceSessionService.BuildDelegatedVoiceContent(
            "What is next?", Array.Empty<VoiceSessionService.VoiceCausalTurn>()));
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

    private static AgentInfo Agent(string? description, string? identity,
        string? outputProtocol = null, string? capabilities = null, string? memoryInstructions = null) => new(
        Guid.NewGuid().ToString(), "nova", "Nova", description, null, null, identity,
        outputProtocol, capabilities, memoryInstructions, null, null);

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
