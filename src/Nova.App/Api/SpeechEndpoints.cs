using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Nova.App.Services;

namespace Nova.App.Api;

public static class SpeechEndpoints
{
    public static void MapSpeechEndpoints(this IEndpointRouteBuilder app, NovaEngine engine)
    {
        app.MapPost("/api/speech/transcribe", async (HttpRequest req, CancellationToken ct) =>
        {
            if (!req.HasFormContentType)
                return Results.BadRequest(new { error = "Expected multipart form data" });

            var form = await req.ReadFormAsync(ct);
            var file = form.Files["audio"];
            if (file is null)
                return Results.BadRequest(new { error = "Missing 'audio' file" });

            try
            {
                await using var stream = file.OpenReadStream();
                var text = await engine.RedCompute.TranscribeAsync(
                    stream, file.FileName, file.ContentType, ct);
                return Results.Ok(new { text });
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        app.MapPost("/api/speech/speak", async (SpeakRequest request, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Text))
                return Results.BadRequest(new { error = "Text is required" });

            try
            {
                var audio = await engine.RedCompute.SpeakAsync(
                    request.Text, request.Voice, request.Instructions, ct);
                return Results.Bytes(audio, "audio/mpeg");
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        app.MapPost("/api/speech/prompt", async (PromptRequest request, CancellationToken ct) =>
        {
            try
            {
                var result = await engine.RedCompute.PromptAsync(request, ct);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });
    }
}

public class SpeakRequest
{
    public string Text { get; set; } = "";
    public string? Voice { get; set; }
    public string? Instructions { get; set; }
}
