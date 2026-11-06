namespace Leaf.Plugins.Nova;

internal static class DiscussionOwnership
{
    /// <summary>
    /// Anonymous local automation can create a delivery discussion before a user
    /// is present. On the first authenticated reply, bind that legacy sentinel to
    /// the replying user so the new Compute job has a verified beneficiary, but only
    /// when that user is the installation owner the discussion belongs to (anyone
    /// signed while the owner is unknown, as before). A second user never takes it over.
    /// Local-user replying leaves it local: its work stays system-owned, as RedLeaf
    /// treats every LocalDefault request. Existing provider sessions retain their owner.
    /// </summary>
    internal static string? ResolveForSessionStart(
        string? discussionOwnerId, string? replyingUserId, bool needsSession, string? installationOwnerId)
    {
        if (needsSession
            && string.Equals(discussionOwnerId, OwnerScope.LocalUser, StringComparison.Ordinal)
            && OwnerScope.MayClaim(replyingUserId, installationOwnerId))
            return replyingUserId;

        return discussionOwnerId;
    }
}
