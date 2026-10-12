using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Nova;

internal sealed record VoiceSessionVoiceBinding(string ProviderSlug, string ProviderVoiceId);

internal static class VoiceSessionVoiceResolver
{
    internal const string AgentField = "voice_session_voice";
    internal const string VoiceType = "voice-session-voice";

    public static async Task<VoiceSessionVoiceBinding?> ResolveAsync(
        IEntityStore store, AgentInfo agent, CancellationToken ct = default)
    {
        var agentEntity = Guid.TryParse(agent.Id, out var agentId)
            ? await store.GetAsync(agentId, ct)
            : await store.GetBySlugAsync("agent", agent.Slug, ct);
        if (agentEntity?.TypeSlug != "agent")
            throw new InvalidOperationException($"Agent '{agent.Name}' could not be read for live voice configuration");

        var voiceReference = String(agentEntity.Data, AgentField);
        if (string.IsNullOrWhiteSpace(voiceReference)) return null;

        var voice = await ResolveReferenceAsync(store, voiceReference, VoiceType, ct)
            ?? throw new InvalidOperationException($"Agent '{agent.Name}' references a Live Voice that was not found");
        var providerReference = String(voice.Data, "provider");
        if (string.IsNullOrWhiteSpace(providerReference))
            throw new InvalidOperationException($"Live Voice '{voice.Name}' requires a Provider");

        var provider = await ResolveReferenceAsync(store, providerReference, "provider", ct)
            ?? throw new InvalidOperationException($"Live Voice '{voice.Name}' references a Provider that was not found");
        if (!HasCapability(provider, "voice-session"))
            throw new InvalidOperationException($"Live Voice '{voice.Name}' Provider '{provider.Name}' does not support Voice Session");

        var providerVoiceId = String(voice.Data, "provider_voice_id");
        if (string.IsNullOrWhiteSpace(providerVoiceId))
            throw new InvalidOperationException($"Live Voice '{voice.Name}' requires a Provider Voice ID");

        return new(provider.Slug, providerVoiceId);
    }

    private static async Task<LeafEntity?> ResolveReferenceAsync(
        IEntityStore store, string reference, string typeSlug, CancellationToken ct)
    {
        var entity = Guid.TryParse(reference, out var id)
            ? await store.GetAsync(id, ct)
            : await store.GetBySlugAsync(typeSlug, reference, ct);
        return entity?.TypeSlug == typeSlug ? entity : null;
    }

    private static bool HasCapability(LeafEntity provider, string capability)
        => provider.Data["capabilities"] is JsonArray capabilities
           && capabilities.Any(entry => entry is JsonValue value
               && value.TryGetValue<string>(out var item)
               && string.Equals(item, capability, StringComparison.OrdinalIgnoreCase));

    private static string? String(JsonObject data, string key)
        => data[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
