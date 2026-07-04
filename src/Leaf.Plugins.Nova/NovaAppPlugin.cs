using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Plugins.Nova.Endpoints;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Leaf.Plugins.Nova;

/// <summary>
/// Nova-the-APP as a Leaf plugin: the chat/discussion UI and its glue endpoints
/// (send, delegate, ask, callbacks, journal, outfits, location). Nova-the-AGENT —
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
        services.AddSingleton(sp =>
            new NovaConfigStore(sp.GetRequiredKeyedService<IEntityStore>(PluginId)));
        services.AddSingleton<RedComputeClient>();
        services.AddSingleton(sp =>
            new AgentDirectory(sp.GetRequiredKeyedService<IEntityStore>(PluginId), sp.GetRequiredService<NovaConfigStore>()));
        services.AddSingleton(sp =>
            new AgentWorkspaces(sp.GetRequiredService<AgentDirectory>()));
        services.AddSingleton(sp =>
            new DiscussionStore(sp.GetRequiredKeyedService<IEntityStore>(PluginId), sp.GetRequiredService<IDiscussions>()));
        services.AddSingleton(sp =>
            new EventInjector(
                sp.GetRequiredService<IDiscussions>(),
                sp.GetRequiredKeyedService<IPluginEvents>(PluginId),
                sp.GetRequiredService<RedComputeClient>(),
                sp.GetRequiredService<AgentDirectory>()));
        services.AddSingleton(sp =>
            new LiveEvents(
                sp.GetRequiredService<DiscussionStore>(),
                sp.GetRequiredService<EventInjector>(),
                sp.GetRequiredService<AgentDirectory>()));
        services.AddSingleton<DiscussionActivity>();
        // Automation action "nova-session" — collected by the kernel after build and
        // dispatched from its AutomationService for entities with that action_type.
        services.AddSingleton<IAutomationActionHandler>(sp =>
            new NovaSessionActionHandler(
                sp.GetRequiredService<AgentDirectory>(),
                sp.GetRequiredService<AgentWorkspaces>(),
                sp.GetRequiredService<DiscussionStore>(),
                sp.GetRequiredService<EventInjector>(),
                sp.GetRequiredService<RedComputeClient>()));
        services.AddSingleton<LocationService>();
        services.AddSingleton<GeoLocationService>();
        services.AddSingleton(sp =>
            new DeviceResolver(sp.GetRequiredKeyedService<IEntityStore>(PluginId)));
        services.AddSingleton(sp =>
            new ConversationExporter(sp.GetRequiredService<RedComputeClient>(), sp.GetRequiredService<IDiscussions>()));
        services.AddSingleton<LivePoller>();
        services.AddSingleton(sp =>
            new MessagePipeline(
                sp.GetRequiredService<DiscussionStore>(),
                sp.GetRequiredService<IDiscussions>(),
                sp.GetRequiredKeyedService<IEntityStore>(PluginId),
                sp.GetRequiredService<RedComputeClient>(),
                sp.GetRequiredService<AgentDirectory>(),
                sp.GetRequiredService<AgentWorkspaces>(),
                sp.GetRequiredService<NovaConfigStore>(),
                sp.GetRequiredService<LocationService>(),
                sp.GetRequiredService<GeoLocationService>()));
    }

    public void MapEndpoints(RouteGroupBuilder group)
    {
        DiscussionEndpoints.Map(group);
        AskEndpoints.Map(group);
        DelegateEndpoints.Map(group);
        CallbackEndpoints.Map(group);
        AgentEndpoints.Map(group);
        MiscEndpoints.Map(group);
    }

    public async Task OnStartupAsync(IPluginHost host, CancellationToken ct)
    {
        var store = host.GetRequiredService<IEntityStore>();
        var agents = host.GetRequiredService<AgentDirectory>();

        // The config singleton is created here, NOT seeded: plugin seeds re-upsert on
        // every boot, which would clobber user-entered values. First boot migrates the
        // standalone app's %LOCALAPPDATA%\Nova\config.json values.
        var existing = await store.QueryAsync(new EntityQuery { TypeSlug = NovaConfigStore.TypeSlug, Limit = 1 }, ct);
        if (existing.Count == 0)
        {
            var (dockerImage, qualityMode, workspacePath) = ReadLegacyConfig();
            await store.UpsertBySlugAsync(NovaConfigStore.TypeSlug, NovaConfigStore.Slug, "Nova App Config",
                new JsonObject
                {
                    ["docker_image"] = dockerImage,
                    ["default_quality_mode"] = qualityMode ?? "standard",
                    ["workspace_path"] = workspacePath,
                }, ct);
        }

        // Resolve Nova's agent entity (kernel-seeded). The agent system is kernel-level;
        // the app only needs the id for defaults (new discussions, LIVE timeline, outfits).
        var agentEntities = await store.QueryAsync(new EntityQuery { TypeSlug = "agent", Limit = 50 }, ct);
        var nova = agentEntities.FirstOrDefault(a => a.Slug == "nova");
        agents.NovaAgentId = nova?.Id.ToString();

        // Ensure the LIVE timeline discussion exists for Nova.
        if (agents.NovaAgentId != null)
        {
            var discussionStore = host.GetRequiredService<DiscussionStore>();
            var all = await discussionStore.ListAsync(agents.NovaAgentId, ct);
            if (!all.Any(d => d.Type == "live" && d.AgentId == agents.NovaAgentId && d.Status != "archived"))
                await discussionStore.CreateAsync($"{nova!.Name} Live", agents.NovaAgentId, "local-user", "live", ct);
        }

        // Steam credentials for the LIVE poller: migrate from the standalone app's
        // config.json once, if the singleton doesn't carry them yet.
        var configStore = host.GetRequiredService<NovaConfigStore>();
        var raw = await configStore.GetRawAsync(ct);
        if (raw["steam_api_key"] is null)
        {
            var (steamKey, steamId) = ReadLegacySteamConfig();
            if (steamKey != null)
                await configStore.PatchAsync(new JsonObject { ["steam_api_key"] = steamKey, ["steam_id"] = steamId }, ct);
        }

        // Coarse IP geolocation for the context block — fire and forget.
        _ = host.GetRequiredService<GeoLocationService>().ResolveAsync();

        // Ambient LIVE timeline (Spotify/Sonos/Hue/weather/Steam). Plugins have no
        // shutdown hook, so the loop binds to ApplicationStopping, not the boot ct.
        var lifetime = host.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>();
        var poller = host.GetRequiredService<LivePoller>();
        _ = Task.Run(() => poller.RunAsync(lifetime.ApplicationStopping), CancellationToken.None);
    }

    private static (string? ApiKey, string? SteamId) ReadLegacySteamConfig()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nova", "config.json");
            if (!File.Exists(path)) return (null, null);

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("Steam", out var steam)) return (null, null);
            return (
                steam.TryGetProperty("ApiKey", out var k) ? k.GetString() : null,
                steam.TryGetProperty("SteamId", out var s) ? s.GetString() : null);
        }
        catch
        {
            return (null, null);
        }
    }

    private static (string? DockerImage, string? QualityMode, string? WorkspacePath) ReadLegacyConfig()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nova", "config.json");
            if (!File.Exists(path)) return (null, null, null);

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            return (
                root.TryGetProperty("DockerImage", out var d) ? d.GetString() : null,
                root.TryGetProperty("DefaultQualityMode", out var q) ? q.GetString() : null,
                root.TryGetProperty("WorkspacePath", out var w) ? w.GetString() : null);
        }
        catch
        {
            return (null, null, null);
        }
    }
}
