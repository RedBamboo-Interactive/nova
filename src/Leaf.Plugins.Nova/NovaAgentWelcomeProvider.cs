using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Nova;

/// <summary>
/// Creates Nova's first durable discussion and asks the selected provider to author
/// the opening. The generation runs as an internal one-shot, so the user-facing
/// transcript contains Nova's answer without a counterfeit visible user message.
/// </summary>
public sealed class NovaAgentWelcomeProvider(
    DiscussionStore discussions,
    IDiscussions messages,
    AgentDirectory agents,
    AgentWorkspaces workspaces,
    IAgentScratchSpace scratchSpace,
    RedComputeClient redCompute,
    IEntityStore entities) : IAgentWelcomeProvider
{
    private const string FirstRunPromptResource = "nova-welcome-prompt.v1.md";
    private const string ReviewPromptResource = "nova-review-welcome-prompt.v1.md";
    private static readonly string[] WelcomeTools = ["Read", "Glob", "Grep"];
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new();
    private static readonly string FirstRunPrompt = ReadPrompt(FirstRunPromptResource);
    private static readonly string ReviewPrompt = ReadPrompt(ReviewPromptResource);

    public string TemplateId => "nova/default";

    public async Task<AgentWelcomeResult> EnsureWelcomeAsync(
        AgentWelcomeContext context,
        CancellationToken ct = default)
    {
        var gate = Gates.GetOrAdd(context.IdempotencyKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var discussionKey = context.IdempotencyKey + ":discussion";
            var (discussion, _) = await discussions.GetOrCreateIdempotentAsync(
                discussionKey,
                context.AgentId.ToString(),
                context.OwnerId,
                qualityTier: context.QualityTierSlug,
                provider: context.ProviderSlug,
                ct: ct);
            ValidateDiscussionBinding(discussion, context);

            var messageKey = context.IdempotencyKey + ":message";
            var existing = (await messages.GetMessagesAsync(discussion.EntityId, ct: ct))
                .FirstOrDefault(message =>
                    message.Role == "assistant"
                    && message.Metadata["idempotency_key"]?.GetValue<string>() == messageKey
                    && !string.IsNullOrWhiteSpace(message.Content));
            if (existing is not null)
            {
                agents.NovaAgentId = context.AgentId.ToString();
                return new AgentWelcomeResult(
                    discussion.Id,
                    null,
                    existing.Metadata["uid"]?.GetValue<string>() ?? MessageUid(messageKey));
            }

            var agent = (await agents.GetAgentsAsync(forceRefresh: true, ct))
                .SingleOrDefault(candidate => candidate.Id == context.AgentId.ToString())
                ?? throw new InvalidOperationException("The newly created Nova Agent could not be resolved");
            agents.NovaAgentId = agent.Id;

            var scratch = scratchSpace.PrepareExecution(agent.Name, context.IdempotencyKey);
            // Meet Nova is never allowed to fall back to a disposable scratch workspace.
            // The welcome and the conversation that follows must use the Agent's real,
            // durable identity and memory root.
            var workspace = await workspaces.GetAsync(agent.Id, ct);
            workspace.GenerateClaudeMd();
            var isReview = context.Purpose == AgentWelcomePurpose.ReviewExistingAgent;
            var body = new Dictionary<string, object?>
            {
                ["prompt"] = PromptFor(context.Purpose),
                ["qualityTier"] = context.QualityTierSlug,
                ["provider"] = context.ProviderSlug,
                ["workingDir"] = workspace.WorkspacePath,
                ["allowedTools"] = WelcomeTools,
                ["maxTurns"] = 8,
                ["timeout"] = 180,
                ["networkAccess"] = false,
                ["env"] = scratch.Environment,
                ["addDirs"] = new[] { scratch.Path },
            };
            var beneficiary = await NovaComputeProvenance.ResolveBeneficiaryAsync(
                entities, context.OwnerId, ct);
            var provenance = await NovaComputeProvenance.CreateAsync(
                entities,
                agent,
                beneficiary,
                "setup:nova-welcome",
                [new ComputeContextReference("discussion", discussion.Id),
                 new ComputeContextReference("setup", context.IdempotencyKey)],
                entrypointKind: "setup",
                method: "POST",
                ct: ct);
            var result = await redCompute.ExecuteAsync(
                body,
                isReview ? "Nova: Welcome back" : "Nova: First hello",
                context.OwnerId,
                180,
                provenance,
                ct,
                idempotencyKey: context.IdempotencyKey + ":generation");
            if (!result.Success || string.IsNullOrWhiteSpace(result.Text))
                throw new InvalidOperationException(result.Error ?? "Nova returned an empty first greeting");

            var uid = MessageUid(messageKey);
            await discussions.PostConversationMessageAsync(
                discussion.EntityId,
                result.Text.Trim(),
                new JsonObject
                {
                    ["source"] = "nova-message",
                    ["sender_agent_id"] = agent.Id,
                    ["uid"] = uid,
                    ["setup_welcome"] = true,
                },
                messageKey,
                context.OwnerId,
                ct);
            await discussions.PatchAsync(
                discussion.EntityId,
                new JsonObject { ["injected_context"] = result.Text.Trim() },
                "Meet Nova",
                ct);

            return new AgentWelcomeResult(
                discussion.Id,
                result.SessionId,
                uid);
        }
        finally
        {
            gate.Release();
        }
    }

    private static string MessageUid(string key)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    internal static string PromptFor(AgentWelcomePurpose purpose)
        => purpose == AgentWelcomePurpose.ReviewExistingAgent
            ? ReviewPrompt
            : FirstRunPrompt;

    internal static void ValidateDiscussionBinding(
        DiscussionRead discussion,
        AgentWelcomeContext context)
    {
        if (!string.Equals(discussion.AgentId, context.AgentId.ToString(),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Meet Nova resolved a discussion bound to a different Agent");
        if (!string.Equals(discussion.OwnerId, context.OwnerId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Meet Nova resolved a discussion owned by a different user");
        if (!string.Equals(discussion.Provider, context.ProviderSlug,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Meet Nova resolved a discussion with a different inference provider");
        if (!string.Equals(discussion.QualityTier, context.QualityTierSlug,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Meet Nova resolved a discussion with a different quality tier");
    }

    private static string ReadPrompt(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded {resourceName} is missing");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var prompt = reader.ReadToEnd().Trim();
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidDataException("Nova's welcome prompt is empty");
        return prompt;
    }
}
