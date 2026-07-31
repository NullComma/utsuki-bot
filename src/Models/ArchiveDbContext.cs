using Microsoft.EntityFrameworkCore;

namespace App.Models;

public class ArchiveDbContext : DbContext
{
    public DbSet<MessageRecord> Messages => Set<MessageRecord>();
    public DbSet<MemoryPost> MemoryPosts => Set<MemoryPost>();

    public ArchiveDbContext() { }

    public ArchiveDbContext(DbContextOptions<ArchiveDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MessageRecord>(entity =>
        {
            entity.ToTable("Messages");

            entity.HasIndex(e => e.AuthorId);
            entity.HasIndex(e => e.ChannelId);
            entity.HasIndex(e => e.GuildId);
            entity.HasIndex(e => e.MessageId).IsUnique();
            entity.HasIndex(e => e.Timestamp);
            entity.HasIndex(e => new { e.AuthorId, e.Timestamp });
        });

        modelBuilder.Entity<MemoryPost>(entity =>
        {
            entity.ToTable("MemoryPosts");

            entity.HasIndex(e => new { e.GuildId, e.PostType }).IsUnique();
        });
    }
}
