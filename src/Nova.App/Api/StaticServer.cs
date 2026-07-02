using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text.Json;
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
using RedBamboo.AppHost.Watchers;
using RedBamboo.AppHost.WebSockets;
using Nova.App.Data;
using Nova.App.Data.Entities;
using Nova.App.Services;

namespace Nova.App.Api;

public class StaticServer
{
    private readonly NovaEngine _engine;
    private readonly MemoryManager _memory;
    private readonly AgentResolver _agentResolver;
    private readonly AgentMemoryFactory _agentMemoryFactory;
    private WebApplication? _app;
    private RedLeafStreamClient? _streamClient;
    private RedLeafEntityWatcher? _entityWatcher;

    public StaticServer(NovaEngine engine, MemoryManager memory, AgentResolver agentResolver, AgentMemoryFactory agentMemoryFactory)
    {
        _engine = engine;
        _memory = memory;
        _agentResolver = agentResolver;
        _agentMemoryFactory = agentMemoryFactory;
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

        var geo = new GeoLocationService();
        _ = geo.ResolveAsync();

        var locationService = new LocationService();

        builder.Services.AddSingleton(_engine);
        builder.Services.AddSingleton(_memory);
        builder.Services.AddSingleton(_agentResolver);
        builder.Services.AddSingleton(_agentMemoryFactory);
        builder.Services.AddSingleton(geo);
        builder.Services.AddSingleton(locationService);


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
        _streamClient.DefineStream(new StreamDefinition(
            "message-reactions", "Message Reactions",
            "Emoji reactions on discussion messages", RetentionDays: null, ParentType: "discussion"));
        NovaMirror.Client = _streamClient;
        NovaMirror.AgentId ??= App.Config.AgentId;

        DiscussionEndpoints.Initialize(authFactory, new RedLeafDiscussionReader(
            App.Config.Suite.RedLeaf,
            new JwtService(new JwtOptions { SigningKey = signingKey })),
            _agentMemoryFactory, geo, locationService);
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
                ".ogg" => "audio/ogg",
                ".mp3" => "audio/mpeg",
                ".wav" => "audio/wav",
                _ => (string?)null
            };
            if (mime == null)
                return ApiError.Forbidden("unsupported_type", "Only media files can be served");

