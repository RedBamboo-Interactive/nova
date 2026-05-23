using System.Windows;
using Nova.App.Api;
using Nova.App.Configuration;
using Nova.App.Services;
using RedBamboo.AppHost.Logging;
using RedBamboo.AppHost.Tray;
using RedBamboo.AppHost.Tunnel;

namespace Nova.App;

public partial class App : Application
{
    private static Mutex? _mutex;
    private CancellationTokenSource? _serverCts;
    private StaticServer? _server;
    private TrayIconManager? _trayIcon;

    public static ConfigManager ConfigManager { get; } = new();
    public static NovaConfig Config => ConfigManager.Config;

    public static CloudflareTunnelService TunnelService { get; } = new(logger: null);

    public static LogService LogService { get; } = new(new LogServiceOptions
    {
        Source = "nova",
        BufferCapacity = 2048
    });

    public static FileLogger FileLogger { get; } = new("Nova");

    public static NovaEngine Engine { get; private set; } = null!;
    public static MemoryManager Memory { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        FileLogger.AttachTo(LogService);

        _mutex = new Mutex(true, @"Global\Nova_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            LogService.Warn("app", "Another instance is already running");
            MessageBox.Show("Nova is already running.", "Nova", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        ConfigManager.Load();
        LogService.Info("app", "Configuration loaded");

        Memory = new MemoryManager(Config.WorkspacePath, LogService);
        Memory.EnsureDirectories();
        Memory.GenerateClaudeMd();

        Engine = new NovaEngine(Config, Memory, LogService);

        _serverCts = new CancellationTokenSource();
        _server = new StaticServer(Engine, Memory);
        try
        {
            await _server.StartAsync(Config.Port, _serverCts.Token);
            LogService.Info("server", $"Server started on port {Config.Port}");
        }
        catch (Exception ex)
        {
            LogService.Critical("server", $"Server failed to start on port {Config.Port}", stackTrace: ex.ToString());
            MessageBox.Show($"Server failed to start:\n{ex.Message}", "Nova", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        if (Config.Tunnel.Enabled)
        {
            await TunnelService.StartAsync(new TunnelConfig
            {
                Enabled = true,
                TunnelToken = Config.Tunnel.TunnelToken,
                Hostname = Config.Tunnel.Hostname,
                CloudflaredPath = Config.Tunnel.CloudflaredPath,
                AccessToken = Config.Tunnel.AccessToken,
            });
        }

        _trayIcon = new TrayIconManager(new TrayIconConfig
        {
            AppName = "Nova",
            Port = Config.Port,
            LoadIcon = () => IconHelper.CreateTrayIcon(StatusColors.Magenta, TrayIcons.Star),
            GetStatusLines = () => Task.FromResult<IReadOnlyList<string>>(
            [
                $"Engine: {(Engine.IsRunning ? "active" : "idle")}",
                $"Heartbeats: {Engine.ActiveHeartbeatCount}",
                $"Tunnel: {TunnelService.Status}",
            ]),
        });
        _trayIcon.Initialize();

        await Engine.StartAsync(_serverCts.Token);
        LogService.Info("app", "Nova started");
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        LogService.Info("app", "Nova shutting down");
        await Engine.StopAsync();
        await TunnelService.DisposeAsync();
        _serverCts?.Cancel();
        if (_server != null)
            await _server.StopAsync();
        _trayIcon?.Dispose();
        await FileLogger.DisposeAsync();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
