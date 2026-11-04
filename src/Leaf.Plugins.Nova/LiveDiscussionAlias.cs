namespace Leaf.Plugins.Nova;

/// <summary>
/// The <c>live</c> discussion-id alias: the LIVE id rotates daily, so callers
/// (heartbeat, rotation automation) address "the current LIVE" instead.
/// </summary>
internal static class LiveDiscussionAlias
{
    public const string Id = "live";

    public static bool IsAlias(string id) => id == Id;

    /// <summary>
    /// First open (not archived/archiving) LIVE owned by Nova — its agent, or no agent.
    /// <paramref name="liveDiscussions"/> must already be filtered to type "live".
    /// </summary>
    public static DiscussionRead? FirstOpen(IEnumerable<DiscussionRead> liveDiscussions, string? novaAgentId)
        => liveDiscussions.FirstOrDefault(d =>
            !DiscussionStatus.IsClosed(d.Status)
            && (novaAgentId == null || d.AgentId == null || d.AgentId == novaAgentId));

    /// <summary>
    /// The LIVE a caller means by the alias: a signed Agent's own when it has one. An Agent without
    /// a LIVE, such as one delivering to Nova's morning, and every unsigned caller get Nova's.
    /// </summary>
    public static DiscussionRead? FirstOpenFor(
        IReadOnlyCollection<DiscussionRead> liveDiscussions, string? callerAgentId, string? novaAgentId)
        => (callerAgentId is null
                ? null
                : liveDiscussions.FirstOrDefault(d => !DiscussionStatus.IsClosed(d.Status) && d.AgentId == callerAgentId))
            ?? FirstOpen(liveDiscussions, novaAgentId);

    /// <summary>
    /// Resolves a route id: the <c>live</c> alias to the caller's current open LIVE, any other id
    /// through <paramref name="getById"/>. Callers still run access/mutation checks on the result.
    /// </summary>
    public static async Task<DiscussionRead?> ResolveAsync(
        string id, string? callerAgentId, string? novaAgentId,
        Func<string, Task<DiscussionRead?>> getById,
        Func<Task<List<DiscussionRead>>> listLive)
        => IsAlias(id)
            ? FirstOpenFor(await listLive(), callerAgentId, novaAgentId)
            : await getById(id);
}
