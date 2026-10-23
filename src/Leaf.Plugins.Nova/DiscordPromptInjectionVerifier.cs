using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging;

namespace Leaf.Plugins.Nova;

public sealed record DiscordInjectionReview(
    string Risk,
    IReadOnlyList<string> Signals,
    string Handling,
    bool Available)
{
    public static DiscordInjectionReview Unavailable { get; } =
        new("unknown", ["verifier-unavailable"], "treat_as_untrusted_data", false);
}

/// <summary>
/// Nova's adapter onto the shared Sentinel (<see cref="IUntrustedContentReview"/>, provided by
/// cap-sentinel). It keeps the Discord and Leaf Commons review semantics Nova has always had:
/// the binding's Agent (which only names the reviewer; the Sentinel chooses its own compute),
/// Nova's review provenance, and the four-field <see cref="DiscordInjectionReview"/>
/// whose JSON shape leaf-discord's projection parses. The capability's isolation report is
/// logged here; it is not part of that wire shape.
/// <para>
/// cap-sentinel is a soft dependency. When it is not installed (or is disabled, or incompatible
/// with the kernel) every review is <see cref="DiscordInjectionReview.Unavailable"/> and the
/// message is still admitted, exactly as when the old in-process verifier could not run.
/// </para>
/// </summary>
public sealed class DiscordPromptInjectionVerifier(
    IUntrustedContentReview? sentinel,
    AgentDirectory agents,
    IEntityStore entities,
    ILogger<DiscordPromptInjectionVerifier> logger)
{
    private int missingSentinelLogged;

    public async Task<DiscordInjectionReview> ReviewAsync(
        string agentReference,
        string ownerUserId,
        string bindingId,
        long generation,
        string content,
        string transport = "discord",
        string? requestId = null,
        CancellationToken ct = default)
    {
        if (sentinel is null)
        {
            if (Interlocked.Exchange(ref missingSentinelLogged, 1) == 0)
                logger.LogWarning(
                    "The sentinel capability (cap-sentinel) is not installed; Discord and Leaf Commons messages are admitted with an unavailable prompt-injection review");
            return DiscordInjectionReview.Unavailable;
        }

        var agent = (await agents.GetAgentsAsync(ct: ct)).FirstOrDefault(candidate =>
            candidate.Id.Equals(agentReference, StringComparison.OrdinalIgnoreCase)
            || candidate.Slug.Equals(agentReference, StringComparison.OrdinalIgnoreCase));
        if (agent is null) return DiscordInjectionReview.Unavailable;

        var beneficiary = await NovaComputeProvenance.ResolveBeneficiaryAsync(entities, ownerUserId, ct);
        var provenance = await NovaComputeProvenance.CreateAsync(
            entities, agent, beneficiary,
            "/api/apps/nova/external-conversations/prompt-injection-review",
            [new ComputeContextReference("external-conversation", bindingId),
             new ComputeContextReference("generation", generation.ToString())],
            entrypointKind: "discord", method: "VERIFY", ct: ct);
        var source = string.Equals(transport, "leaf-commons", StringComparison.OrdinalIgnoreCase)
            ? UntrustedContentSources.LeafCommons
            : UntrustedContentSources.Discord;

        try
        {
            var result = await sentinel.ReviewAsync(new UntrustedContentReviewRequest
            {
                Content = content,
                Origin = new UntrustedContentOrigin(
                    source, $"{bindingId}:{generation}", ownerUserId, NovaAppPlugin.PluginId),
                // No compute: the Sentinel always runs on its configured tier (fast by default) on
                // the installation's default provider. The Agent only names the reviewer.
                AgentRef = agent.Id,
                IdempotencyKey = string.IsNullOrWhiteSpace(requestId)
                    ? null
                    : $"sentinel:{source}:{bindingId}:{generation}:{requestId}",
                ComputeProvenance = provenance,
                JobName = "Discord prompt-injection review",
            }, ct);
            logger.LogInformation(
                "Sentinel review {TraceId} for {BindingId} generation {Generation}: {Isolation}, available {Available}",
                result.TraceId, bindingId, generation, result.Verdict.Isolation, result.Verdict.Available);
            return ToDiscordReview(result.Verdict);
        }
        catch (Exception ex)
        {
            // As before the move: any failure of the review itself, cancellation included, is
            // advisory Unavailable. The caller's next cancellable step observes the token.
            logger.LogWarning(ex,
                "Discord prompt-injection verifier failed for {BindingId} generation {Generation}",
                bindingId, generation);
            return DiscordInjectionReview.Unavailable;
        }
    }

    internal static DiscordInjectionReview ToDiscordReview(UntrustedContentVerdict verdict)
        => verdict.Available
            ? new DiscordInjectionReview(verdict.Risk, verdict.Signals, verdict.Handling, true)
            : DiscordInjectionReview.Unavailable;
}
