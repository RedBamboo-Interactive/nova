using Nova.App.Data.Entities;

namespace Nova.App.Data;

/// <summary>
/// Centralized owner-scoping rules for user-owned resources (discussions, automations).
/// A resource is accessible when it has no owner, is owned by the local user, the caller
/// is the local user, or the caller is the owner.
/// </summary>
public static class OwnerScope
{
    public static bool CanAccess(string? ownerId, string? userId)
        => ownerId is null || ownerId == "local-user" || userId == "local-user" || ownerId == userId;

    /// <summary>
    /// Applies the owner filter to a discussion query. No-op for the local user
    /// (or anonymous local requests), matching the per-entity CanAccess rules.
    /// </summary>
    public static IQueryable<Discussion> WhereCanAccess(this IQueryable<Discussion> query, string? userId)
    {
        if (userId is null || userId == "local-user") return query;
        return query.Where(d => d.OwnerId == null || d.OwnerId == "local-user" || d.OwnerId == userId);
    }
}
