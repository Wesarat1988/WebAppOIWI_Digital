using System;
using Microsoft.EntityFrameworkCore;

namespace WepAppOIWI_Digital.Data;

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<DocumentEntity> Documents => Set<DocumentEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var doc = modelBuilder.Entity<DocumentEntity>();
        doc.HasKey(x => x.Id);
        doc.Property(x => x.Id).ValueGeneratedNever();
        doc.Property(x => x.NormalizedPath).IsRequired();
        doc.Property(x => x.DisplayName).IsRequired();
        doc.Property(x => x.FileName).IsRequired(false);
        doc.Property(x => x.DocumentType).IsRequired(false);
        doc.Property(x => x.Line).IsRequired(false);
        doc.Property(x => x.Station).IsRequired(false);
        doc.Property(x => x.Model).IsRequired(false);
        doc.Property(x => x.Machine).IsRequired(false);
        doc.Property(x => x.UploadedBy).IsRequired(false);
        doc.Property(x => x.Comment).IsRequired(false);
        doc.Property(x => x.LinkUrl).IsRequired(false);
        doc.Property(x => x.ActiveVersionId).IsRequired(false);
        doc.Property(x => x.DocumentCode).IsRequired(false);
        doc.Property(x => x.SequenceNumber).IsRequired(false);
        doc.Property(x => x.Version).HasDefaultValue(1);
        doc.Property(x => x.IndexedAtUtc).IsRequired();

        doc.HasIndex(x => x.NormalizedPath).IsUnique();
        doc.HasIndex(x => x.UpdatedAt);
        doc.HasIndex(x => x.DocumentCode);
        doc.HasIndex(x => x.DisplayName);
        doc.HasIndex(x => new { x.Line, x.Station, x.Model });
        doc.HasIndex(x => x.Machine);
        doc.HasIndex(x => x.UploadedBy);
    }
}

public sealed class DocumentEntity
{
    public Guid Id { get; set; }
    public string NormalizedPath { get; set; } = string.Empty;
    public string? FileName { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? Line { get; set; }
    public string? Station { get; set; }
    public string? Model { get; set; }
    public string? Machine { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? UploadedBy { get; set; }
    public string? Comment { get; set; }
    public string? DocumentType { get; set; }
    public int? SequenceNumber { get; set; }
    public string? ActiveVersionId { get; set; }
    public string? DocumentCode { get; set; }
    public int Version { get; set; }
    public string? LinkUrl { get; set; }
    public DateTimeOffset IndexedAtUtc { get; set; }
}
