using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Oiwi.Data;

#nullable disable

namespace Oiwi.Data.Migrations;

[DbContext(typeof(OiwiDbContext))]
partial class OiwiDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
#pragma warning disable 612, 618
        modelBuilder.HasAnnotation("ProductVersion", "8.0.0");

        modelBuilder.Entity("Oiwi.Data.Entities.Document", b =>
        {
            b.Property<int>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("INTEGER");

            b.Property<string>("Comment")
                .HasColumnType("TEXT");

            b.Property<string>("DeleteReason")
                .HasColumnType("TEXT");

            b.Property<DateTime?>("DeletedAt")
                .HasColumnType("TEXT");

            b.Property<string>("DeletedBy")
                .HasColumnType("TEXT");

            b.Property<string>("DocumentCode")
                .IsRequired()
                .HasColumnType("TEXT");

            b.Property<string>("DocumentType")
                .IsRequired()
                .HasColumnType("TEXT");

            b.Property<bool>("IsDeleted")
                .ValueGeneratedOnAdd()
                .HasColumnType("INTEGER")
                .HasDefaultValue(false);

            b.Property<DateTime?>("LastUpdatedAt")
                .HasColumnType("TEXT");

            b.Property<string>("Line")
                .HasColumnType("TEXT");

            b.Property<string>("MachineName")
                .HasColumnType("TEXT");

            b.Property<string>("Model")
                .HasColumnType("TEXT");

            b.Property<string>("Station")
                .HasColumnType("TEXT");

            b.Property<string>("Title")
                .IsRequired()
                .HasColumnType("TEXT");

            b.Property<DateTime>("UploadedAt")
                .HasColumnType("TEXT");

            b.Property<string>("UploadedBy")
                .IsRequired()
                .HasColumnType("TEXT");

            b.HasKey("Id");

            b.HasIndex("DocumentCode")
                .IsUnique();

            b.ToTable("Documents");
        });

        modelBuilder.Entity("Oiwi.Data.Entities.DocumentFile", b =>
        {
            b.Property<int>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("INTEGER");

            b.Property<int>("DocumentId")
                .HasColumnType("INTEGER");

            b.Property<DateTime>("EffectiveDate")
                .HasColumnType("TEXT");

            b.Property<string>("FileName")
                .IsRequired()
                .HasColumnType("TEXT");

            b.Property<bool>("IsActive")
                .HasColumnType("INTEGER");

            b.Property<string>("RelativePath")
                .IsRequired()
                .HasColumnType("TEXT");

            b.Property<long>("SizeBytes")
                .HasColumnType("INTEGER");

            b.Property<int>("VersionNo")
                .HasColumnType("INTEGER");

            b.HasKey("Id");

            b.HasIndex("DocumentId");

            b.ToTable("DocumentFiles");
        });

        modelBuilder.Entity("Oiwi.Data.Entities.DocumentHistory", b =>
        {
            b.Property<int>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("INTEGER");

            b.Property<string>("Action")
                .IsRequired()
                .HasColumnType("TEXT");

            b.Property<DateTime>("ChangedAt")
                .HasColumnType("TEXT");

            b.Property<string>("ChangedBy")
                .IsRequired()
                .HasColumnType("TEXT");

            b.Property<int>("DocumentId")
                .HasColumnType("INTEGER");

            b.Property<int?>("OldFileVersion")
                .HasColumnType("INTEGER");

            b.Property<string>("OldDocumentCode")
                .HasColumnType("TEXT");

            b.Property<string>("Reason")
                .HasColumnType("TEXT");

            b.HasKey("Id");

            b.HasIndex("DocumentId");

            b.ToTable("DocumentHistories");
        });

        modelBuilder.Entity("Oiwi.Data.Entities.StampInfo", b =>
        {
            b.Property<int>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("INTEGER");

            b.Property<int>("DocumentId")
                .HasColumnType("INTEGER");

            b.Property<string>("Mode")
                .IsRequired()
                .HasColumnType("TEXT");

            b.Property<DateTime>("StampDate")
                .HasColumnType("TEXT");

            b.HasKey("Id");

            b.HasIndex("DocumentId")
                .IsUnique();

            b.ToTable("StampInfos");
        });

        modelBuilder.Entity("Oiwi.Data.Entities.DocumentFile", b =>
        {
            b.HasOne("Oiwi.Data.Entities.Document", "Document")
                .WithMany("Files")
                .HasForeignKey("DocumentId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();

            b.Navigation("Document");
        });

        modelBuilder.Entity("Oiwi.Data.Entities.DocumentHistory", b =>
        {
            b.HasOne("Oiwi.Data.Entities.Document", "Document")
                .WithMany("Histories")
                .HasForeignKey("DocumentId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();

            b.Navigation("Document");
        });

        modelBuilder.Entity("Oiwi.Data.Entities.StampInfo", b =>
        {
            b.HasOne("Oiwi.Data.Entities.Document", "Document")
                .WithOne("StampInfo")
                .HasForeignKey("Oiwi.Data.Entities.StampInfo", "DocumentId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();

            b.Navigation("Document");
        });

        modelBuilder.Entity("Oiwi.Data.Entities.Document", b =>
        {
            b.Navigation("Files");

            b.Navigation("Histories");

            b.Navigation("StampInfo");
        });
#pragma warning restore 612, 618
    }
}
