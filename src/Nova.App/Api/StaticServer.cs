using System.IO;
using System.Net.NetworkInformation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using RedBamboo.AppHost.Auth;
using RedBamboo.AppHost.Extensions;
using RedBamboo.AppHost.Logging;
using Nova.App.Data;
using Nova.App.Services;

namespace Nova.App.Api;

public class StaticServer
{
    private readonly NovaEngine _engine;
    private readonly MemoryManager _memory;
    private WebApplication? _app;

    public StaticServer(NovaEngine engine, MemoryManager memory)
    {
        _engine = engine;
        _memory = memory;
    }

    public async Task StartAsync(int port, CancellationToken ct)
    {
        await WaitForPortAsync(port, ct);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
                policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
        });

        builder.Services.AddAppHostWebSocket();

        var dbDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nova");
        Directory.CreateDirectory(dbDir);
        var dbPath = Path.Combine(dbDir, "nova.db");
        builder.Services.AddDbContext<NovaDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));

        builder.Services.AddSingleton(_engine);
        builder.Services.AddSingleton(_memory);

        _app = builder.Build();
        _app.UseCors();

        _app.UseAppHostAuth(new BearerAuthOptions
        {
            GetAccessToken = () => App.Config.Tunnel.AccessToken,
            CookieName = "nova_token",
            BypassPaths = ["/ping", "/api/remote/status"],
        });

        var logService = App.LogService;
        var descriptor = new NovaServiceDescriptor(port, logService, _engine);

        _app.UseWebSockets();

        _app.MapAppHostEndpoints(
            descriptor,
            App.TunnelService,
            "Nova",
            () => new RedBamboo.AppHost.Tunnel.TunnelConfig
            {
                Enabled = App.Config.Tunnel.Enabled,
                TunnelToken = App.Config.Tunnel.TunnelToken,
                Hostname = App.Config.Tunnel.Hostname,
                CloudflaredPath = App.Config.Tunnel.CloudflaredPath,
                AccessToken = App.Config.Tunnel.AccessToken,
            },
            logService);

        _app.MapChatEndpoints(_engine);
        _app.MapSettingsEndpoints(_memory);
        _app.MapMemoryEndpoints(_memory);
        _app.MapScheduleEndpoints(_engine);

        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NovaDbContext>();
            db.Database.EnsureCreated();
        }

        var repoWebDist = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "web", "dist");
        var webRoot = Directory.Exists(repoWebDist)
            ? Path.GetFullPath(repoWebDist)
            : Path.Combine(AppContext.BaseDirectory, "wwwroot");

        if (Directory.Exists(webRoot))
        {
            _app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(webRoot),
                OnPrepareResponse = ctx =>
                {
                    if (ctx.File.Name is "index.html" or "sw.js" or "favicon.svg" or "manifest.json")
                        ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store";
                    else
                        ctx.Context.Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
                }
            });

            _app.MapFallback(async ctx =>
            {
                ctx.Response.ContentType = "text/html";
                ctx.Response.Headers["Cache-Control"] = "no-cache, no-store";
                await ctx.Response.SendFileAsync(Path.Combine(webRoot, "index.html"));
            });
        }

        await _app.StartAsync(ct);
    }

    public async Task StopAsync()
    {
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }
    }

    private static async Task WaitForPortAsync(int port, CancellationToken ct)
    {
        for (int i = 0; i < 20; i++)
        {
            ct.ThrowIfCancellationRequested();
            bool inUse = IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Any(ep => ep.Port == port);
            if (!inUse) return;
            await Task.Delay(500, ct);
        }
    }
}
