using Microsoft.EntityFrameworkCore;
using Oiwi.Data.Entities;

namespace Oiwi.Data;

public class OiwiDbContext : DbContext
{
    public OiwiDbContext(DbContextOptions<OiwiDbContext> options)
        : base(options)
    {
    }

    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentFile> DocumentFiles => Set<DocumentFile>();
    public DbSet<DocumentHistory> DocumentHistories => Set<DocumentHistory>();
    public DbSet<StampInfo> StampInfos => Set<StampInfo>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Document>(entity =>
        {
            entity.HasIndex(e => e.DocumentCode).IsUnique();

            entity.Property(e => e.IsDeleted)
                .HasDefaultValue(false);

            entity.HasMany(e => e.Files)
                .WithOne(f => f.Document)
                .HasForeignKey(f => f.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Histories)
                .WithOne(h => h.Document)
                .HasForeignKey(h => h.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.StampInfo)
                .WithOne(s => s.Document)
                .HasForeignKey<StampInfo>(s => s.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DocumentFile>(entity =>
        {
            entity.Property(e => e.FileName).IsRequired();
            entity.Property(e => e.RelativePath).IsRequired();
        });

        modelBuilder.Entity<DocumentHistory>(entity =>
        {
            entity.Property(e => e.Action).IsRequired();
            entity.Property(e => e.ChangedBy).IsRequired();
        });

        modelBuilder.Entity<StampInfo>(entity =>
        {
            entity.Property(e => e.Mode).IsRequired();
        });
    }
}
