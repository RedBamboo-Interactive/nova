using RedBamboo.AppHost.Discovery;
using RedBamboo.AppHost.Logging;

namespace Nova.App.Services;

public class NovaServiceDescriptor : IServiceDescriptor
{
    private readonly int _port;
    private readonly LogService _logService;
    private readonly NovaEngine _engine;

    public NovaServiceDescriptor(int port, LogService logService, NovaEngine engine)
    {
        _port = port;
        _logService = logService;
        _engine = engine;
    }

    public string ServiceName => "Nova";
    public string Version => "0.1.0";
    public string Description => "Persistent AI companion with identity, memory, and proactive capabilities";
    public string ApiBase => $"http://localhost:{_port}";

    public Task<IReadOnlyList<CapabilityDescriptor>> GetCapabilitiesAsync()
    {
        return Task.FromResult<IReadOnlyList<CapabilityDescriptor>>(
        [
            LogEndpoints.GetLogCapabilityDescriptor(_logService),
            new CapabilityDescriptor(
                "chat",
                "Chat",
                _engine.IsRunning ? "running" : "stopped",
                Description: "AI chat with persistent context and memory"),
            new CapabilityDescriptor(
                "heartbeats",
                "Heartbeats",
                _engine.IsRunning ? "running" : "stopped",
                Description: $"{_engine.ActiveHeartbeatCount} active heartbeat(s)"),
        ]);
    }

    public IReadOnlyList<EndpointDescriptor> GetAppEndpoints()
    {
        return
        [
            new("GET",  "/api/chat/contexts",             "List active conversation contexts"),
            new("POST", "/api/chat/send",                 "Send a message in a conversation"),
            new("GET",  "/api/settings",                  "Get current settings including identity"),
            new("PUT",  "/api/settings/identity",         "Update Nova's identity"),
            new("PUT",  "/api/settings/general",          "Update general settings"),
            new("GET",  "/api/memory/manifest",           "List all memory files"),
            new("GET",  "/api/memory/file",               "Read a memory file"),
            new("GET",  "/api/schedule",                  "List scheduled tasks"),
            new("POST", "/api/schedule",                  "Create a scheduled task"),
            new("DELETE", "/api/schedule/{name}",         "Remove a scheduled task"),
            new("GET",  "/api/heartbeats",                "List active heartbeats"),
            new("POST", "/api/heartbeats",                "Create a heartbeat"),
            new("DELETE", "/api/heartbeats/{name}",       "Remove a heartbeat"),
        ];
    }

    public Task<object?> GetHealthExtrasAsync()
    {
        return Task.FromResult<object?>(new
        {
            engineRunning = _engine.IsRunning,
            activeContexts = _engine.GetAllContexts().Count,
            activeHeartbeats = _engine.ActiveHeartbeatCount,
        });
    }
}
