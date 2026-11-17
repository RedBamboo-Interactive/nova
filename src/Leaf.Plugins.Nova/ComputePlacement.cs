using System.Text.Json.Nodes;

namespace Leaf.Plugins.Nova;

/// <summary>
/// The <c>placement</c> member Nova adds to the RedCompute session and execute bodies it already sends
/// (placement by reference, Phase 2 design 2.1; rollout stage A, 8.1). It names the same workspace and
/// scratch the legacy path fields carry, by reference; RedCompute resolves it in the background,
/// compares it with those fields and still uses them.
/// <para>
/// RedCompute refuses a malformed <c>placement</c> with 422 before the work starts, so a builder here never
/// sends a partial or doubtful one: it is built only from values the caller already holds, checked
/// against RedCompute's parser rules (<c>PlacementParser</c>: ids are entity UUIDs; text is non-blank, at
/// most 512 characters, no control characters), and when any part fails the caller sends no
/// <c>placement</c> at all. <c>nodeId</c> is never sent yet: absent means "route", which is node zero.
/// </para>
/// </summary>
internal static class ComputePlacement
{
    /// <summary>The body member.</summary>
    public const string Member = "placement";

    internal const int MaxTextLength = 512;

    /// <summary>The scratch folder is the process's scratch environment (sessions: <c>scratchDir</c>).</summary>
    public const string ScratchAsEnv = "env";

    /// <summary>The scratch environment plus the folder in <c>addDirs</c> (execute only).</summary>
    public const string ScratchAsAddDir = "add-dir";

    /// <summary>
    /// An Agent's workspace with a scratch allocation: <c>{workspace:{agentId}, scratch:{owner, executionKey}}</c>,
    /// plus <c>scratchAs</c> when it is not the default <c>env</c>. RedCompute's working folder is the mounted
    /// workspace, or, for an Agent with none, the scratch folder (where Nova writes the disposable workspace).
    /// Null when any part is not well formed.
    /// </summary>
    public static JsonObject? ForAgent(string? agentId, string? scratchOwner, string? executionKey,
        string scratchAs = ScratchAsEnv)
    {
        if (!TryEntityId(agentId, out var agent) || !IsText(scratchOwner) || !IsText(executionKey)) return null;
        var placement = new JsonObject
        {
            ["workspace"] = new JsonObject { ["agentId"] = agent },
            ["scratch"] = new JsonObject { ["owner"] = scratchOwner, ["executionKey"] = executionKey },
        };
        if (scratchAs != ScratchAsEnv) placement["scratchAs"] = scratchAs;
        return placement;
    }

    /// <summary>An Agent's durable workspace and nothing else: <c>{workspace:{agentId}}</c>. Null when not well formed.</summary>
    public static JsonObject? ForAgentWorkspace(string? agentId)
        => TryEntityId(agentId, out var agent)
            ? new JsonObject { ["workspace"] = new JsonObject { ["agentId"] = agent } }
            : null;

    /// <summary>
    /// A Repository's checkout, or the worktree of <paramref name="branch"/> when one was resolved:
    /// <c>{repository:{repositoryId, branch?}}</c>. Null when not well formed (a branch that is present but not
    /// valid text drops the whole placement rather than naming the checkout instead of the worktree).
    /// </summary>
    public static JsonObject? ForRepository(Guid repositoryId, string? branch)
    {
        if (repositoryId == Guid.Empty || (branch is not null && !IsText(branch))) return null;
        var repository = new JsonObject { ["repositoryId"] = repositoryId.ToString("D") };
        if (branch is not null) repository["branch"] = branch;
        return new JsonObject { ["repository"] = repository };
    }

    /// <summary>Adds <paramref name="placement"/> as the body's last member; a null one adds nothing.</summary>
    public static void AddTo(IDictionary<string, object?> body, JsonObject? placement)
    {
        if (placement is not null) body[Member] = placement;
    }

    internal static bool IsText(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= MaxTextLength && !value.Any(char.IsControl);

    private static bool TryEntityId(string? value, out string canonical)
    {
        canonical = "";
        if (!IsText(value) || !Guid.TryParse(value, out var id) || id == Guid.Empty) return false;
        canonical = id.ToString("D");
        return true;
    }
}
