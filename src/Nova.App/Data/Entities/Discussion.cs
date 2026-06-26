namespace Nova.App.Data.Entities;

public class Discussion
{
    public string Id { get; set; } = "";
    public string? Title { get; set; }
    public string Status { get; set; } = "idle";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    public int MessageCount { get; set; }
    public string? SessionId { get; set; }
    public DateTime? LastReadAt { get; set; }
    public string? InjectedContext { get; set; }
    public string? OwnerId { get; set; }
    public string? AgentId { get; set; }
    public string? LastContextJson { get; set; }
}
