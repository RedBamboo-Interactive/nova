using Microsoft.AspNetCore.Http;

namespace Leaf.Plugins.Nova;

internal enum DiscussionReopenDecision
{
    Reopen,
    AlreadyOpen,
    Archiving,
    UnsupportedType,
    MissingSession,
    AccessDenied,
}

internal static class DiscussionMutationPolicy
{
    public static bool RejectSend(DiscussionRead discussion)
        => DiscussionStatus.IsClosed(discussion.Status);

    public static DiscussionReopenDecision Reopen(
        DiscussionRead discussion, HttpContext context)
    {
        if (!DiscussionAccessPolicy.CanRead(discussion, context))
            return DiscussionReopenDecision.AccessDenied;
        if (discussion.Type is "live" or HeartbeatService.DiscussionType)
            return DiscussionReopenDecision.UnsupportedType;
        if (discussion.Status == DiscussionStatus.Archiving)
            return DiscussionReopenDecision.Archiving;
        if (discussion.Status != DiscussionStatus.Archived)
            return DiscussionReopenDecision.AlreadyOpen;
        if (string.IsNullOrWhiteSpace(discussion.SessionId))
            return DiscussionReopenDecision.MissingSession;
        return DiscussionReopenDecision.Reopen;
    }
}
