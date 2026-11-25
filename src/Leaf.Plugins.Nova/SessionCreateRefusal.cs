using System.Text.Json;

namespace Leaf.Plugins.Nova;

/// <summary>
/// Why a session could not start: RedCompute's HTTP status with its error code and message, or RedLeaf's
/// identity refusal before anything reached RedCompute.
/// </summary>
public sealed record SessionCreateRefusal(int Status, string Code, string Message)
{
    /// <summary>RedLeaf would not sign the start for the caller (<c>ExecutionIdentityValidationException</c>).</summary>
    public const string IdentityRefused = "execution_identity_refused";

    private const int MaxMessageLength = 500;

    /// <summary>
    /// Reads RedCompute's error body, <c>{"error": code, "message": text}</c> (some routes put the code in
    /// <c>code</c>), keeping the raw text when it is not that shape.
    /// </summary>
    public static SessionCreateRefusal FromResponse(int status, string? body)
    {
        string? code = null, message = null;
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var json = JsonDocument.Parse(body);
                if (json.RootElement.ValueKind == JsonValueKind.Object)
                {
                    code = Text(json.RootElement, "error") ?? Text(json.RootElement, "code");
                    message = Text(json.RootElement, "message");
                }
            }
            catch (JsonException) { /* Not JSON: the raw text is the message. */ }
        }
        message ??= string.IsNullOrWhiteSpace(body) ? $"RedCompute answered HTTP {status}" : body.Trim();
        if (message.Length > MaxMessageLength) message = message[..MaxMessageLength];
        return new(status, code ?? $"http_{status}", message);
    }

    /// <summary>What the person sees under their message.</summary>
    public string Describe() => Code == IdentityRefused
        ? $"This session cannot start for whoever is signed in here: {Message}"
        : $"RedCompute refused to start a session ({Code}): {Message}";

    public override string ToString() => $"HTTP {Status} {Code}: {Message}";

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()
                : null;
}
