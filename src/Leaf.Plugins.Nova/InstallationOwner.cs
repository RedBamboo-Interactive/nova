using System.Security.Claims;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Http;

namespace Leaf.Plugins.Nova;

/// <summary>
/// The person this installation belongs to, whom the loopback <c>local-user</c> stands for: the
/// owner recorded when setup finished, else the first account, which setup created. The same rule
/// as RedLeaf's <c>DevelopmentOverrideEndpoints.InstallationOwnerAsync</c>. When there is no such
/// person yet, <c>local-user</c> stands for nobody and reaches only unowned and local discussions.
/// </summary>
public sealed class InstallationOwner(IEntityStore entities, TimeProvider? time = null)
{
    internal const string RequestItem = "nova.installation-owner";
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(30);
    private const int UserPage = 200;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private Cached? _cached;

    private sealed record Cached(string? OwnerId, DateTimeOffset At);

    /// <summary>The installation owner's user id, or null when the installation has none yet.</summary>
    public async Task<string?> ResolveAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        if (_cached is { } cached && now - cached.At < Fresh) return cached.OwnerId;

        var ownerId = await ReadAsync(ct);
        _cached = new Cached(ownerId, now);
        return ownerId;
    }

    /// <summary>
    /// Records on the request whom its <c>local-user</c> stands for, so the synchronous access
    /// policy can read it. Only a <c>local-user</c> request pays for the lookup.
    /// </summary>
    public async Task StampAsync(HttpContext context)
    {
        if (OwnerScope.IsLocalUser(context.User.FindFirstValue("sub")))
            context.Items[RequestItem] = await ResolveAsync(context.RequestAborted);
    }

    /// <summary>What <see cref="StampAsync"/> recorded on this request; null when nothing was.</summary>
    public static string? ForRequest(HttpContext context)
        => context.Items.TryGetValue(RequestItem, out var value) ? value as string : null;

    private async Task<string?> ReadAsync(CancellationToken ct)
    {
        var status = await entities.GetBySlugAsync("setup-status", "default", ct);
        if (status?.Data["experience"] is JsonObject experience
            && experience["ownerUserId"] is JsonValue value
            && value.TryGetValue<string>(out var recorded)
            && !string.IsNullOrWhiteSpace(recorded))
            return recorded;

        LeafEntity? first = null;
        Guid? after = null;
        while (true)
        {
            var page = await entities.QueryAsync(new EntityQuery
            {
                TypeSlug = "user", OrderById = true, AfterId = after, Limit = UserPage,
            }, ct);
            foreach (var user in page)
                if (first is null || user.CreatedAt < first.CreatedAt
                    || (user.CreatedAt == first.CreatedAt && user.Id.CompareTo(first.Id) < 0))
                    first = user;
            if (page.Count < UserPage || page[^1].Id == after) break;
            after = page[^1].Id;
        }
        return first?.Id.ToString();
    }
}
