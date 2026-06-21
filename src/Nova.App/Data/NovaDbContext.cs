using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Nova.App.Data.Entities;
using Nova.App.Services;

namespace Nova.App.Data;

public class NovaDbContext : DbContext
{
    public NovaDbContext(DbContextOptions<NovaDbContext> options) : base(options) { }

    public DbSet<Discussion> Discussions => Set<Discussion>();
    public DbSet<ConversationRecord> Conversations => Set<ConversationRecord>();
    public DbSet<InvocationLog> InvocationLogs => Set<InvocationLog>();

    // Mirroring to RedLeaf is hooked at SaveChanges so every write site —
    // endpoints, automations, engine — is covered by one choke point.
    public override int SaveChanges()
    {
        var (discussions, messages) = CollectMirrorTargets();
        var result = base.SaveChanges();
        Publish(discussions, messages);
        return result;
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var (discussions, messages) = CollectMirrorTargets();
        var result = await base.SaveChangesAsync(cancellationToken);
        Publish(discussions, messages);
        return result;
    }

    private (List<Discussion>, List<ConversationRecord>) CollectMirrorTargets()
    {
        var discussions = ChangeTracker.Entries<Discussion>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified)
            .Select(e => e.Entity)
            .ToList();
        var messages = ChangeTracker.Entries<ConversationRecord>()
            .Where(e => e.State == EntityState.Added)
            .Select(e => e.Entity)
            .ToList();
        return (discussions, messages);
    }

    private static void Publish(List<Discussion> discussions, List<ConversationRecord> messages)
    {
        try
        {
            foreach (var d in discussions)
                NovaMirror.PublishDiscussion(d);
            if (messages.Count > 0)
                NovaMirror.PublishMessages(messages);
        }
        catch
        {
            // mirroring must never break the local write path
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var utcConverter = new ValueConverter<DateTime, DateTime>(
            v => v,
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            foreach (var property in entityType.GetProperties())
                if (property.ClrType == typeof(DateTime))
                    property.SetValueConverter(utcConverter);
        modelBuilder.Entity<Discussion>(e =>
        {
            e.HasKey(d => d.Id);
            e.HasIndex(d => d.Status);
            e.HasIndex(d => d.LastActivity);
            e.HasIndex(d => d.OwnerId);
            e.HasIndex(d => d.AgentId);
        });

        modelBuilder.Entity<ConversationRecord>(e =>
        {
            e.HasKey(c => c.Id);
            e.HasIndex(c => c.ContextId);
            e.HasIndex(c => c.UserId);
        });

        modelBuilder.Entity<InvocationLog>(e =>
        {
            e.HasKey(l => l.Id);
            e.HasIndex(l => l.Timestamp);
            e.HasIndex(l => l.Purpose);
            e.HasIndex(l => l.AgentId);
        });
    }
}
