namespace Nova.App.Data.Entities;

public class InvocationLog
{
    public int Id { get; set; }
    public string Purpose { get; set; } = "";
    public string? ContextId { get; set; }
    public string? PromptSnippet { get; set; }
    public string? ResponseSnippet { get; set; }
    public int DurationMs { get; set; }
    public bool Success { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string? AgentId { get; set; }
}
