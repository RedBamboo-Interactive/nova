using System.Security.Claims;
using Leaf.Plugins.Nova.Endpoints;
using Microsoft.AspNetCore.Http;

namespace Leaf.Plugins.Nova;

/// <summary>
/// A caller that runs inside a RedCompute session (its verified token names one) must not post into a
/// session with a higher access level than its own: a session at Ask or capped at Auto could otherwise
/// tell a Full session to run something. A person, and a token that names no session, are not limited
/// here. Posting into a session at the same level or a lower one is unchanged.
/// </summary>
public static class SessionLevelGuard
{
    private static readonly string[] Order = ["plan", "ask", "auto", "full"];

    /// <summary>
    /// A level's rank. A session RedCompute reports no level for predates access levels and ran with
    /// everything allowed, so a missing or unknown level counts as Full, on either side.
    /// </summary>
    internal static int Rank(string? level)
    {
        var index = Array.FindIndex(Order, candidate => string.Equals(candidate, level, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? Order.Length - 1 : index;
    }

    internal static bool IsAbove(string? targetLevel, string? callerLevel) => Rank(targetLevel) > Rank(callerLevel);

    /// <summary>
    /// A refusal when the token's session sits below <paramref name="targetSessionId"/>, otherwise null.
    /// The caller's session comes from the verified token only. When either level cannot be read the
    /// post is refused too: nothing can be shown to be safe.
    /// </summary>
    internal static async Task<IResult?> RefuseUpwardAsync(
        ClaimsPrincipal user, string? targetSessionId, RedComputeClient redCompute, CancellationToken ct)
    {
        var callerSessionId = DelegateEndpoints.CallerSessionId(user);
        if (callerSessionId is null || string.IsNullOrWhiteSpace(targetSessionId)
            || string.Equals(callerSessionId, targetSessionId, StringComparison.Ordinal))
            return null;

        var caller = await redCompute.ProbeSessionAsync(callerSessionId, ct);
        var target = await redCompute.ProbeSessionAsync(targetSessionId, ct);
        if (!caller.Reachable || !target.Reachable)
            return Results.Json(new
            {
                error = "session_level_unverified",
                message = "RedCompute could not report the access levels of the two sessions, so this message was not sent. Try again in a moment.",
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        // A session RedCompute no longer has takes nothing; the send reports that itself.
        if (target.Status is null) return null;
        if (!IsAbove(target.AccessLevel, caller.AccessLevel)) return null;
        return Results.Json(new
        {
            error = "session_above_caller",
            message = $"This session runs at a higher access level ({target.AccessLevel ?? "unknown"}) than the session you are in ({caller.AccessLevel ?? "unknown"}), so you cannot post into it. Ask the person to do it, or ask for it in a discussion at your own level.",
        }, statusCode: StatusCodes.Status403Forbidden);
    }
}