            ctx.Response.Headers["Cache-Control"] = "public, max-age=3600";
            return Results.File(fullPath, mime);
        }).WithParam("path", "string", required: true,
            description: "Absolute path to a local media file (images and videos)",
            location: RedBamboo.AppHost.Discovery.ParamLocation.Query);

        registry.MapGet("/api/avatar", "Proxy the Nova agent avatar from RedLeaf", async (HttpContext ctx) =>
        {
            // Use AgentResolver for fresh avatar (respects outfit)
            string? avatarUrl = null;
            if (NovaMirror.AgentId != null)
            {
                var agent = await _agentResolver.GetAgentAsync(NovaMirror.AgentId);
                if (agent?.AvatarFilename != null)
                    avatarUrl = _agentResolver.BuildAvatarUrl(agent.AvatarFilename);
            }

            // Fallback to cached NovaMirror value
            if (string.IsNullOrEmpty(avatarUrl))
            {
                if (string.IsNullOrEmpty(NovaMirror.AvatarUrl))
                {
                    var fetched = await AgentRegistration.FetchAvatarUrlAsync(App.Config);
                    if (fetched != null)
                    {
                        NovaMirror.AvatarUrl = fetched;
                        logService.Info("avatar", $"Lazy-fetched avatar URL: {fetched}");
                    }
                }
                avatarUrl = NovaMirror.AvatarUrl;
            }

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
                        ctx.Response.Headers["Cache-Control"] = "public, max-age=60";
                        return Results.Bytes(bytes, contentType);
                    }
                }
                catch (Exception ex) { logService.Warn("avatar", $"Proxy error: {ex.Message}"); }
            }
            else { logService.Info("avatar", "No avatar on agent entity, using fallback"); }
            return Results.Redirect("/nova-avatar.png");
        });

        registry.MapGet("/api/agents", "List all active RedLeaf agents", async () =>
        {
            var agents = await _agentResolver.GetAgentsAsync();
            return Results.Ok(agents.Select(a => new
            {
                a.Id, a.Slug, a.Name, a.Description, a.Status,
                avatarUrl = $"/api/agents/{a.Id}/avatar",
            }));
        });

        registry.MapGet("/api/agents/{agentId}/avatar", "Proxy an agent's avatar from RedLeaf", async (string agentId, HttpContext ctx) =>
        {
            var agent = await _agentResolver.GetAgentAsync(agentId);
            var avatarUrl = agent != null ? _agentResolver.BuildAvatarUrl(agent.AvatarFilename) : null;

            if (!string.IsNullOrEmpty(avatarUrl))
            {
                try
                {
                    var jwt = new JwtService(new JwtOptions { SigningKey = signingKey });
                    var token = jwt.GenerateAccessToken("system", "system@redsuite", "System", ["admin"]);
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {token}");
                    var response = await http.GetAsync(avatarUrl);
                    if (response.IsSuccessStatusCode)
                    {
                        var contentType = response.Content.Headers.ContentType?.ToString() ?? "image/png";
                        var bytes = await response.Content.ReadAsByteArrayAsync();
                        ctx.Response.Headers["Cache-Control"] = "public, max-age=60";
                        return Results.Bytes(bytes, contentType);
                    }
                }
                catch (Exception ex) { logService.Warn("avatar", $"Agent avatar proxy error: {ex.Message}"); }
            }
            return Results.Redirect("/nova-avatar.png");
        }).WithParam("agentId", "string", required: true, description: "RedLeaf agent entity ID");

        registry.MapGet("/api/redleaf-asset/{*path}", "Proxy a RedLeaf asset through Nova for tunnel access", async (string path, HttpContext ctx) =>
        {
            try
            {
                var jwt2 = new JwtService(new JwtOptions { SigningKey = signingKey });
                var token = jwt2.GenerateAccessToken("system", "system@redsuite", "System", ["admin"]);
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {token}");
                var rlBase = App.Config.Suite.RedLeaf.TrimEnd('/');
                var response = await http.GetAsync($"{rlBase}/api/assets/{path}");
                if (response.IsSuccessStatusCode)
                {
                    var contentType = response.Content.Headers.ContentType?.ToString() ?? "image/png";
                    var bytes = await response.Content.ReadAsByteArrayAsync();
                    ctx.Response.Headers["Cache-Control"] = "public, max-age=3600";
                    return Results.Bytes(bytes, contentType);
                }
                return Results.StatusCode((int)response.StatusCode);
            }
            catch { return Results.StatusCode(502); }
        });

        HttpClient BuildRedLeafClient()
        {
            var jwt2 = new JwtService(new JwtOptions { SigningKey = signingKey });
            var token = jwt2.GenerateAccessToken("system", "system@redsuite", "System", ["admin"]);
            var http = new HttpClient { BaseAddress = new Uri(App.Config.Suite.RedLeaf.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            return http;
        }

        JsonElement ParseEntityData(JsonElement entity)
        {
            var d = entity.GetProperty("data");
            return d.ValueKind == JsonValueKind.String ? JsonDocument.Parse(d.GetString()!).RootElement.Clone() : d;
        }

        string? GetStr(JsonElement obj, string key) =>
            obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        registry.MapGet("/api/outfits", "List outfit entities for an agent", async (HttpContext ctx) =>
        {
            var agentId = ctx.Request.Query.TryGetValue("agentId", out var aid) && !string.IsNullOrEmpty(aid.ToString())
                ? aid.ToString() : NovaMirror.AgentId;
            if (agentId == null) return Results.Ok(new { baseAvatarUrl = "/nova-avatar.png", currentOverride = (string?)null, outfits = Array.Empty<object>() });

            try
            {
                using var rl = BuildRedLeafClient();
                var rlBase = App.Config.Suite.RedLeaf.TrimEnd('/');

                // Fetch agent entity for base avatar + current override
                var agentResp = await rl.GetStringAsync($"api/entities/{agentId}");
                using var agentDoc = JsonDocument.Parse(agentResp);
                var agentData = ParseEntityData(agentDoc.RootElement);
                var baseAvRaw = GetStr(agentData, "avatar");
                string baseAvatarUrl;
                if (baseAvRaw != null)
                {
                    var filename = baseAvRaw.Contains('/') ? baseAvRaw.Split('/').Last() : baseAvRaw;
                    baseAvatarUrl = $"/api/redleaf-asset/{filename}";
                }
                else baseAvatarUrl = "/nova-avatar.png";

                string? currentOverride = GetStr(agentData, "outfit");

                // Fetch outfit entities for this agent, newest first
                var outfitResp = await rl.GetStringAsync($"api/entities?type=outfit&data.agent={agentId}&sort_by=createdAt&sort_dir=desc&limit=30");
                using var outfitDoc = JsonDocument.Parse(outfitResp);
                var items = outfitDoc.RootElement.GetProperty("items");

                var outfits = new List<object>();
                foreach (var item in items.EnumerateArray())
                {
                    var data = ParseEntityData(item);
                    outfits.Add(new
                    {
                        id = item.GetProperty("id").GetString(),
                        name = item.TryGetProperty("name", out var n) ? n.GetString() : null,
                        url = GetStr(data, "asset"),
                        prompt = GetStr(data, "prompt"),
                        reasoning = GetStr(data, "reasoning"),
                        nsfw = GetStr(data, "nsfw") == "true",
                        date = item.TryGetProperty("createdAt", out var ca) ? ca.GetString() : null,
                        active = item.GetProperty("id").GetString() == currentOverride,
                    });
                }

                return Results.Ok(new { baseAvatarUrl, currentOverride, outfits });
            }
            catch (Exception ex)
            {
                logService.Warn("outfits", $"List failed: {ex.Message}");
                return Results.Ok(new { baseAvatarUrl = "/nova-avatar.png", currentOverride = (string?)null, outfits = Array.Empty<object>() });
            }
        });

        registry.MapPost("/api/outfits/select", "Select an outfit or reset to base avatar", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            string? url = null;
            string? outfitId = null;
            string? discussionId = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                url = doc.RootElement.TryGetProperty("url", out var u) ? u.GetString() : null;
                outfitId = doc.RootElement.TryGetProperty("outfitId", out var oid) ? oid.GetString() : null;
                discussionId = doc.RootElement.TryGetProperty("discussionId", out var d) ? d.GetString() : null;
            }
            catch { return Results.BadRequest(new { error = "Invalid JSON" }); }

            if (NovaMirror.AgentId == null) return Results.BadRequest(new { error = "No agent configured" });

            try
            {
                using var rl = BuildRedLeafClient();

                // Update outfit on agent entity (stores outfit ID, or empty for base avatar)
                var patchBody = new StringContent(
                    JsonSerializer.Serialize(new { outfit = outfitId ?? "" }),
                    System.Text.Encoding.UTF8, "application/json");
                var resp = await rl.PatchAsync($"api/entities/{NovaMirror.AgentId}/data", patchBody);
                if (!resp.IsSuccessStatusCode) return Results.StatusCode(502);

                // Resolve asset URL from outfit entity (outfit stores ID, not URL)
                string resolvedUrl = "";
                if (!string.IsNullOrEmpty(outfitId))
                {
                    try
                    {
                        var outfitEntityResp = await rl.GetStringAsync($"api/entities/{outfitId}");
                        using var outfitEntityDoc = JsonDocument.Parse(outfitEntityResp);
                        var outfitEntityData = ParseEntityData(outfitEntityDoc.RootElement);
                        var asset = GetStr(outfitEntityData, "asset");
                        if (asset != null) resolvedUrl = asset;
                    }
                    catch { }
                }

                await _agentResolver.GetAgentsAsync(forceRefresh: true);

                // Broadcast avatar change to all connected frontends
                _app.Services.GetService<WebSocketBroadcaster>()
                    ?.Broadcast("agent.avatar-changed", new { agentId = NovaMirror.AgentId, url = resolvedUrl });

                // Post to LIVE timeline
                if (!string.IsNullOrEmpty(outfitId))
                {
                    try
                    {
                        var outfitResp = await rl.GetStringAsync($"api/entities/{outfitId}");
                        using var outfitDoc = JsonDocument.Parse(outfitResp);
                        var outfitName = outfitDoc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
                        _ = LiveEventService.Instance?.PostAsync("outfit", $"Changed into \"{outfitName ?? "new outfit"}\"");
                    }
                    catch { _ = LiveEventService.Instance?.PostAsync("outfit", "Changed outfit"); }
                }
                else
                {
                    _ = LiveEventService.Instance?.PostAsync("outfit", "Reset to base avatar");
                }

                // Notify Nova via discussion event
                if (!string.IsNullOrEmpty(discussionId))
                {
                    var outfitLabel = string.IsNullOrEmpty(resolvedUrl) ? "base avatar" : "a new outfit";
                    using var novaHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    var eventBody = new StringContent(
                        JsonSerializer.Serialize(new { type = "outfit-change", content = $"Laurent changed your outfit to {outfitLabel}." }),
                        System.Text.Encoding.UTF8, "application/json");
                    await novaHttp.PostAsync($"http://127.0.0.1:18803/api/discussions/{discussionId}/event", eventBody);
                }

                return Results.Ok(new { success = true, url = resolvedUrl });
            }
            catch (Exception ex)
            {
                logService.Warn("outfits", $"Select failed: {ex.Message}");
                return Results.StatusCode(502);
            }
        });

        registry.MapPost("/api/outfits", "Create a new outfit entity", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            try
            {
                using var doc = JsonDocument.Parse(body);
                var assetUrl = doc.RootElement.GetProperty("url").GetString();
                var prompt = doc.RootElement.TryGetProperty("prompt", out var p) ? p.GetString() : null;
                var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : "Outfit";
                var reasoning = doc.RootElement.TryGetProperty("reasoning", out var r) ? r.GetString() : null;
                var nsfw = doc.RootElement.TryGetProperty("nsfw", out var nsfwVal) && nsfwVal.ValueKind == JsonValueKind.True;

                using var rl = BuildRedLeafClient();
                var dataObj = new Dictionary<string, object?>
                {
                    ["agent"] = NovaMirror.AgentId, ["asset"] = assetUrl,
                    ["prompt"] = prompt, ["nsfw"] = nsfw ? "true" : "false",
                };
                if (reasoning != null) dataObj["reasoning"] = reasoning;
                var createBody = new StringContent(JsonSerializer.Serialize(new
                {
                    name,
                    type_slug = "outfit",
                    data = dataObj
                }), System.Text.Encoding.UTF8, "application/json");

                var resp = await rl.PostAsync("api/entities", createBody);
                var respBody = await resp.Content.ReadAsStringAsync();
                using var respDoc = JsonDocument.Parse(respBody);
                return Results.Ok(new { success = true, id = respDoc.RootElement.GetProperty("id").GetString() });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        registry.MapDiscussionEndpoints(_engine);
        registry.MapAskEndpoints(_engine);
        registry.MapDiscussionExportEndpoints();
        registry.MapSettingsEndpoints(_memory);
        registry.MapMemoryEndpoints(_memory, _agentMemoryFactory);
        registry.MapAutomationEndpoints(_engine);
        registry.MapDelegateEndpoints();
        registry.MapCallbackEndpoints();
        registry.MapLocationEndpoints();

        var broadcaster = _app.Services.GetService<WebSocketBroadcaster>();
        broadcaster?.RegisterEvent(new WsEventSchema(
            "agent.avatar-changed",
            "Fired when an agent's avatar override is updated (outfit change).",
            Fields: ["agentId", "url"]));
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
        broadcaster?.RegisterEvent(new WsEventSchema(
            "discussion.reaction",
            "Fired when an emoji reaction is added or removed on a discussion message.",
            Fields: ["discussionId", "messageId", "emoji", "action", "actorId", "actorName", "actorType"]));

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
            EnsureLiveDiscussions(db);
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

        var scopeFactory = _app.Services.GetRequiredService<IServiceScopeFactory>();
        _ = new LiveEventService(scopeFactory, App.LogService);
        var livePoller = new LivePollerService(App.LogService);
        _ = Task.Run(async () =>
        {
            try { await livePoller.StartAsync(CancellationToken.None); }
            catch (Exception ex) { App.LogService.Error("live-poller", $"Poller crashed: {ex}"); }
        });

        var wsBroadcaster = _app.Services.GetService<WebSocketBroadcaster>();
        var jwtService = _app.Services.GetRequiredService<JwtService>();

        _entityWatcher = new RedLeafEntityWatcher(
            App.Config.Suite.RedLeaf,
            jwtService,
            App.LogService);

        _entityWatcher.On("agent", async (change, ct) =>
        {
            var agents = await _agentResolver.GetAgentsAsync(forceRefresh: true);
            var nova = agents.FirstOrDefault(a => a.Slug == "nova");
            var url = nova != null ? _agentResolver.BuildAvatarUrl(nova.AvatarFilename) : null;
            if (url != null) NovaMirror.AvatarUrl = url;
            wsBroadcaster?.Broadcast("agent.avatar-changed", new
            {
                agentId = NovaMirror.AgentId,
                url = url ?? "",
            });
        });

        _entityWatcher.On("automation", async (change, ct) =>
        {
            if (_engine?.Automations != null && Guid.TryParse(change.Id, out var entityId))
                await _engine.Automations.RefreshAutomationAsync(entityId);
        });

        _entityWatcher.Start(ct);
    }

    public async Task StopAsync()
    {
        if (_entityWatcher != null)
        {
            await _entityWatcher.StopAsync();
            await _entityWatcher.DisposeAsync();
            _entityWatcher = null;
        }

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

        if (!discColumns.Contains("AgentId"))
        {
            cmd.CommandText = "ALTER TABLE Discussions ADD COLUMN AgentId TEXT";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "CREATE INDEX IF NOT EXISTS IX_Discussions_AgentId ON Discussions(AgentId)";
            cmd.ExecuteNonQuery();
            if (NovaMirror.AgentId != null)
            {
                cmd.CommandText = $"UPDATE Discussions SET AgentId = @aid WHERE AgentId IS NULL";
                var p = cmd.CreateParameter();
                p.ParameterName = "@aid";
                p.Value = NovaMirror.AgentId;
                cmd.Parameters.Add(p);
                cmd.ExecuteNonQuery();
                cmd.Parameters.Clear();
            }
        }

        if (!discColumns.Contains("LastContextJson"))
        {
            cmd.CommandText = "ALTER TABLE Discussions ADD COLUMN LastContextJson TEXT";
            cmd.ExecuteNonQuery();
        }

        if (!discColumns.Contains("Type"))
        {
            cmd.CommandText = "ALTER TABLE Discussions ADD COLUMN Type TEXT NOT NULL DEFAULT 'chat'";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS IX_Discussions_LivePerAgent ON Discussions(AgentId) WHERE Type = 'live' AND Status != 'archived'";
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

        if (!columns.Contains("SenderAgentId"))
        {
            cmd.CommandText = "ALTER TABLE Conversations ADD COLUMN SenderAgentId TEXT";
            cmd.ExecuteNonQuery();
        }

        if (!columns.Contains("Uid"))
        {
            cmd.CommandText = "ALTER TABLE Conversations ADD COLUMN Uid TEXT";
            cmd.ExecuteNonQuery();
        }

        cmd.CommandText = "PRAGMA table_info(InvocationLogs)";
        using var invReader = cmd.ExecuteReader();
        var invColumns = new HashSet<string>();
        while (invReader.Read()) invColumns.Add(invReader.GetString(1));
        invReader.Close();

        if (!invColumns.Contains("AgentId"))
        {
            cmd.CommandText = "ALTER TABLE InvocationLogs ADD COLUMN AgentId TEXT";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "CREATE INDEX IF NOT EXISTS IX_InvocationLogs_AgentId ON InvocationLogs(AgentId)";
            cmd.ExecuteNonQuery();
        }
    }

    private static void EnsureLiveDiscussions(NovaDbContext db)
    {
        if (NovaMirror.AgentId is null) return;

        var hasLive = db.Discussions.Any(d => d.Type == "live" && d.AgentId == NovaMirror.AgentId && d.Status != "archived");
        if (hasLive) return;

        var agentName = "Nova";
        var live = new Discussion
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Type = "live",
            Title = $"{agentName} Live",
            Status = "idle",
            AgentId = NovaMirror.AgentId,
            OwnerId = NovaMirror.UserId,
        };
        db.Discussions.Add(live);
        db.SaveChanges();
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
