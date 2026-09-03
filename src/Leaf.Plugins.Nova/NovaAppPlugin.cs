using System.Reflection;
using Leaf.Plugins.Nova.Endpoints;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Leaf.Plugins.Nova;

/// <summary>
/// Nova-the-APP as a Leaf plugin: the chat/discussion UI and its glue endpoints
/// (send, delegate, ask, callbacks, and journal). Nova-the-AGENT —
/// the agent entity, workspace VFS mount, memory, dreaming automations — is a kernel
/// capability and deliberately NOT here.
/// </summary>
public sealed class NovaAppPlugin : ILeafPlugin
{
    public const string PluginId = "nova";

    public PluginManifest Manifest { get; } = LoadManifest();

    private static PluginManifest LoadManifest()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("plugin.json")
            ?? throw new InvalidOperationException("Embedded plugin.json is missing");
        using var reader = new StreamReader(stream);
        return PluginManifest.Parse(reader.ReadToEnd());
    }

    public void ConfigureServices(IServiceCollection services, PluginContext ctx)
    {
        services.AddSingleton<IAgentTemplateProvider, NovaAgentTemplateProvider>();
        services.AddSingleton<IAgentWelcomeProvider>(sp =>
            new NovaAgentWelcomeProvider(
                sp.GetRequiredService<DiscussionStore>(),
                sp.GetRequiredService<AgentDirectory>(),
                sp.GetRequiredService<AgentWorkspaces>(),
                sp.GetRequiredService<MessagePipeline>(),
                sp.GetRequiredService<RedComputeClient>(),
                sp.GetRequiredService<ILogger<NovaAgentWelcomeProvider>>(),
                sp.GetRequiredService<HeartbeatService>()));
        services.AddSingleton<RedComputeClient>();
        services.AddSingleton(sp =>
            new AgentDirectory(
                sp.GetRequiredKeyedService<IEntityStore>(PluginId),
                sp.GetRequiredKeyedService<IPluginEvents>(PluginId)));
        services.AddSingleton<IEntityDisplayEnricher>(sp =>
            new NovaAgentEntityDisplayEnricher(sp.GetRequiredService<AgentDirectory>()));
        services.AddSingleton(sp =>
            new AgentWorkspaces(
                sp.GetRequiredService<AgentDirectory>(),
                sp.GetRequiredService<IAgentWorkspacePathResolver>()));
        services.AddSingleton(sp =>
            new DiscussionStore(sp.GetRequiredKeyedService<IEntityStore>(PluginId), sp.GetRequiredService<IDiscussions>()));
        services.AddSingleton<ConversationUnread>();
        services.AddSingleton(sp => new ConfidentialSessionBackfill(
            sp.GetRequiredService<DiscussionStore>(),
            sp.GetRequiredService<AgentDirectory>(),
            sp.GetRequiredService<RedComputeClient>(),
            sp.GetRequiredKeyedService<IEntityStore>(PluginId),
            sp.GetRequiredService<ILogger<ConfidentialSessionBackfill>>()));
        services.AddSingleton(sp =>
            new EventInjector(
                sp.GetRequiredService<IDiscussions>(),
                sp.GetRequiredKeyedService<IPluginEvents>(PluginId),
                sp.GetRequiredService<RedComputeClient>(),
                sp.GetRequiredService<AgentDirectory>(),
                sp.GetRequiredKeyedService<IEntityStore>(PluginId),
                sp.GetRequiredService<ConversationUnread>()));
        services.AddSingleton(sp =>
            new LiveEvents(
                sp.GetRequiredService<DiscussionStore>(),
                sp.GetRequiredService<EventInjector>(),
                sp.GetRequiredService<AgentDirectory>()));
        services.AddSingleton<DiscussionActivity>();
        services.AddSingleton(sp =>
            new DiscussionLifecycle(sp.GetRequiredService<DiscussionStore>(), sp.GetRequiredService<RedComputeClient>()));
        // Automation action "nova-session" — collected by the kernel after build and
        // dispatched from its AutomationService for entities with that action_type.
        services.AddSingleton(sp =>
            new NovaSessionActionHandler(
                sp.GetRequiredService<AgentDirectory>(),
                sp.GetRequiredService<AgentWorkspaces>(),
                sp.GetRequiredService<DiscussionStore>(),
                sp.GetRequiredService<EventInjector>(),
                sp.GetRequiredService<RedComputeClient>(),
                sp.GetRequiredKeyedService<IEntityStore>(PluginId),
                sp.GetRequiredService<IAgentScratchSpace>()));
        services.AddSingleton<IAutomationActionHandler>(sp =>
            sp.GetRequiredService<NovaSessionActionHandler>());
        services.AddSingleton<IFlowNodeHandler>(sp => new AutomationActionFlowNodeHandler(
            "nova-session",
            new FlowNodeExecutionContract(
                "nova-session/1", FlowNodeEffect.External,
                FlowNodeDeterminism.Nondeterministic, FlowNodeCachePolicy.Never,
                FlowNodeRecoveryPolicy.AtLeastOnce,
                FlowNodeCancellationPolicy.Cooperative, 7_200),
            sp.GetRequiredService<NovaSessionActionHandler>()));
        services.AddSingleton(sp => new ExtensionContributions(
            sp.GetRequiredKeyedService<IPluginExtensions>(PluginId),
            sp.GetRequiredService<LiveEvents>()));
        services.AddSingleton<DeviceResolver>();
        services.AddSingleton(sp =>
            new ConversationExporter(sp.GetRequiredService<RedComputeClient>(), sp.GetRequiredService<IDiscussions>()));
        services.AddSingleton<IShareEnricher>(sp =>
            new NovaShareEnricher(
                sp.GetRequiredService<DiscussionStore>(),
                sp.GetRequiredService<AgentDirectory>(),
                sp.GetRequiredService<RedComputeClient>(),
                sp.GetRequiredService<IDiscussions>()));
        services.AddSingleton<LivePoller>();
        services.AddSingleton(sp =>
            new HeartbeatService(
                sp.GetRequiredKeyedService<IEntityStore>(PluginId),
                sp.GetRequiredKeyedService<IWorkflowAutomations>(PluginId),
                sp.GetRequiredService<IDiscussions>(),
                sp.GetRequiredService<DiscussionStore>(),
                sp.GetRequiredService<DiscussionLifecycle>(),
                sp.GetRequiredService<MessagePipeline>(),
                sp.GetRequiredService<RedComputeClient>(),
                sp.GetRequiredService<EventInjector>(),
                sp.GetRequiredService<AgentDirectory>(),
                sp.GetRequiredService<ExtensionContributions>()));
        // Automation action "heartbeat-tick" — one tick of the per-agent heartbeat.
        services.AddSingleton(sp =>
            new HeartbeatTickHandler(
                sp.GetRequiredService<HeartbeatService>(),
                sp.GetRequiredService<DiscussionStore>(),
                sp.GetRequiredKeyedService<IEntityStore>(PluginId),
                sp.GetRequiredService<EventInjector>()));
        services.AddSingleton<IAutomationActionHandler>(sp =>
            sp.GetRequiredService<HeartbeatTickHandler>());
        services.AddSingleton<IFlowNodeHandler>(sp => new AutomationActionFlowNodeHandler(
            "heartbeat-tick",
            new FlowNodeExecutionContract(
                "heartbeat-tick/1", FlowNodeEffect.External,
                FlowNodeDeterminism.Nondeterministic, FlowNodeCachePolicy.Never,
                FlowNodeRecoveryPolicy.AtLeastOnce,
                FlowNodeCancellationPolicy.Cooperative, 3_600),
            sp.GetRequiredService<HeartbeatTickHandler>()));
        services.AddSingleton(sp =>
            new MessagePipeline(
                sp.GetRequiredService<DiscussionStore>(),
                sp.GetRequiredService<IDiscussions>(),
                sp.GetRequiredKeyedService<IEntityStore>(PluginId),
                sp.GetRequiredService<IAssets>(),
                sp.GetRequiredService<RedComputeClient>(),
                sp.GetRequiredService<AgentDirectory>(),
                sp.GetRequiredService<AgentWorkspaces>(),
                sp.GetRequiredService<IAgentScratchSpace>(),
                sp.GetRequiredService<ExtensionContributions>(),
                sp.GetRequiredService<ConversationUnread>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MessagePipeline>>()));
    }

    public void MapEndpoints(RouteGroupBuilder group)
    {
        // Discussion responses can contain private conversational state. Keep the
        // whole app surface out of intermediary and browser HTTP caches.
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "private, no-store";
            return await next(context);
        });
        DiscussionEndpoints.Map(group);
        AskEndpoints.Map(group);
        DelegateEndpoints.Map(group);
        CallbackEndpoints.Map(group);
        AgentEndpoints.Map(group);
        HeartbeatEndpoints.Map(group);
        MiscEndpoints.Map(group);
    }

    public async Task OnStartupAsync(IPluginHost host, CancellationToken ct)
    {
        var store = host.GetRequiredService<IEntityStore>();
        var agents = host.GetRequiredService<AgentDirectory>();

        // The old standalone Nova config duplicated Agent quality/workspace settings.
        // Agent entities and RedLeaf's VFS are now authoritative. Preserve unrelated
        // legacy integration data until its owning extensions migrate it.
        var legacyConfigs = await store.QueryAsync(
            new EntityQuery { TypeSlug = "nova-config", Limit = 10 }, ct);
        foreach (var legacyConfig in legacyConfigs)
        {
            var data = legacyConfig.Data.DeepClone().AsObject();
            var changed = data.Remove("default_quality_mode");
            changed |= data.Remove("workspace_path");
            if (changed)
                await store.ReplaceDataAsync(legacyConfig.Id, data, ct: ct);
        }

        // Resolve Nova's agent entity (kernel-seeded). The agent system is kernel-level;
        // the app only needs the id for defaults (new discussions and LIVE timeline).
        var agentEntities = await store.QueryAsync(new EntityQuery { TypeSlug = "agent", Limit = 50 }, ct);
        var nova = agentEntities.FirstOrDefault(a => a.Slug == "nova");
        agents.NovaAgentId = nova?.Id.ToString();

        // data.live owns the paired LIVE + Heartbeat lifecycle. The canonical
        // automation owns its schedule and workflow action configuration.
        var heartbeat = host.GetRequiredService<HeartbeatService>();
        _ = Task.Run(async () =>
        {
            try { await heartbeat.ReconcileAsync(); }
            catch { /* reconcile endpoint retries on demand */ }
        }, CancellationToken.None);

        // Optional extensions attach through generic backend slots. Nova does not
        // know which contributors are installed.
        host.GetRequiredService<ExtensionContributions>().Start();

        // Keep the remaining Smart Home poller independent of Presence availability.

        // Ambient LIVE timeline (Spotify/Sonos/Hue). Plugins have no
        // shutdown hook, so the loop binds to ApplicationStopping, not the boot ct.
        var lifetime = host.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>();
        var confidentialBackfill = host.GetRequiredService<ConfidentialSessionBackfill>();
        _ = Task.Run(() => confidentialBackfill.RunAsync(lifetime.ApplicationStopping),
            CancellationToken.None);
        var poller = host.GetRequiredService<LivePoller>();
        _ = Task.Run(() => poller.RunAsync(lifetime.ApplicationStopping), CancellationToken.None);

        // Archive reconciler: finishes archives whose session stop was never confirmed
        // (RedCompute down, restart between intent and finalization).
        var lifecycle = host.GetRequiredService<DiscussionLifecycle>();
        _ = Task.Run(() => lifecycle.RunReconcilerAsync(lifetime.ApplicationStopping), CancellationToken.None);
    }

}
