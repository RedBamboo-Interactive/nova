using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Leaf.Plugins.Nova.Endpoints;

public class LocationUpdateRequest
{
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public double? Accuracy { get; set; }
    public string? Timezone { get; set; }
}

public class MemoryFileRequest
{
    public string Path { get; set; } = "";
    public string Content { get; set; } = "";
}

public class DockerSettingsRequest
{
    public string? Image { get; set; }
}

/// <summary>Location, journal/memory files, settings, and local media serving.</summary>
public static class MiscEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        // ── Location ───────────────────────────────────────────────────

        group.MapPost("/location/update", async (HttpContext ctx, LocationService location) =>
        {
            LocationUpdateRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<LocationUpdateRequest>(); }
            catch { return Results.BadRequest(new { error = "Invalid JSON" }); }

            if (body?.Latitude == null || body.Longitude == null)
                return Results.BadRequest(new { error = "latitude and longitude are required" });

            location.UpdateLocation(body.Latitude.Value, body.Longitude.Value, body.Accuracy, body.Timezone);
            return Results.Ok(new { success = true });
        });

        group.MapGet("/location/current", (LocationService location) =>
        {
            var loc = location.Latest;
            if (loc == null)
                return Results.Ok(new { available = false });

            return Results.Ok(new
            {
                available = true,
                latitude = loc.Latitude,
                longitude = loc.Longitude,
                accuracy = loc.Accuracy,
                timestamp = loc.Timestamp.ToString("o"),
                zone = loc.Zone,
                place = loc.PlaceName,
            });
        });

        // ── Journal / memory files ─────────────────────────────────────

        group.MapGet("/workspace/manifest", async (HttpContext ctx, AgentWorkspaces workspaces) =>
        {
            var agentId = ctx.Request.Query["agent"].FirstOrDefault();
            var workspace = await workspaces.GetAsync(agentId);
            return Results.Ok(new { files = workspace.GetManifest() });
        });

        group.MapPost("/workspace/reveal", async (HttpContext ctx, AgentWorkspaces workspaces) =>
        {
            if (!OperatingSystem.IsWindows())
                return Results.BadRequest(new { error = "Opening a workspace folder is only supported on Windows" });

            var agentId = ctx.Request.Query["agent"].FirstOrDefault();
            var workspace = await workspaces.GetAsync(agentId, ctx.RequestAborted);

            try
            {
                var psi = new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = false };
                psi.ArgumentList.Add(workspace.WorkspacePath);
                Process.Start(psi);
                return Results.Ok(new { success = true, path = workspace.WorkspacePath });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = $"Could not open the workspace folder: {ex.Message}" });
            }
        });

        group.MapGet("/memory/file", async (HttpContext ctx, AgentWorkspaces workspaces) =>
        {
            var path = ctx.Request.Query["path"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "Path is required" });

            var agentId = ctx.Request.Query["agent"].FirstOrDefault();
            var workspace = await workspaces.GetAsync(agentId);

            var content = workspace.ReadFile(path);
            if (content == null)
                return Results.NotFound(new { error = "File not found" });

            return Results.Ok(new { path, content });
        });

        group.MapPut("/memory/file", async (MemoryFileRequest request, AgentWorkspaces workspaces) =>
        {
            if (string.IsNullOrWhiteSpace(request.Path) || request.Content == null)
                return Results.BadRequest(new { error = "Path and content are required" });

            try
            {
                var workspace = await workspaces.GetAsync(null);
                workspace.WriteFile(request.Path, request.Content);
                return Results.Ok(new { success = true });
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Json(new { error = "The path escapes the agent workspace — only workspace-relative paths can be written" }, statusCode: 403);
            }
        });

        // ── Settings ───────────────────────────────────────────────────

        group.MapGet("/settings", async (NovaConfigStore config) =>
        {
            var c = await config.GetAsync();
            return Results.Ok(new
            {
                docker = new
                {
                    enabled = !string.IsNullOrEmpty(c.DockerImage),
                    image = c.DockerImage,
                },
                general = new
                {
                    defaultQualityMode = c.DefaultQualityMode,
                    workspacePath = c.WorkspacePath,
                },
            });
        });

        group.MapPut("/settings/docker", async (DockerSettingsRequest request, NovaConfigStore config) =>
        {
            var image = string.IsNullOrWhiteSpace(request.Image) ? null : request.Image.Trim();
            await config.PatchAsync(new JsonObject { ["docker_image"] = image });
            return Results.Ok(new
            {
                success = true,
                enabled = image != null,
                image,
            });
        });

        // ── Local media ────────────────────────────────────────────────

        group.MapGet("/file", (HttpContext ctx) =>
        {
            var path = ctx.Request.Query["path"].ToString();
            if (string.IsNullOrEmpty(path))
                return Results.BadRequest(new { error = "Query parameter 'path' is required" });

            // Claude sometimes emits garbled paths like "C:/.../T:/real/path" —
            // extract the last drive-letter root so we still resolve correctly.
            var lastDrive = System.Text.RegularExpressions.Regex.Match(
                path, @".*([A-Za-z]:[\\\/])", System.Text.RegularExpressions.RegexOptions.Singleline);
            if (lastDrive.Success && lastDrive.Groups[1].Index > 0)
                path = path[lastDrive.Groups[1].Index..];

            string fullPath;
            try { fullPath = Path.GetFullPath(path); }
            catch { return Results.BadRequest(new { error = "Path could not be resolved" }); }

            if (fullPath.StartsWith(@"\\"))
                return Results.Json(new { error = "UNC paths are not allowed" }, statusCode: 403);
            var sysRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(sysRoot) && fullPath.StartsWith(sysRoot, StringComparison.OrdinalIgnoreCase))
                return Results.Json(new { error = "System directory paths are not allowed" }, statusCode: 403);

            if (!File.Exists(fullPath))
                return Results.NotFound(new { error = "File not found" });

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
                _ => (string?)null,
            };
            if (mime == null)
                return Results.Json(new { error = "Only media files can be served" }, statusCode: 403);

            ctx.Response.Headers.CacheControl = "public, max-age=3600";
            return Results.File(fullPath, mime);
        });
    }
}
