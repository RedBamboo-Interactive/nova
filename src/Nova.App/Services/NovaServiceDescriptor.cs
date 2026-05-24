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
                Description: "AI chat via Claude Code sessions (proxied to RedCompute)"),
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
            new("GET",    "/api/discussions",              "List discussions (filter: status, search)"),
            new("POST",   "/api/discussions",              "Create a new discussion (starts a session)"),
            new("GET",    "/api/discussions/pending",      "Count pending discussions"),
            new("GET",    "/api/discussions/{id}",         "Get discussion metadata"),
            new("PUT",    "/api/discussions/{id}/title",   "Update discussion title"),
            new("DELETE", "/api/discussions/{id}",         "Archive a discussion"),
            new("GET",    "/api/discussions/search",       "Search conversation content (query: q, limit)"),
            new("GET",    "/api/discussions/export",       "Export conversations as markdown (query: since, limit)"),
            new("GET",    "/api/settings",                 "Get current settings including identity"),
            new("PUT",    "/api/settings/identity",        "Update Nova's identity"),
            new("GET",    "/api/memory/manifest",          "List all memory files"),
            new("GET",    "/api/memory/file",              "Read a memory file"),
            new("PUT",    "/api/memory/file",              "Write a memory file"),
            new("GET",    "/api/schedule",                 "List scheduled tasks"),
            new("POST",   "/api/schedule",                 "Create a scheduled task"),
            new("DELETE", "/api/schedule/{name}",          "Remove a scheduled task"),
            new("GET",    "/api/heartbeats",               "List active heartbeats"),
            new("POST",   "/api/heartbeats",               "Create a heartbeat"),
            new("DELETE", "/api/heartbeats/{name}",        "Remove a heartbeat"),
            new("POST",   "/api/speech/transcribe",        "Transcribe audio (multipart form)"),
            new("POST",   "/api/speech/speak",             "Text-to-speech"),
            new("POST",   "/api/speech/prompt",            "Execute a prompt via RedCompute"),
            new("GET",    "/api/file",                     "Serve an image file by path"),
        ];
    }

    public Task<object?> GetHealthExtrasAsync()
    {
        return Task.FromResult<object?>(new
        {
            engineRunning = _engine.IsRunning,
            activeHeartbeats = _engine.ActiveHeartbeatCount,
        });
    }
}
