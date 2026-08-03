using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Nova;

/// <summary>
/// Exposes an existing Nova automation action as a typed workflow node. The migration
/// freezes the complete action definition into node config, so execution never reads
/// mutable behavior from the source automation entity.
/// </summary>
public sealed class AutomationFlowNodeAdapter(
    string nodeType,
    FlowNodeExecutionContract executionContract,
    IAutomationActionHandler action) : IFlowNodeHandler
{
    public string NodeType { get; } = nodeType;
    public FlowNodeExecutionContract ExecutionContract { get; } = executionContract;

    public async Task<JsonObject> ExecuteAsync(FlowNodeContext context, CancellationToken ct)
    {
        var sourceId = GuidValue(context.Config, "source_automation_id")
            ?? throw new InvalidOperationException(
                $"{NodeType} node requires source_automation_id in its frozen config");
        var attemptJobId = context.ParentJobId
            ?? (Guid.TryParse(context.ExecutionId, out var executionId)
                ? executionId
                : throw new InvalidOperationException(
                    $"{NodeType} node requires a canonical workflow Compute job"));

        var data = new JsonObject();
        foreach (var (key, value) in context.Config)
        {
            if (key is "source_automation_id" or "automation_slug"
                or "automation_name" or "action_config") continue;
            data[key] = value?.DeepClone();
        }
        foreach (var (key, value) in context.Inputs)
            data[key] = value?.DeepClone();

        var now = DateTimeOffset.UtcNow;
        var automation = new LeafEntity(
            sourceId,
            "automation",
            String(context.Config, "automation_slug") ?? $"workflow-{NodeType}",
            String(context.Config, "automation_name") ?? context.NodeLabel ?? NodeType,
            data,
            now,
            now,
            null);
        var result = await action.ExecuteAsync(new AutomationActionContext
        {
            Automation = automation,
            ActionConfig = context.Config["action_config"]?.DeepClone() as JsonObject ?? [],
            Beneficiary = context.Beneficiary,
            AttemptJobId = attemptJobId,
            CorrelationId = context.CorrelationId ?? context.ExecutionId,
        }, ct);
        return result ?? [];
    }

    private static Guid? GuidValue(JsonObject value, string key)
    {
        if (value[key] is JsonValue json && json.TryGetValue<Guid>(out var guid)) return guid;
        return Guid.TryParse(String(value, key), out var parsed) ? parsed : null;
    }

    private static string? String(JsonObject value, string key)
        => value[key] is JsonValue json && json.TryGetValue<string>(out var text) ? text : null;
}
