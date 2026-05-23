namespace Nova.App.Data.Entities;

public class ConversationRecord
{
    public int Id { get; set; }
    public string ContextId { get; set; } = "";
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
