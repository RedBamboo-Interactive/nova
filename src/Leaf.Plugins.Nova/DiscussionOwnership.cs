namespace Leaf.Plugins.Nova;

internal static class DiscussionOwnership
{
    /// <summary>
    /// Anonymous local automation can create a delivery discussion before a user
    /// is present. On the first authenticated reply, bind that legacy sentinel to
    /// the replying user so the new Compute job has a verified beneficiary.
    /// Existing provider sessions retain their original owner.
    /// </summary>
    internal static string? ResolveForSessionStart(
        string? discussionOwnerId, string? replyingUserId, bool needsSession)
    {
        if (needsSession
            && string.Equals(discussionOwnerId, "local-user", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(replyingUserId)
            && !string.Equals(replyingUserId, "local-user", StringComparison.Ordinal))
            return replyingUserId;

        return discussionOwnerId;
    }
}
