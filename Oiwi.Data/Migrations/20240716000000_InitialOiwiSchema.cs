using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Oiwi.Data.Migrations;

public partial class InitialOiwiSchema : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Documents",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                DocumentCode = table.Column<string>(type: "TEXT", nullable: false),
                DocumentType = table.Column<string>(type: "TEXT", nullable: false),
                Title = table.Column<string>(type: "TEXT", nullable: false),
                Line = table.Column<string>(type: "TEXT", nullable: true),
                Station = table.Column<string>(type: "TEXT", nullable: true),
                Model = table.Column<string>(type: "TEXT", nullable: true),
                MachineName = table.Column<string>(type: "TEXT", nullable: true),
                Comment = table.Column<string>(type: "TEXT", nullable: true),
                UploadedBy = table.Column<string>(type: "TEXT", nullable: false),
                UploadedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                LastUpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Documents", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "DocumentFiles",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                DocumentId = table.Column<int>(type: "INTEGER", nullable: false),
                VersionNo = table.Column<int>(type: "INTEGER", nullable: false),
                FileName = table.Column<string>(type: "TEXT", nullable: false),
                RelativePath = table.Column<string>(type: "TEXT", nullable: false),
                SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                EffectiveDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DocumentFiles", x => x.Id);
                table.ForeignKey(
                    name: "FK_DocumentFiles_Documents_DocumentId",
                    column: x => x.DocumentId,
                    principalTable: "Documents",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "DocumentHistories",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                DocumentId = table.Column<int>(type: "INTEGER", nullable: false),
                Action = table.Column<string>(type: "TEXT", nullable: false),
                ChangedBy = table.Column<string>(type: "TEXT", nullable: false),
                ChangedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                Reason = table.Column<string>(type: "TEXT", nullable: true),
                OldDocumentCode = table.Column<string>(type: "TEXT", nullable: true),
                OldFileVersion = table.Column<int>(type: "INTEGER", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DocumentHistories", x => x.Id);
                table.ForeignKey(
                    name: "FK_DocumentHistories_Documents_DocumentId",
                    column: x => x.DocumentId,
                    principalTable: "Documents",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "StampInfos",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                DocumentId = table.Column<int>(type: "INTEGER", nullable: false),
                Mode = table.Column<string>(type: "TEXT", nullable: false),
                StampDate = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_StampInfos", x => x.Id);
                table.ForeignKey(
                    name: "FK_StampInfos_Documents_DocumentId",
                    column: x => x.DocumentId,
                    principalTable: "Documents",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_DocumentFiles_DocumentId",
            table: "DocumentFiles",
            column: "DocumentId");

        migrationBuilder.CreateIndex(
            name: "IX_DocumentHistories_DocumentId",
            table: "DocumentHistories",
            column: "DocumentId");

        migrationBuilder.CreateIndex(
            name: "IX_Documents_DocumentCode",
            table: "Documents",
            column: "DocumentCode",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_StampInfos_DocumentId",
            table: "StampInfos",
            column: "DocumentId",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "DocumentFiles");

        migrationBuilder.DropTable(
            name: "DocumentHistories");

        migrationBuilder.DropTable(
            name: "StampInfos");

        migrationBuilder.DropTable(
            name: "Documents");
    }

    protected override void BuildTargetModel(ModelBuilder modelBuilder)
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

            b.Property<string>("DocumentCode")
                .IsRequired()
                .HasColumnType("TEXT");

            b.Property<string>("DocumentType")
                .IsRequired()
                .HasColumnType("TEXT");

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
