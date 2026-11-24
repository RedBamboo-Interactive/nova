using System.Text.Json.Nodes;
using Leaf.Plugins.Nova.Endpoints;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging;

namespace Leaf.Plugins.Nova;

/// <summary>What the Skills surface of one discussion shows: what is attached, what waits, what could be.</summary>
public sealed record DiscussionSkillsView(
    IReadOnlyList<CapabilityAttachment> Attached,
    IReadOnlyList<CapabilityRequest> Requests,
    IReadOnlyList<SkillSummary> Defaults,
    IReadOnlyList<SkillSummary> Optional,
    IReadOnlyList<SkillSummary>? Library);

/// <summary>
/// Skills attached to one discussion on demand. The record is on the discussion entity; this keeps the
/// discussion's scratch copy in step with it and tells the Agent, in the conversation, what changed.
/// The Agent's default Skills reach every discussion through RedLeaf's workspace projection, which one
/// Agent's discussions all share, so an attachment cannot go there: it goes to the discussion's own scratch.
/// </summary>
public sealed class DiscussionSkillService(
    DiscussionStore store,
    IEntityStore entities,
    AgentDirectory agents,
    IAgentScratchSpace scratchSpace,
    EventInjector injector,
    ILogger<DiscussionSkillService> logger,
    IAssets? assets = null)
{
    public const string EventSource = "skill";

    public async Task<LeafEntity?> ResolveSkillAsync(string reference, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        var skill = Guid.TryParse(reference.Trim(), out var id)
            ? await entities.GetAsync(id, ct)
            : await entities.GetBySlugAsync(DiscussionSkills.SkillType, reference.Trim(), ct);
        return skill?.TypeSlug == DiscussionSkills.SkillType ? skill : null;
    }

    public async Task<LeafEntity?> GetAgentEntityAsync(string? agentId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return null;
        var agent = Guid.TryParse(agentId, out var id)
            ? await entities.GetAsync(id, ct)
            : await entities.GetBySlugAsync("agent", agentId, ct);
        return agent?.TypeSlug == "agent" ? agent : null;
    }

    /// <summary>The discussion's scratch folder, the one its sessions get as REDLEAF_SCRATCH_DIR.</summary>
    public async Task<string?> ScratchRootAsync(DiscussionRead discussion, CancellationToken ct)
    {
        if (discussion.AgentId is null) return null;
        var agent = await agents.GetAgentAsync(discussion.AgentId, ct);
        return agent is null ? null : scratchSpace.PrepareExecution(agent.Name, discussion.Id).Path;
    }

    public async Task<DiscussionSkillsView> ViewAsync(DiscussionRead discussion, bool includeLibrary, CancellationToken ct)
    {
        var agent = await GetAgentEntityAsync(discussion.AgentId, ct);
        var defaults = agent is null ? [] : await ResolveListAsync(DiscussionSkills.References(agent.Data, DiscussionSkills.DefaultField), ct);
        var optional = agent is null ? [] : await ResolveListAsync(DiscussionSkills.References(agent.Data, DiscussionSkills.OptionalField), ct);
        IReadOnlyList<SkillSummary>? library = null;
        if (includeLibrary)
        {
            var all = await entities.QueryAsync(new EntityQuery { TypeSlug = DiscussionSkills.SkillType, Limit = 500 }, ct);
            library = all.Select(DiscussionSkills.Summary).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        return new DiscussionSkillsView(discussion.AttachedSkills,
            (discussion.CapabilityRequests ?? []).Where(r => r.Kind == CapabilityAttachment.SkillKind).ToList(),
            defaults.Select(DiscussionSkills.Summary).ToList(),
            optional.Select(DiscussionSkills.Summary).ToList(),
            library);
    }

    /// <summary>Whether the Agent may attach this Skill without asking: it is optional or already a default.</summary>
    public static bool AgentMayAttach(LeafEntity agent, LeafEntity skill)
        => DiscussionSkills.Lists(DiscussionSkills.References(agent.Data, DiscussionSkills.OptionalField), skill)
            || DiscussionSkills.Lists(DiscussionSkills.References(agent.Data, DiscussionSkills.DefaultField), skill);

    /// <summary>
    /// Attaches, writes the scratch copy, and gives the Agent the whole SKILL.md as a note. Returns the
    /// attachments and whether this call added one (false: it was already attached, nothing was sent).
    /// </summary>
    public async Task<(IReadOnlyList<CapabilityAttachment> Items, bool Added, string? SkillFile)?> AttachAsync(
        DiscussionRead discussion, LeafEntity skill, CapabilityAttachment attachment, string attachedByText, CancellationToken ct)
    {
        var result = await store.AttachCapabilityAsync(discussion.EntityId, attachment, ct);
        if (result is not var (items, added)) return null;
        var root = await ScratchRootAsync(discussion, ct);
        string? skillFile = null;
        if (root is not null)
        {
            try { skillFile = await DiscussionSkills.MaterializeAsync(skill, root, ReadAssetAsync, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not write attached Skill {Skill} into discussion {Discussion}'s scratch", skill.Slug, discussion.Id);
            }
        }
        if (added)
        {
            var file = skillFile ?? (root is null ? "(scratch unavailable)" : DiscussionSkills.SkillFile(root, skill.Slug));
            await NotifyAsync(discussion, DiscussionSkills.AttachedNote(attachment, skill, file, attachedByText),
                $"skill-attached:{discussion.Id}:{attachment.EntityId}:{attachment.AttachedAt.UtcTicks}", ct);
        }
        return (items, added, skillFile);
    }

    /// <summary>Records the Agent's request; the discussion's card asks the person. One pending request per Skill.</summary>
    public Task<(CapabilityRequest Request, bool Added)?> RequestAsync(DiscussionRead discussion, LeafEntity skill,
        DiscussionSkillEndpoints.CallerInfo caller, string? reason, DateTimeOffset at, CancellationToken ct)
        => store.RequestCapabilityAsync(discussion.EntityId, new CapabilityRequest(
            Guid.NewGuid().ToString("N"), CapabilityAttachment.SkillKind, skill.Id.ToString(), skill.Slug,
            string.IsNullOrWhiteSpace(skill.Name) ? skill.Slug : skill.Name,
            string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()[..Math.Min(reason.Trim().Length, 500)],
            caller.Id ?? "", caller.Name, at, CapabilityRequest.Pending), ct);

    public async Task<(IReadOnlyList<CapabilityAttachment> Items, CapabilityAttachment? Removed)?> DetachAsync(
        DiscussionRead discussion, string reference, string detachedByText, CancellationToken ct)
    {
        var result = await store.DetachCapabilityAsync(discussion.EntityId, CapabilityAttachment.SkillKind, reference, ct);
        if (result is not var (items, removed)) return null;
        if (removed is not null)
        {
            if (await ScratchRootAsync(discussion, ct) is { } root) DiscussionSkills.Remove(root, removed.Slug);
            await NotifyAsync(discussion, DiscussionSkills.DetachedNote(removed, detachedByText),
                $"skill-detached:{discussion.Id}:{removed.EntityId}:{DateTimeOffset.UtcNow.UtcTicks}", ct);
        }
        return (items, removed);
    }

    public Task NotifyDeclinedAsync(DiscussionRead discussion, CapabilityRequest request, string declinedByText, CancellationToken ct)
        => NotifyAsync(discussion, DiscussionSkills.DeclinedNote(request, declinedByText),
            $"skill-declined:{discussion.Id}:{request.Id}", ct);

    /// <summary>
    /// Writes every attached Skill that is missing or stale back into scratch, which may have been
    /// cleared while the discussion slept, and returns the lines this turn's context names them with.
    /// Never fails the turn: a Skill that cannot be written is logged and still named.
    /// </summary>
    public async Task<IReadOnlyList<string>> PrepareTurnAsync(DiscussionRead discussion, CancellationToken ct)
    {
        var attached = discussion.AttachedSkills;
        if (attached.Count == 0) return [];
        string? root;
        try { root = await ScratchRootAsync(discussion, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "No scratch folder for discussion {Discussion}'s attached Skills", discussion.Id);
            return [];
        }
        if (root is null) return [];
        var lines = new List<string>();
        foreach (var attachment in attached)
        {
            try
            {
                if (await ResolveSkillAsync(attachment.EntityId, ct) is { } skill)
                    await DiscussionSkills.MaterializeAsync(skill, root, ReadAssetAsync, ct);
                else
                {
                    lines.Add($"Attached Skill: {attachment.Name} ({attachment.Slug}) no longer exists in the Skill library. Say so once if it matters here.");
                    continue;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not refresh attached Skill {Skill} for discussion {Discussion}", attachment.Slug, discussion.Id);
            }
            lines.Add(DiscussionSkills.ContextLine(attachment, root));
        }
        return lines;
    }

    /// <summary>Sends Nova's own note about an attachment change (a Skill, or an MCP server with <paramref name="source"/> mcp).</summary>
    internal async Task NotifyAsync(DiscussionRead discussion, string content, string idempotencyKey, CancellationToken ct,
        string source = EventSource)
    {
        try
        {
            // The note is Nova's own, so it goes out under the discussion's Agent and owner, never under
            // the request that caused it: the kernel's compute gateway reads the ambient caller, and a
            // loopback browser (local-user) would have the owner's send refused. Awaited, so the note
            // is in place before the response; a note the session cannot take now waits in its queue.
            // A note the Agent acts on after whatever it is doing now: attaching from inside a turn
            // must not interrupt that turn.
            Task<bool> send;
            using (ExecutionContext.SuppressFlow())
                send = Task.Run(() => injector.InjectAsync(discussion, content, null, source,
                    idempotencyKey: idempotencyKey, ct: CancellationToken.None, deliveryIdempotencyKey: idempotencyKey,
                    delivery: DelegateEndpoints.AfterCurrent));
            if (!await send.WaitAsync(ct))
                logger.LogInformation("The {Source} note for discussion {Discussion} is recorded but was not forwarded now (no session yet, or refused)", source, discussion.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The {Source} note for discussion {Discussion} was not delivered", source, discussion.Id);
        }
    }

    /// <summary>
    /// The person as the note names them. A loopback browser signs in as local-user ("Local User"),
    /// who stands for the installation owner: name the discussion's owner instead.
    /// </summary>
    public async Task<DiscussionSkillEndpoints.CallerInfo> NamePersonAsync(DiscussionRead discussion,
        DiscussionSkillEndpoints.CallerInfo caller, CancellationToken ct)
    {
        if (caller.Kind != CapabilityAttachment.ByUser
            || (caller.Id is not null && caller.Id != OwnerScope.LocalUser && !string.IsNullOrWhiteSpace(caller.Name)))
            return caller;
        var owner = await NovaComputeProvenance.ResolveBeneficiaryAsync(entities, discussion.OwnerId, ct);
        if (owner.Kind != "user" || string.IsNullOrWhiteSpace(owner.Id)) return caller;
        var name = string.IsNullOrWhiteSpace(owner.NameSnapshot) ? null : owner.NameSnapshot;
        return new DiscussionSkillEndpoints.CallerInfo(caller.Kind, owner.Id, name,
            string.IsNullOrWhiteSpace(name) ? "Your owner" : name.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]);
    }

    private async Task<byte[]?> ReadAssetAsync(string assetId, CancellationToken ct)
        => assets is null ? null : (await assets.ReadAsync(assetId, ct))?.Bytes;

    private async Task<IReadOnlyList<LeafEntity>> ResolveListAsync(IReadOnlyList<string> references, CancellationToken ct)
    {
        var skills = new List<LeafEntity>();
        foreach (var reference in references)
            if (await ResolveSkillAsync(reference, ct) is { } skill && skills.All(s => s.Id != skill.Id))
                skills.Add(skill);
        return skills;
    }
}
