using Microsoft.EntityFrameworkCore;
using Nova.App.Data.Entities;

namespace Nova.App.Data;

public class NovaDbContext : DbContext
{
    public NovaDbContext(DbContextOptions<NovaDbContext> options) : base(options) { }

    public DbSet<Discussion> Discussions => Set<Discussion>();
    public DbSet<ConversationRecord> Conversations => Set<ConversationRecord>();
    public DbSet<InvocationLog> InvocationLogs => Set<InvocationLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Discussion>(e =>
        {
            e.HasKey(d => d.Id);
            e.HasIndex(d => d.Status);
            e.HasIndex(d => d.LastActivity);
        });

        modelBuilder.Entity<ConversationRecord>(e =>
        {
            e.HasKey(c => c.Id);
            e.HasIndex(c => c.ContextId);
        });

        modelBuilder.Entity<InvocationLog>(e =>
        {
            e.HasKey(l => l.Id);
            e.HasIndex(l => l.Timestamp);
            e.HasIndex(l => l.Purpose);
        });
    }
}
