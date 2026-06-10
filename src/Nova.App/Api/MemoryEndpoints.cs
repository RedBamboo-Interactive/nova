using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RedBamboo.AppHost;
using RedBamboo.AppHost.Discovery;
using Nova.App.Services;

namespace Nova.App.Api;

public static class MemoryEndpoints
{
    public static void MapMemoryEndpoints(this EndpointRegistry registry, MemoryManager memory)
    {
        registry.MapGet("/api/memory/manifest", "List all memory files", () =>
        {
            var files = memory.GetMemoryManifest();
            return Results.Ok(new { files });
        });

        registry.MapGet("/api/memory/file", "Read a memory file", (string path) =>
        {
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "Path is required" });

            var content = memory.ReadMemoryFile(path);
            if (content == null)
                return Results.NotFound(new { error = "File not found" });

            return Results.Ok(new { path, content });
        }).WithParam("path", "string", required: true,
            description: "Workspace-relative path of the file (e.g. memory/index.md)",
            location: ParamLocation.Query);

        registry.MapPut("/api/memory/file", "Write a memory file (creates parent directories as needed)", (MemoryFileRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.Path) || request.Content == null)
                return Results.BadRequest(new { error = "Path and content are required" });

            try
            {
                memory.WriteMemoryFile(request.Path, request.Content);
                return Results.Ok(new { success = true });
            }
            catch (UnauthorizedAccessException)
            {
                return ApiError.Forbidden("path_outside_workspace",
                    "The path escapes the Nova workspace — only workspace-relative paths can be written");
            }
        })
        .WithParam("path", "string", required: true,
            description: "Workspace-relative path of the file to write (must stay inside the workspace)",
            location: ParamLocation.Body)
        .WithParam("content", "string", required: true,
            description: "Full file content (replaces the existing file)",
            location: ParamLocation.Body);
    }
}

public class MemoryFileRequest
{
    public string Path { get; set; } = "";
    public string Content { get; set; } = "";
}
