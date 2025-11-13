using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Oiwi.Data;
using Oiwi.Data.Entities;

namespace WepAppOIWI_Digital.Services;

public class DocumentV2Service
{
    private readonly OiwiDbContext _db;
    private readonly ILogger<DocumentV2Service> _logger;

    public DocumentV2Service(OiwiDbContext db, ILogger<DocumentV2Service> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task EnsureSeedDataAsync(CancellationToken cancellationToken = default)
    {
        if (await _db.Documents.AnyAsync(d => !d.IsDeleted, cancellationToken))
        {
            return;
        }

        var utcNow = DateTime.UtcNow;
        var seed = new Document
        {
            DocumentCode = "OI-9000",
            DocumentType = "OI",
            Title = "TEST OI FROM V2",
            Line = "STF03",
            Station = "AS-01",
            Model = "FA121A08-P84",
            MachineName = "Test Machine",
            Comment = "Seed data",
            UploadedBy = "System",
            UploadedAt = utcNow,
            LastUpdatedAt = utcNow
        };

        _db.Documents.Add(seed);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task<List<Document>> GetDocumentsAsync(bool includeDeleted = false, CancellationToken cancellationToken = default)
    {
        var query = _db.Documents
            .AsNoTracking()
            .OrderBy(d => d.DocumentCode)
            .AsQueryable();

        if (!includeDeleted)
        {
            query = query.Where(d => !d.IsDeleted);
        }

        return query.ToListAsync(cancellationToken);
    }

    public Task<Document?> GetDocumentAsync(int id, CancellationToken cancellationToken = default)
    {
        return _db.Documents
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
    }

    public async Task DeleteDocumentAsync(int documentId, string deletedBy, string deleteReason, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deletedBy))
        {
            throw new ArgumentException("A deleted by value is required.", nameof(deletedBy));
        }

        if (string.IsNullOrWhiteSpace(deleteReason))
        {
            throw new ArgumentException("A delete reason is required.", nameof(deleteReason));
        }

        var document = await _db.Documents
            .Include(d => d.Files)
            .FirstOrDefaultAsync(d => d.Id == documentId, cancellationToken);

        if (document is null)
        {
            throw new InvalidOperationException($"Document with Id {documentId} not found.");
        }

        if (document.IsDeleted)
        {
            _logger.LogInformation("Document {DocumentId} is already deleted; skipping delete request.", documentId);
            return;
        }

        var trimmedDeletedBy = deletedBy.Trim();
        var trimmedReason = deleteReason.Trim();
        var utcNow = DateTime.UtcNow;

        document.IsDeleted = true;
        document.DeletedAt = utcNow;
        document.DeletedBy = trimmedDeletedBy;
        document.DeleteReason = trimmedReason;
        document.LastUpdatedAt = utcNow;

        var activeVersion = document.Files.FirstOrDefault(f => f.IsActive)?.VersionNo;

        var history = new DocumentHistory
        {
            DocumentId = document.Id,
            Action = "Deleted",
            ChangedBy = trimmedDeletedBy,
            ChangedAt = utcNow,
            Reason = trimmedReason,
            OldDocumentCode = document.DocumentCode,
            OldFileVersion = activeVersion
        };

        _db.DocumentHistories.Add(history);

        await _db.SaveChangesAsync(cancellationToken);
    }
}
