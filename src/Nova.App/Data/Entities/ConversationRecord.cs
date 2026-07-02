namespace Nova.App.Data.Entities;

public class ConversationRecord
{
    public int Id { get; set; }
    public string ContextId { get; set; } = "";
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
    public string? PartsJson { get; set; }
    public string Source { get; set; } = "user";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string? UserId { get; set; }
    public string? SenderAgentId { get; set; }
    // Universal message identity, shared with the copy of this message in
    // other stores (e.g. the RedCompute session transcript when an event is
    // cross-posted). Null on rows created before the uid rollout.
    public string? Uid { get; set; }
}
