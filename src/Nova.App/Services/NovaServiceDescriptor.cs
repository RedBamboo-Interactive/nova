using RedBamboo.AppHost.Discovery;
using RedBamboo.AppHost.Logging;

namespace Nova.App.Services;

public class NovaServiceDescriptor : RegistryServiceDescriptor
{
    private readonly int _port;
    private readonly LogService _logService;
    private readonly NovaEngine _engine;

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

    public override Task<IReadOnlyList<CapabilityDescriptor>> GetCapabilitiesAsync()
    {
        return Task.FromResult<IReadOnlyList<CapabilityDescriptor>>(
        [
            LogEndpoints.GetLogCapabilityDescriptor(_logService),
            new CapabilityDescriptor(
                "chat",
                "Chat",
                _engine.IsRunning ? "running" : "stopped",
                Description: "AI chat via Claude Code sessions (proxied to RedCompute)"),
            new CapabilityDescriptor(
                "automations",
                "Automations",
                _engine.IsRunning ? "running" : "stopped",
                Description: $"{_engine.ActiveAutomationCount} active automation(s)"),
        ]);
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
