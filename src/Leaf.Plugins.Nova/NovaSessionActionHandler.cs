using System.Text.Json.Nodes;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Nova;

/// <summary>
/// Automation action <c>nova-session</c> — the kernel's <c>ai-session</c> plus the
/// Nova-specific delivery flow the standalone app used to provide server-side:
/// an optional pre-created discussion announced to the session via a
/// <c>&lt;nova-context&gt;</c> header, and an optional completion report injected
/// into a target discussion (<c>report_to_discussion_id</c>).
///
/// Entity data shape (same keys as kernel ai-session automations):
///   agent   — agent entity id or slug (defaults to Nova)
///   prompt  — the task prompt
///   action_config { preCreateDiscussion?, qualityMode?, timeout? }
/// </summary>
public sealed class NovaSessionActionHandler(
    AgentDirectory agents,
    AgentWorkspaces workspaces,
    DiscussionStore discussions,
    EventInjector injector,
    RedComputeClient redCompute) : IAutomationActionHandler
{
    private static readonly string[] DefaultTools =
        ["Read", "Write", "Edit", "Glob", "Grep", "Bash", "WebFetch", "WebSearch", "TodoWrite"];

    public string ActionType => "nova-session";

    public async Task<JsonObject?> ExecuteAsync(AutomationActionContext context, CancellationToken ct)
    {
        var automation = context.Automation;
        var data = automation.Data;
        var config = context.ActionConfig;

        var prompt = Str(data, "prompt");
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("nova-session automation has no prompt");

        var agent = await ResolveAgentAsync(Str(data, "agent"), ct)
            ?? throw new InvalidOperationException($"Agent not found: {Str(data, "agent") ?? "(default)"}");

        var workspace = await workspaces.GetAsync(agent.Id, ct);
        workspace.GenerateClaudeMd();

        var ownerId = Str(data, "owner_id") ?? automation.CreatedBy;

        // Pre-create the delivery discussion and tell the session about it — same
        // contract the standalone app's automations relied on.
        DiscussionRead? preCreated = null;
        if (Bool(config, "preCreateDiscussion"))
            preCreated = await discussions.CreateAsync(null, agent.Id, ownerId, "chat", ct);

        var fullPrompt = ComposePrompt(automation.Name, agent, preCreated, prompt);

        var timeout = IntOr(config, "timeout") ?? 3600;
        var body = new Dictionary<string, object?>
        {
            ["prompt"] = fullPrompt,
            ["qualityTier"] = Str2(config, "qualityMode"),
            ["provider"] = agent.Provider,
            ["workingDir"] = workspace.WorkspacePath,
            ["allowedTools"] = DefaultTools,
            ["maxTurns"] = 50,
            ["timeout"] = timeout,
        };

        var result = await redCompute.ExecuteAsync(body, $"Nova: {automation.Name}", ownerId, timeout, ct);
        if (!result.Success)
            throw new InvalidOperationException(result.Error ?? "AI session reported failure");

        var summary = string.IsNullOrWhiteSpace(result.Text) ? "(no output)" : result.Text;

        if (Str(data, "report_to_discussion_id") is { } reportTo)
        {
            var target = await discussions.GetAsync(reportTo, ct);
            if (target != null)
                await injector.InjectAsync(target, summary, null, $"automation:{automation.Name}", ct: ct);
        }

        return new JsonObject
        {
            ["summary"] = summary,
            ["sessionId"] = result.SessionId,
            ["discussionId"] = preCreated?.Id,
        };
    }

    private async Task<AgentInfo?> ResolveAgentAsync(string? reference, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(reference))
        {
            var all = await agents.GetAgentsAsync(ct: ct);
            var match = all.FirstOrDefault(a => a.Id == reference || a.Slug == reference);
            if (match != null) return match;
        }
        return agents.NovaAgentId != null ? await agents.GetAgentAsync(agents.NovaAgentId, ct) : null;
    }

    private static string ComposePrompt(string automationName, AgentInfo agent, DiscussionRead? preCreated, string prompt)
    {
        // /ai-session/execute takes a single prompt — fold the agent identity in ahead
        // of the task, then the run context, then the task itself. CLAUDE.md in the
        // workspace carries the full protocol; the fold-in keeps identity primary.
        var now = DateTimeOffset.Now;
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(agent.Identity))
            parts.Add(agent.Identity);

        parts.Add($"""
            # Automation: {automationName}
            Current date: {now:yyyy-MM-dd} ({now:dddd}), {now:HH:mm} local time.
            You are running an automated task. Be concise.
            If there is nothing to do, say so briefly.
            """);

        if (preCreated != null)
        {
            parts.Add($"""
                <nova-context pre-created-discussion="{preCreated.Id}">
                A discussion has already been created for you with ID: {preCreated.Id}.
                Post your content to it using: POST http://127.0.0.1:18804/api/apps/nova/discussions/{preCreated.Id}/nova-message
                Do NOT create a new discussion — use the one provided.
                </nova-context>
                """);
        }

        parts.Add(prompt);
        return string.Join("\n\n---\n\n", parts);
    }

    private static string? Str(JsonObject data, string key)
        => data[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string? Str2(JsonObject? data, string key)
        => data?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool Bool(JsonObject? obj, string key)
    {
        if (obj?[key] is not JsonValue v) return false;
        if (v.TryGetValue<bool>(out var b)) return b;
        return v.TryGetValue<string>(out var s) && string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static int? IntOr(JsonObject? obj, string key)
    {
        if (obj?[key] is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d)) return (int)d;
        if (v.TryGetValue<string>(out var s) && int.TryParse(s, out var parsed)) return parsed;
        return null;
    }
}
