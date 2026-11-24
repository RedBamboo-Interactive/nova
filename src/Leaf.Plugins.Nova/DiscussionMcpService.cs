using Leaf.Plugins.Nova.Endpoints;
using Leaf.Sdk;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Nova;

/// <summary>What the MCP surface of one discussion shows: what is attached, what waits, what could be.</summary>
public sealed record DiscussionMcpView(
    IReadOnlyList<CapabilityAttachment> Attached,
    IReadOnlyList<CapabilityRequest> Requests,
    IReadOnlyList<McpServerSummary> Defaults,
    IReadOnlyList<McpServerSummary> Optional,
    IReadOnlyList<McpServerSummary>? Library);

/// <summary>
/// MCP servers attached to one discussion on demand, on the same record as attached Skills. Nothing is
/// written to scratch: RedLeaf's MCP gateway reads the record and serves the tools; this tells the Agent.
/// </summary>
public sealed class DiscussionMcpService(DiscussionStore store, IEntityStore entities, DiscussionSkillService skills)
{
    public const string EventSource = "mcp";

    public async Task<LeafEntity?> ResolveServerAsync(string reference, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        var server = Guid.TryParse(reference.Trim(), out var id)
            ? await entities.GetAsync(id, ct)
            : await entities.GetBySlugAsync(DiscussionMcpServers.ServerType, reference.Trim(), ct);
        return server?.TypeSlug == DiscussionMcpServers.ServerType ? server : null;
    }

    public async Task<DiscussionMcpView> ViewAsync(DiscussionRead discussion, bool includeLibrary, CancellationToken ct)
    {
        var agent = await skills.GetAgentEntityAsync(discussion.AgentId, ct);
        var defaults = agent is null ? [] : await ResolveListAsync(DiscussionSkills.References(agent.Data, DiscussionMcpServers.DefaultField), ct);
        var optional = agent is null ? [] : await ResolveListAsync(DiscussionSkills.References(agent.Data, DiscussionMcpServers.OptionalField), ct);
        IReadOnlyList<McpServerSummary>? library = null;
        if (includeLibrary)
        {
            var all = await entities.QueryAsync(new EntityQuery { TypeSlug = DiscussionMcpServers.ServerType, Limit = 500 }, ct);
            library = all.Where(s => !DiscussionMcpServers.IsDisabled(s)).Select(DiscussionMcpServers.Summary)
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        return new DiscussionMcpView(discussion.AttachedMcpServers,
            (discussion.CapabilityRequests ?? []).Where(r => r.Kind == CapabilityAttachment.McpKind).ToList(),
            defaults.Select(DiscussionMcpServers.Summary).ToList(),
            optional.Select(DiscussionMcpServers.Summary).ToList(),
            library);
    }

    /// <summary>Whether the Agent may attach this server without asking: it is optional or already a default.</summary>
    public static bool AgentMayAttach(LeafEntity agent, LeafEntity server)
        => DiscussionSkills.Lists(DiscussionSkills.References(agent.Data, DiscussionMcpServers.OptionalField), server)
            || DiscussionSkills.Lists(DiscussionSkills.References(agent.Data, DiscussionMcpServers.DefaultField), server);

    /// <summary>Attaches and tells the Agent how to reach the tools. Added is false when it was already attached.</summary>
    public async Task<(IReadOnlyList<CapabilityAttachment> Items, bool Added)?> AttachAsync(
        DiscussionRead discussion, CapabilityAttachment attachment, string attachedByText, CancellationToken ct)
    {
        var result = await store.AttachCapabilityAsync(discussion.EntityId, attachment, ct);
        if (result is not var (items, added)) return null;
        if (added)
            await skills.NotifyAsync(discussion, DiscussionMcpServers.AttachedNote(attachment, attachedByText),
                $"mcp-attached:{discussion.Id}:{attachment.EntityId}:{attachment.AttachedAt.UtcTicks}", ct, EventSource);
        return (items.Where(a => a.Kind == CapabilityAttachment.McpKind).ToList(), added);
    }

    public Task<(CapabilityRequest Request, bool Added)?> RequestAsync(DiscussionRead discussion, LeafEntity server,
        DiscussionSkillEndpoints.CallerInfo caller, string? reason, DateTimeOffset at, CancellationToken ct)
        => store.RequestCapabilityAsync(discussion.EntityId, new CapabilityRequest(
            Guid.NewGuid().ToString("N"), CapabilityAttachment.McpKind, server.Id.ToString(), server.Slug,
            string.IsNullOrWhiteSpace(server.Name) ? server.Slug : server.Name,
            string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()[..Math.Min(reason.Trim().Length, 500)],
            caller.Id ?? "", caller.Name, at, CapabilityRequest.Pending), ct);

    public async Task<(IReadOnlyList<CapabilityAttachment> Items, CapabilityAttachment? Removed)?> DetachAsync(
        DiscussionRead discussion, string reference, string detachedByText, CancellationToken ct)
    {
        var result = await store.DetachCapabilityAsync(discussion.EntityId, CapabilityAttachment.McpKind, reference, ct);
        if (result is not var (items, removed)) return null;
        if (removed is not null)
            await skills.NotifyAsync(discussion, DiscussionMcpServers.DetachedNote(removed, detachedByText),
                $"mcp-detached:{discussion.Id}:{removed.EntityId}:{DateTimeOffset.UtcNow.UtcTicks}", ct, EventSource);
        return (items.Where(a => a.Kind == CapabilityAttachment.McpKind).ToList(), removed);
    }

    public Task NotifyDeclinedAsync(DiscussionRead discussion, CapabilityRequest request, string declinedByText, CancellationToken ct)
        => skills.NotifyAsync(discussion, DiscussionMcpServers.DeclinedNote(request, declinedByText),
            $"mcp-declined:{discussion.Id}:{request.Id}", ct, EventSource);

    /// <summary>The lines this turn's context names the attached MCP servers with.</summary>
    public static IReadOnlyList<string> ContextLines(DiscussionRead discussion)
        => discussion.AttachedMcpServers.Select(DiscussionMcpServers.ContextLine).ToList();

    private async Task<IReadOnlyList<LeafEntity>> ResolveListAsync(IReadOnlyList<string> references, CancellationToken ct)
    {
        var servers = new List<LeafEntity>();
        foreach (var reference in references)
            if (await ResolveServerAsync(reference, ct) is { } server && servers.All(s => s.Id != server.Id))
                servers.Add(server);
        return servers;
    }
}
