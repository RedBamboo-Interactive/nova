using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Nova.App.Services;

namespace Nova.App.Api;

public static class MemoryEndpoints
{
    public static void MapMemoryEndpoints(this IEndpointRouteBuilder app, MemoryManager memory)
    {
        app.MapGet("/api/memory/manifest", () =>
        {
            var files = memory.GetMemoryManifest();
            return Results.Ok(new { files });
        });

        app.MapGet("/api/memory/file", (string path) =>
        {
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "Path is required" });

            var content = memory.ReadMemoryFile(path);
            if (content == null)
                return Results.NotFound(new { error = "File not found" });

            return Results.Ok(new { path, content });
        });

        app.MapPut("/api/memory/file", (MemoryFileRequest request) =>
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
                return Results.Forbid();
            }
        });
    }
}

public class MemoryFileRequest
{
    public string Path { get; set; } = "";
    public string Content { get; set; } = "";
}
