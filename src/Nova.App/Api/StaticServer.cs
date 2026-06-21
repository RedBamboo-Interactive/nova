using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using RedBamboo.AppHost;
using RedBamboo.AppHost.Auth;
using RedBamboo.AppHost.Discovery;
using RedBamboo.AppHost.Extensions;
using RedBamboo.AppHost.Logging;
using RedBamboo.AppHost.Streams;
using RedBamboo.AppHost.WebSockets;
using Nova.App.Data;
using Nova.App.Services;

namespace Nova.App.Api;

public class StaticServer
{
    private readonly NovaEngine _engine;
    private readonly MemoryManager _memory;
    private WebApplication? _app;
    private RedLeafStreamClient? _streamClient;

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
        builder.Services.AddAppHostTelemetry(opts => opts.AppName = "Nova");

        var dbDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nova");
        Directory.CreateDirectory(dbDir);
        var dbPath = Path.Combine(dbDir, "nova.db");
        builder.Services.AddDbContext<NovaDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));

        builder.Services.AddSingleton(_engine);
        builder.Services.AddSingleton(_memory);


        var redSuiteDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RedSuite");
        var signingKey = SigningKeyPersistence.EnsureSigningKey(redSuiteDir);
        var googleAuth = SigningKeyPersistence.LoadGoogleOAuth(redSuiteDir);
        builder.Services.AddAppHostAuth(new AuthOptions
        {
            Jwt = new JwtOptions { SigningKey = signingKey },
            Google = googleAuth,
            Mode = googleAuth != null ? AuthMode.Required : AuthMode.LocalDefault,
        });

        _app = builder.Build();

        var authFactory = _app.Services.GetRequiredService<AuthenticatedHttpClientFactory>();
        _engine.RedCompute.SetAuthFactory(authFactory);
        _engine.SetServiceScopeFactory(_app.Services.GetRequiredService<IServiceScopeFactory>());

        // Mirror discussions/messages/invocations to RedLeaf (dual-write;
        // local SQLite stays the read path until reads cut over).
        _streamClient = new RedLeafStreamClient(
            App.Config.Suite.RedLeaf, "nova",
            new JwtService(new JwtOptions { SigningKey = signingKey }),
            App.LogService);
        _streamClient.DefineEntityType(new EntityTypeDefinition(
            "discussion", "Discussion",
            "Nova chat discussion",
            Icon: "fa-solid fa-comments", Color: "fuchsia", Versioning: false,
            Fields:
            [
                new { name = "Status", fieldType = "string", description = "idle, thinking, stopped or archived" },
                new { name = "Owner ID", fieldType = "string" },
                new { name = "Session ID", fieldType = "string", description = "RedCompute AI session backing this discussion" },
                new { name = "Message Count", fieldType = "number" },
                new { name = "Last Activity", fieldType = "date" },
            ]));
        _streamClient.DefineStream(new StreamDefinition(
            "nova-messages", "Nova Messages",
            "Chat messages from Nova discussions", RetentionDays: null, ParentType: "discussion"));
        _streamClient.DefineStream(new StreamDefinition(
            "nova-invocations", "Nova Invocations",
            "AI invocation audit records (purpose, snippets, duration, success)", RetentionDays: 90));
        // Versioned on purpose: schedule/config edits deserve history. Run
        // state is excluded from the entity (see NovaMirror) so per-run
        // saves don't produce version rows.
        _streamClient.DefineEntityType(new EntityTypeDefinition(
            "automation", "Automation",
            "Nova automation definition (schedule + action)",
            Icon: "fa-solid fa-robot", Color: "fuchsia", Versioning: true,
            Fields:
            [
                new { name = "Schedule", fieldType = "string", description = "Cron expression" },
                new { name = "Enabled", fieldType = "boolean" },
                new { name = "Action Type", fieldType = "string", description = "ai-session, http-check or builtin:backup" },
                new { name = "Owner ID", fieldType = "string" },
                new { name = "Description", fieldType = "string" },
            ]));
        _streamClient.DefineStream(new StreamDefinition(
            "automation-runs", "Automation Runs",
            "Per-trigger automation results", RetentionDays: 90, ParentType: "automation"));
        NovaMirror.Client = _streamClient;
        NovaMirror.AgentId ??= App.Config.AgentId;

        DiscussionEndpoints.Initialize(authFactory, new RedLeafDiscussionReader(
            App.Config.Suite.RedLeaf,
            new JwtService(new JwtOptions { SigningKey = signingKey }),
            NovaMirror.AgentId));
        DelegateEndpoints.Initialize(authFactory);
        ConversationExporter.Initialize(authFactory);

        _app.UseAppHostForwardedHeaders();
        _app.UseAppHostTelemetry();
        _app.UseCors();

        _app.UseAppHostAuth(new BearerAuthOptions
        {
            GetAccessToken = () => App.Config.Tunnel.AccessToken,
            CookieName = "nova_token",
            BypassPaths = ["/ping", "/api/remote/status"],
            FallThroughOnFailure = googleAuth != null,
        });
        _app.UseAppHostJwtAuth();
        _app.UseUserDetection();

        var logService = App.LogService;
        var registry = _app.CreateEndpointRegistry();
        registry.MapAuthEndpoints();

        _app.UseWebSockets();

        registry.MapGet("/api/file", "Serve a local image or video file by absolute path", (HttpContext ctx) =>
        {
            var path = ctx.Request.Query["path"].ToString();
            if (string.IsNullOrEmpty(path))
                return ApiError.BadRequest("missing_path", "Query parameter 'path' is required");

            // Claude sometimes emits garbled paths like "C:/.../T:/real/path" —
            // extract the last drive-letter root so we still resolve correctly.
            var lastDrive = System.Text.RegularExpressions.Regex.Match(
                path, @".*([A-Za-z]:[\\\/])", System.Text.RegularExpressions.RegexOptions.Singleline);
            if (lastDrive.Success && lastDrive.Groups[1].Index > 0)
                path = path[lastDrive.Groups[1].Index..];

            string fullPath;
            try { fullPath = Path.GetFullPath(path); }
            catch { return ApiError.BadRequest("invalid_path", "Path could not be resolved"); }

            // Block UNC paths and system directories
            if (fullPath.StartsWith(@"\\"))
                return ApiError.Forbidden("unc_blocked", "UNC paths are not allowed");
            var sysRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(sysRoot) && fullPath.StartsWith(sysRoot, StringComparison.OrdinalIgnoreCase))
                return ApiError.Forbidden("system_blocked", "System directory paths are not allowed");

            if (!File.Exists(fullPath))
                return ApiError.NotFound("not_found", "File not found");

            var ext = Path.GetExtension(fullPath).ToLowerInvariant();
            var mime = ext switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                ".svg" => "image/svg+xml",
                ".bmp" => "image/bmp",
                ".webm" => "video/webm",
                ".mp4" => "video/mp4",
                ".mov" => "video/quicktime",
                ".ogg" => "video/ogg",
                _ => (string?)null
            };
            if (mime == null)
                return ApiError.Forbidden("unsupported_type", "Only image and video files can be served");

            ctx.Response.Headers["Cache-Control"] = "public, max-age=3600";
            return Results.File(fullPath, mime);
        }).WithParam("path", "string", required: true,
            description: "Absolute path to a local media file (images and videos)",
            location: RedBamboo.AppHost.Discovery.ParamLocation.Query);

        registry.MapGet("/api/avatar", "Proxy the Nova agent avatar from RedLeaf", async (HttpContext ctx) =>
        {
            // Lazy-fetch if RedLeaf was down at startup
            if (string.IsNullOrEmpty(NovaMirror.AvatarUrl))
            {
                var fetched = await AgentRegistration.FetchAvatarUrlAsync(App.Config);
                if (fetched != null)
                {
                    NovaMirror.AvatarUrl = fetched;
                    logService.Info("avatar", $"Lazy-fetched avatar URL: {fetched}");
                }
            }

            var avatarUrl = NovaMirror.AvatarUrl;
            if (!string.IsNullOrEmpty(avatarUrl))
            {
                try
                {
                    var jwt = new JwtService(new JwtOptions { SigningKey = signingKey });
                    var token = jwt.GenerateAccessToken("system", "system@redsuite", "System", ["admin"]);
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {token}");
                    var response = await http.GetAsync(avatarUrl);
                    logService.Info("avatar", $"Proxy {avatarUrl} → {(int)response.StatusCode}");
                    if (response.IsSuccessStatusCode)
                    {
                        var contentType = response.Content.Headers.ContentType?.ToString() ?? "image/png";
                        var bytes = await response.Content.ReadAsByteArrayAsync();
                        ctx.Response.Headers["Cache-Control"] = "public, max-age=300";
                        return Results.Bytes(bytes, contentType);
                    }
                }
                catch (Exception ex) { logService.Warn("avatar", $"Proxy error: {ex.Message}"); }
            }
            else { logService.Info("avatar", "No avatar on agent entity, using fallback"); }
            return Results.Redirect("/nova-avatar.png");
        });

        registry.MapDiscussionEndpoints(_engine);
        registry.MapAskEndpoints(_engine);
        registry.MapDiscussionExportEndpoints();
        registry.MapSettingsEndpoints(_memory);
        registry.MapMemoryEndpoints(_memory);
        registry.MapAutomationEndpoints(_engine);
        registry.MapDelegateEndpoints();
        registry.MapCallbackEndpoints();

        var broadcaster = _app.Services.GetService<WebSocketBroadcaster>();
        broadcaster?.RegisterEvent(new WsEventSchema(
            "discussion.event",
            "Fired when an automation or system event is injected into a discussion with a live session " +
            "(POST /api/discussions/{id}/event). content carries the <nova-event> payload sent to the session.",
            Fields: ["discussionId", "sessionId", "content", "source"]));
        broadcaster?.RegisterEvent(new WsEventSchema(
            "discussion.nova-message",
            "Fired when a Nova-authored assistant message is injected into a discussion without triggering inference " +
            "(POST /api/discussions/{id}/nova-message).",
            Fields: ["discussionId", "content"]));

        var descriptor = new NovaServiceDescriptor(port, logService, _engine, registry);

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
            logService,
            proxyRoutes: new Dictionary<string, string>
            {
                ["/ai-session"] = App.Config.Suite.RedCompute,
                ["/tts"] = App.Config.Suite.RedCompute,
                ["/stt"] = App.Config.Suite.RedCompute,
            });

        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NovaDbContext>();
            db.Database.EnsureCreated();
            EnsureSchema(db);
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
        NovaMirror.Client = null;
        if (_streamClient != null)
        {
            await _streamClient.DisposeAsync();
            _streamClient = null;
        }

        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }
    }

    private static void EnsureSchema(NovaDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Discussions (
                Id TEXT PRIMARY KEY,
                Title TEXT,
                Status TEXT NOT NULL DEFAULT 'idle',
                CreatedAt TEXT NOT NULL,
                LastActivity TEXT NOT NULL,
                MessageCount INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS IX_Discussions_Status ON Discussions(Status);
            CREATE INDEX IF NOT EXISTS IX_Discussions_LastActivity ON Discussions(LastActivity);
            """;
        cmd.ExecuteNonQuery();

        cmd.CommandText = "PRAGMA table_info(Discussions)";
        using var discReader = cmd.ExecuteReader();
        var discColumns = new HashSet<string>();
        while (discReader.Read()) discColumns.Add(discReader.GetString(1));
        discReader.Close();

        if (!discColumns.Contains("SessionId"))
        {
            cmd.CommandText = "ALTER TABLE Discussions ADD COLUMN SessionId TEXT";
            cmd.ExecuteNonQuery();
        }

        if (!discColumns.Contains("LastReadAt"))
        {
            cmd.CommandText = "ALTER TABLE Discussions ADD COLUMN LastReadAt TEXT";
            cmd.ExecuteNonQuery();
        }

        if (!discColumns.Contains("InjectedContext"))
        {
            cmd.CommandText = "ALTER TABLE Discussions ADD COLUMN InjectedContext TEXT";
            cmd.ExecuteNonQuery();
        }

        if (!discColumns.Contains("OwnerId"))
        {
            cmd.CommandText = "ALTER TABLE Discussions ADD COLUMN OwnerId TEXT";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "CREATE INDEX IF NOT EXISTS IX_Discussions_OwnerId ON Discussions(OwnerId)";
            cmd.ExecuteNonQuery();
        }

        cmd.CommandText = "PRAGMA table_info(Conversations)";
        using var reader = cmd.ExecuteReader();
        var columns = new HashSet<string>();
        while (reader.Read()) columns.Add(reader.GetString(1));
        reader.Close();

        if (!columns.Contains("PartsJson"))
        {
            cmd.CommandText = "ALTER TABLE Conversations ADD COLUMN PartsJson TEXT";
            cmd.ExecuteNonQuery();
        }

        if (!columns.Contains("Source"))
        {
            cmd.CommandText = "ALTER TABLE Conversations ADD COLUMN Source TEXT NOT NULL DEFAULT 'user'";
            cmd.ExecuteNonQuery();
        }

        if (!columns.Contains("UserId"))
        {
            cmd.CommandText = "ALTER TABLE Conversations ADD COLUMN UserId TEXT";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "CREATE INDEX IF NOT EXISTS IX_Conversations_UserId ON Conversations(UserId)";
            cmd.ExecuteNonQuery();
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
