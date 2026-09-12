using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Leaf.Plugins.Nova.Endpoints;

public class MemoryFileRequest
{
    public string Path { get; set; } = "";
    public string Content { get; set; } = "";
}

public sealed record OpenLocalFileRequest(string Path, string? AgentId);

/// <summary>Journal/memory files, settings, and local media serving.</summary>
public static class MiscEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        // ── Journal / memory files ─────────────────────────────────────

        group.MapGet("/workspace/manifest", async (HttpContext ctx, AgentWorkspaces workspaces) =>
        {
            var agentId = ctx.Request.Query["agent"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(agentId))
                return Results.BadRequest(new { error = "An Agent must be selected" });
            try
            {
                var workspace = await workspaces.GetAsync(agentId, ctx.RequestAborted);
                return Results.Ok(new { files = workspace.GetManifest() });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 409);
            }
        });

        group.MapPost("/workspace/reveal", async (HttpContext ctx, AgentWorkspaces workspaces) =>
        {
            if (!OperatingSystem.IsWindows())
                return Results.BadRequest(new { error = "Opening a workspace folder is only supported on Windows" });

            var agentId = ctx.Request.Query["agent"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(agentId))
                return Results.BadRequest(new { error = "An Agent must be selected" });

            try
            {
                var workspace = await workspaces.GetAsync(agentId, ctx.RequestAborted);
                var psi = new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = false };
                psi.ArgumentList.Add(workspace.WorkspacePath);
                Process.Start(psi);
                return Results.Ok(new { success = true, path = workspace.WorkspacePath });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 409);
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
            if (string.IsNullOrWhiteSpace(agentId))
                return Results.BadRequest(new { error = "An Agent must be selected" });

            try
            {
                var workspace = await workspaces.GetAsync(agentId, ctx.RequestAborted);
                var content = workspace.ReadFile(path);
                if (content == null)
                    return Results.NotFound(new { error = "File not found" });

                return Results.Ok(new { path, content });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 409);
            }
        });

        group.MapPut("/memory/file", async (HttpContext ctx, MemoryFileRequest request, AgentWorkspaces workspaces) =>
        {
            if (string.IsNullOrWhiteSpace(request.Path) || request.Content == null)
                return Results.BadRequest(new { error = "Path and content are required" });
            var agentId = ctx.Request.Query["agent"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(agentId))
                return Results.BadRequest(new { error = "An Agent must be selected" });

            try
            {
                var workspace = await workspaces.GetAsync(agentId, ctx.RequestAborted);
                workspace.WriteFile(request.Path, request.Content);
                return Results.Ok(new { success = true });
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Json(new { error = "The path escapes the agent workspace — only workspace-relative paths can be written" }, statusCode: 403);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 409);
            }
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

        group.MapPost("/file/open", async (OpenLocalFileRequest request, AgentWorkspaces workspaces, CancellationToken ct) =>
        {
            if (!OperatingSystem.IsWindows())
                return Results.BadRequest(new { error = "Opening local files is only supported on Windows" });

            var validation = ResolveSafeLocalPath(request.Path);
            if (!validation.Valid)
                return Results.Json(new { error = validation.Error }, statusCode: 403);

            var fullPath = validation.Path!;
            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
                return Results.NotFound(new { error = "File or directory not found" });

            if (!string.IsNullOrWhiteSpace(request.AgentId))
            {
                var workspace = await workspaces.TryGetAsync(request.AgentId, ct);
                if (workspace is not null && TryGetRelativePath(workspace.WorkspacePath, fullPath, out var relativePath))
                {
                    var markdown = File.Exists(fullPath)
                        && Path.GetExtension(fullPath) is { } extension
                        && (extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
                            || extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase));
                    return Results.Ok(new
                    {
                        action = markdown ? "markdown" : "workspace",
                        path = relativePath.Replace('\\', '/'),
                    });
                }
            }

            var psi = new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = false };
            psi.ArgumentList.Add(File.Exists(fullPath) ? $"/select,{fullPath}" : fullPath);
            Process.Start(psi);
            return Results.Ok(new { action = "explorer", path = fullPath });
        });
    }

    private static (bool Valid, string? Path, string? Error) ResolveSafeLocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return (false, null, "Path is required");
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
                return (false, null, "UNC paths are not allowed");
            var sysRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(sysRoot) && fullPath.StartsWith(sysRoot, StringComparison.OrdinalIgnoreCase))
                return (false, null, "System directory paths are not allowed");
            return (true, fullPath, null);
        }
        catch
        {
            return (false, null, "Path could not be resolved");
        }
    }

    private static bool TryGetRelativePath(string root, string path, out string relativePath)
    {
        relativePath = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relativePath != ".."
            && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !Path.IsPathRooted(relativePath);
    }
}
