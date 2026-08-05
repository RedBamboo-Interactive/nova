using Leaf.Sdk.Services;

namespace Leaf.Plugins.Nova;

/// <summary>
/// Nova's generic host side for optional backend extensions. This service knows
/// only SDK contribution contracts, never contributor ids or domain schemas.
/// </summary>
public sealed class ExtensionContributions(IPluginExtensions extensions, LiveEvents live)
    : IDisposable
{
    public const string ContextSlot = "context-contributor";
    public const string LiveSlot = "live-event";

    private IDisposable? _liveSubscription;

    public void Start()
    {
        _liveSubscription ??= extensions.SubscribeLiveEvents(LiveSlot, async (projection, ct) =>
            await live.PostAsync(
                projection.Source,
                projection.Content,
                projection.Metadata,
                projection.IdempotencyKey,
                ct));
    }

    public Task<IReadOnlyList<PluginContextFragment>> CollectContextAsync(
        string? userId,
        string? agentId,
        string? discussionId,
        string purpose,
        CancellationToken ct = default)
        => extensions.CollectContextAsync(ContextSlot,
            new PluginContextRequest(userId, agentId, discussionId, purpose), ct);

    public void Dispose()
    {
        _liveSubscription?.Dispose();
        _liveSubscription = null;
    }
}
