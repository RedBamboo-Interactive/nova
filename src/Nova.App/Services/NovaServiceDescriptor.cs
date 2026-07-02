using RedBamboo.AppHost.Discovery;
using RedBamboo.AppHost.Logging;

namespace Nova.App.Services;

public class NovaServiceDescriptor : RegistryServiceDescriptor
{
    private readonly int _port;
    private readonly LogService _logService;
    private readonly NovaEngine _engine;

    private bool _redComputeUp;
    private DateTime _redComputeCheckedAt = DateTime.MinValue;

    public NovaServiceDescriptor(int port, LogService logService, NovaEngine engine, EndpointRegistry registry)
        : base(registry)
    {
        _port = port;
        _logService = logService;
        _engine = engine;
    }

    public override string ServiceName => "Nova";
    public override string Version => "0.1.0";
    public override string Description => "Persistent AI companion with identity, memory, and proactive capabilities";
    public override string ApiBase => $"http://localhost:{_port}";
    public override string? IconClass => "fa-solid fa-star";
    public override string? IconColor => "#C74B7A";

    private static readonly string[] ChatEndpointKeys =
    [
        "GET /api/discussions",
        "POST /api/discussions",
        "GET /api/discussions/{id}",
        "GET /api/discussions/live",
        "PUT /api/discussions/{id}/title",
        "DELETE /api/discussions/{id}",
        "POST /api/discussions/{id}/message",
        "POST /api/discussions/{id}/event",
        "POST /api/discussions/{id}/clear",
        "GET /api/event-types",
        "GET /api/discussions/search",
        "GET /api/discussions/export",
        "GET /api/discussions/{id}/export",
        "POST /api/ask",
        "POST /api/discussions/{id}/messages/{messageId}/reactions",
        "DELETE /api/discussions/{id}/messages/{messageId}/reactions/{emoji}",
        "GET /api/discussions/{id}/reactions",
        "GET /api/discussions/{id}/messages/{messageId}/reactions",
    ];

    private static readonly string[] AutomationEndpointKeys =
    [
        "GET /api/automations",
        "GET /api/automations/{name}",
        "POST /api/automations",
        "PUT /api/automations/{name}",
        "POST /api/automations/{name}/trigger",
        "DELETE /api/automations/{name}",
    ];

    public override async Task<IReadOnlyList<CapabilityDescriptor>> GetCapabilitiesAsync()
    {
        var redComputeUp = await IsRedComputeAvailableAsync();
        var byKey = Registry.GetEndpoints()
            .GroupBy(e => $"{e.Method} {e.Path}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        return
        [
            LogEndpoints.GetLogCapabilityDescriptor(_logService),
            new CapabilityDescriptor(
                "chat",
                "Chat",
                !_engine.IsRunning ? "stopped" : redComputeUp ? "running" : "error",
                Description: redComputeUp
                    ? "AI chat via Claude Code sessions (proxied to RedCompute)"
                    : "AI chat via Claude Code sessions — RedCompute is unreachable, chat cannot run",
                Endpoints: Pick(byKey, ChatEndpointKeys)),
            new CapabilityDescriptor(
                "automations",
                "Automations",
                _engine.IsRunning ? "running" : "stopped",
                Description: $"{_engine.ActiveAutomationCount} active automation(s)",
                Endpoints: Pick(byKey, AutomationEndpointKeys)),
        ];
    }

    /// <summary>RedCompute /ping with a 30s memo so /discover stays fast.</summary>
    private async Task<bool> IsRedComputeAvailableAsync()
    {
        if (DateTime.UtcNow - _redComputeCheckedAt < TimeSpan.FromSeconds(30))
            return _redComputeUp;

        _redComputeUp = await _engine.RedCompute.IsAvailableAsync();
        _redComputeCheckedAt = DateTime.UtcNow;
        return _redComputeUp;
    }

    private static IReadOnlyList<EndpointDescriptor>? Pick(
        Dictionary<string, EndpointDescriptor> byKey, string[] keys)
    {
        var picked = keys
            .Select(k => byKey.GetValueOrDefault(k))
            .OfType<EndpointDescriptor>()
            .ToList();
        return picked.Count > 0 ? picked : null;
    }

    public override Task<object?> GetHealthExtrasAsync()
    {
        return Task.FromResult<object?>(new
        {
            engineRunning = _engine.IsRunning,
            activeAutomations = _engine.ActiveAutomationCount,
        });
    }
}
