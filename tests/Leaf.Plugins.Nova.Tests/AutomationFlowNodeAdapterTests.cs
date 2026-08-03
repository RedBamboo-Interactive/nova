using System.Text.Json.Nodes;
using Leaf.Plugins.Nova;
using Leaf.Sdk.Services;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class AutomationFlowNodeAdapterTests
{
    [Fact]
    public async Task Adapter_builds_the_action_only_from_frozen_node_config_and_run_context()
    {
        var sourceId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var beneficiary = new ComputeBeneficiary("user", "user-1", "Laurent");
        var action = new CapturingAction();
        var adapter = new AutomationActionFlowNodeHandler("nova-session",
            FlowNodeExecutionContract.UnverifiedExternal, action);

        var output = await adapter.ExecuteAsync(new FlowNodeContext
        {
            ExecutionId = parentId.ToString(),
            FlowId = Guid.NewGuid(),
            NodeId = "session",
            NodeLabel = "Morning greeting",
            Config = new JsonObject
            {
                ["source_automation_id"] = sourceId,
                ["automation_slug"] = "morning-greeting",
                ["automation_name"] = "Morning greeting",
                ["agent"] = "nova",
                ["prompt"] = "frozen prompt",
                ["action_config"] = new JsonObject { ["qualityMode"] = "deep" },
            },
            Inputs = new JsonObject { ["prompt"] = "bound prompt" },
            Beneficiary = beneficiary,
            ParentJobId = parentId,
            CorrelationId = "correlation-1",
        }, CancellationToken.None);

        Assert.Equal("ok", output["summary"]?.GetValue<string>());
        Assert.NotNull(action.Context);
        Assert.Equal(sourceId, action.Context.Automation.Id);
        Assert.Equal("Morning greeting", action.Context.Automation.Name);
        Assert.Equal("bound prompt", action.Context.Automation.Data["prompt"]?.GetValue<string>());
        Assert.Null(action.Context.Automation.Data["action_config"]);
        Assert.Equal("deep", action.Context.ActionConfig?["qualityMode"]?.GetValue<string>());
        Assert.Equal(beneficiary, action.Context.Beneficiary);
        Assert.Equal(parentId, action.Context.AttemptJobId);
        Assert.Equal("correlation-1", action.Context.CorrelationId);
    }

    private sealed class CapturingAction : IAutomationActionHandler
    {
        public string ActionType => "nova-session";
        public AutomationActionContext? Context { get; private set; }

        public Task<JsonObject?> ExecuteAsync(AutomationActionContext context, CancellationToken ct)
        {
            Context = context;
            return Task.FromResult<JsonObject?>(new() { ["summary"] = "ok" });
        }
    }
}
