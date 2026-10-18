using Microsoft.AspNetCore.Http;

namespace Leaf.Plugins.Nova;

/// <summary>
/// Another plugin may own a <c>discussion</c> entity while keeping it visible in Nova
/// (Storyboard campaign Agent discussions). The kernel then rejects Nova's writes to it,
/// and admission into its session belongs to the owner: Nova's pipeline would add private
/// <c>&lt;nova-context&gt;</c> and lose the owner's signed conversation identity. Nova reads
/// such discussions, skips its own housekeeping on them, and refuses lifecycle changes.
/// </summary>
public static class DiscussionApplicationOwner
{
    /// <summary>
    /// Nova's own plugin entity id, resolved at startup. Nova creates discussions through
    /// the kernel <c>IDiscussions</c> service and only patches them, so it never stamps
    /// <c>owner_plugin</c> today; excluding its own id keeps a future stamped write from
    /// locking Nova out of its own discussions.
    /// </summary>
    public static Guid? NovaPluginEntityId { get; set; }

    public static bool IsForeign(DiscussionRead discussion) => IsForeign(discussion.OwnerPlugin);

    public static bool IsForeign(string? ownerPlugin)
        => Guid.TryParse(ownerPlugin, out var owner) && owner != NovaPluginEntityId;

    public const string ErrorCode = "owned_by_application";

    public static string Message(DiscussionRead discussion)
        => $"This discussion belongs to {discussion.OwnerApp ?? "another application"} and is read-only in Nova.";

    public static IResult Refusal(DiscussionRead discussion)
        => Results.Json(new
        {
            error = ErrorCode,
            app = discussion.OwnerApp,
            message = Message(discussion),
        }, statusCode: StatusCodes.Status409Conflict);
}

/// <summary>A Nova lifecycle write was attempted on a discussion another plugin owns.</summary>
public sealed class DiscussionOwnedByApplicationException(DiscussionRead discussion)
    : InvalidOperationException(DiscussionApplicationOwner.Message(discussion))
{
    public DiscussionRead Discussion { get; } = discussion;
}
