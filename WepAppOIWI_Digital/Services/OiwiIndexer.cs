using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WepAppOIWI_Digital.Data;

namespace WepAppOIWI_Digital.Services;

public sealed class OiwiIndexer : BackgroundService
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(3);

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<OiwiIndexer> _logger;
    private readonly TimeSpan _interval;

    public OiwiIndexer(IServiceProvider serviceProvider, ILogger<OiwiIndexer> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _interval = DefaultInterval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunIndexAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            await RunIndexAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RunIndexAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var catalog = scope.ServiceProvider.GetRequiredService<DocumentCatalogService>();
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using var dbContext = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

            var documents = await catalog.LoadDocumentsFromSourceAsync(cancellationToken).ConfigureAwait(false);

            var existing = await dbContext.Documents.ToListAsync(cancellationToken).ConfigureAwait(false);
            var lookup = existing.ToDictionary(d => d.NormalizedPath, StringComparer.OrdinalIgnoreCase);
            var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var indexedAt = DateTimeOffset.UtcNow;

            foreach (var record in documents)
            {
                if (string.IsNullOrWhiteSpace(record.FileName))
                {
                    continue;
                }

                if (!lookup.TryGetValue(record.FileName, out var entity))
                {
                    entity = new DocumentEntity
                    {
                        Id = Guid.NewGuid(),
                        NormalizedPath = record.FileName
                    };
                    dbContext.Documents.Add(entity);
                }

                entity.FileName = Path.GetFileName(record.FileName);
                entity.DisplayName = record.DisplayName;
                entity.Line = record.Line;
                entity.Station = record.Station;
                entity.Model = record.Model;
                entity.Machine = record.Machine;
                var updatedAt = record.UpdatedAt?.ToUniversalTime();
                entity.UpdatedAt = updatedAt;
                entity.UpdatedAtUnixMs = updatedAt?.ToUnixTimeMilliseconds() ?? 0L;
                entity.UploadedBy = record.UploadedBy;
                entity.Comment = record.Comment;
                entity.DocumentType = record.DocumentType;
                entity.SequenceNumber = record.SequenceNumber;
                entity.ActiveVersionId = record.ActiveVersionId;
                entity.DocumentCode = record.DocumentCode;
                entity.Version = record.Version;
                entity.LinkUrl = record.LinkUrl;
                entity.IndexedAtUtc = indexedAt;

                processed.Add(record.FileName);
                lookup.Remove(record.FileName);
            }

            if (lookup.Count > 0)
            {
                dbContext.Documents.RemoveRange(lookup.Values);
            }

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            catalog.InvalidateCache();

            _logger.LogInformation("Indexed {Count} OI/WI records at {Timestamp}.", processed.Count, indexedAt);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to index OI/WI catalog.");
        }
    }
}
