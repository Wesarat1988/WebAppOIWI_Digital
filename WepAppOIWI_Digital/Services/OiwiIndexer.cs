using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WepAppOIWI_Digital.Data;

namespace WepAppOIWI_Digital.Services;

public sealed class OiwiIndexer : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<OiwiIndexer> _logger;
    private readonly IOptionsMonitor<OiwiOptions> _options;

    public OiwiIndexer(
        IServiceProvider serviceProvider,
        ILogger<OiwiIndexer> logger,
        IOptionsMonitor<OiwiOptions> options)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunIndexAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error while indexing OI/WI documents.");
            }

            var intervalSeconds = Math.Max(30, _options.CurrentValue.ScanIntervalSeconds);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunIndexAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        if (string.IsNullOrWhiteSpace(options.SharePath))
        {
            _logger.LogWarning("OI/WI share path is not configured.");
            return;
        }

        if (!Directory.Exists(options.SharePath))
        {
            _logger.LogWarning("OI/WI share path '{Path}' is not accessible.", options.SharePath);
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<DocumentCatalogService>();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var dbContext = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<DocumentRecord> documents;
        try
        {
            documents = await catalog.LoadDocumentsFromSourceAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load documents from source while indexing.");
            return;
        }

        var existing = await dbContext.Documents
            .AsTracking()
            .ToDictionaryAsync(d => d.NormalizedPath, StringComparer.OrdinalIgnoreCase, cancellationToken)
            .ConfigureAwait(false);

        var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;
        var batchSize = Math.Clamp(options.BatchSize, 50, 2000);
        var changes = 0;
        var hasMutations = false;

        var scanResults = new System.Collections.Concurrent.ConcurrentBag<DocumentScanResult>();
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = options.MaxParallelDirs > 0
                ? Math.Min(options.MaxParallelDirs, Environment.ProcessorCount)
                : Environment.ProcessorCount
        };

        await Parallel.ForEachAsync(documents, parallelOptions, (record, ct) =>
        {
            if (string.IsNullOrWhiteSpace(record.FileName))
            {
                return ValueTask.CompletedTask;
            }

            var normalizedPath = NormalizePath(record.FileName);
            if (!IsExtensionAllowed(normalizedPath, options.IncludeExtensions))
            {
                return ValueTask.CompletedTask;
            }

            long sizeBytes = 0L;
            DateTimeOffset? lastWrite = record.UpdatedAt;

            try
            {
                var physicalPath = catalog.ResolveDocumentPhysicalPath(normalizedPath);
                if (!string.IsNullOrEmpty(physicalPath) && File.Exists(physicalPath))
                {
                    var info = new FileInfo(physicalPath);
                    sizeBytes = info.Length;
                    lastWrite = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                }
            }
            catch (Exception ex)
            {
                // Best-effort metadata collection; keep going if resolution fails
                _logger.LogDebug(ex, "Failed to inspect file {Path} while indexing.", normalizedPath);
            }

            scanResults.Add(new DocumentScanResult(normalizedPath, record, sizeBytes, lastWrite));
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);

        foreach (var result in scanResults.OrderBy(static r => r.NormalizedPath, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            processed.Add(result.NormalizedPath);

            if (!existing.TryGetValue(result.NormalizedPath, out var entity))
            {
                entity = new DocumentEntity
                {
                    Id = Guid.NewGuid(),
                    NormalizedPath = result.NormalizedPath,
                };

                dbContext.Documents.Add(entity);
                existing[result.NormalizedPath] = entity;
                changes++;
                hasMutations = true;
            }

            var updated = ApplyRecord(entity, result.Record, result.NormalizedPath, result.SizeBytes, result.LastWriteUtc, now);
            if (updated)
            {
                changes++;
                hasMutations = true;
            }

            if (changes >= batchSize)
            {
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                changes = 0;
            }
        }

        if (changes > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        var removals = existing
            .Where(pair => !processed.Contains(pair.Key))
            .Select(pair => pair.Value)
            .ToList();

        if (removals.Count > 0)
        {
            dbContext.Documents.RemoveRange(removals);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            hasMutations = true;
        }

        if (processed.Count == 0 && removals.Count == 0)
        {
            _logger.LogInformation("OI/WI indexer found no documents to process.");
            return;
        }

        if (hasMutations)
        {
            catalog.InvalidateCache();
        }

        _logger.LogInformation(
            "OI/WI index refresh complete. Processed {Processed} documents, removed {Removed} entries.",
            processed.Count,
            removals.Count);
    }

    private static string NormalizePath(string path)
        => path.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Trim();

    private static bool IsExtensionAllowed(string normalizedPath, IReadOnlyList<string> includeExtensions)
    {
        if (includeExtensions is null || includeExtensions.Count == 0)
        {
            return true;
        }

        var extension = Path.GetExtension(normalizedPath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            return true;
        }

        for (var i = 0; i < includeExtensions.Count; i++)
        {
            var candidate = includeExtensions[i];
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (string.Equals(candidate.Trim(), extension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SetIfDifferent<T>(ref T target, T value) where T : class
    {
        if (EqualityComparer<T>.Default.Equals(target, value))
        {
            return false;
        }

        target = value;
        return true;
    }

    private static bool SetStructIfDifferent<T>(ref T target, T value) where T : struct
    {
        if (EqualityComparer<T>.Default.Equals(target, value))
        {
            return false;
        }

        target = value;
        return true;
    }

    private static bool SetNullableStructIfDifferent<T>(ref T? target, T? value) where T : struct
    {
        if (Nullable.Equals(target, value))
        {
            return false;
        }

        target = value;
        return true;
    }

    private static string? TrimOrNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool ApplyRecord(
        DocumentEntity entity,
        DocumentRecord record,
        string normalizedPath,
        long sizeBytes,
        DateTimeOffset? lastWriteUtc,
        DateTimeOffset indexedAt)
    {
        var changed = false;

        changed |= SetIfDifferent(ref entity.FileName, Path.GetFileName(normalizedPath));
        changed |= SetIfDifferent(ref entity.RelativePath, normalizedPath);
        changed |= SetIfDifferent(ref entity.DisplayName, record.DisplayName ?? string.Empty);
        changed |= SetIfDifferent(ref entity.Line, TrimOrNull(record.Line));
        changed |= SetIfDifferent(ref entity.Station, TrimOrNull(record.Station));
        changed |= SetIfDifferent(ref entity.Model, TrimOrNull(record.Model));
        changed |= SetIfDifferent(ref entity.Machine, TrimOrNull(record.Machine));
        changed |= SetNullableStructIfDifferent(ref entity.UpdatedAt, record.UpdatedAt?.ToUniversalTime());
        changed |= SetIfDifferent(ref entity.UploadedBy, TrimOrNull(record.UploadedBy));
        changed |= SetIfDifferent(ref entity.Comment, TrimOrNull(record.Comment));
        changed |= SetIfDifferent(ref entity.DocumentType, TrimOrNull(record.DocumentType));
        changed |= SetNullableStructIfDifferent(ref entity.SequenceNumber, record.SequenceNumber);
        changed |= SetIfDifferent(ref entity.ActiveVersionId, TrimOrNull(record.ActiveVersionId));
        changed |= SetIfDifferent(ref entity.DocumentCode, TrimOrNull(record.DocumentCode));
        changed |= SetStructIfDifferent(ref entity.Version, record.Version);
        changed |= SetIfDifferent(ref entity.LinkUrl, record.LinkUrl);

        var effectiveSize = sizeBytes > 0 ? sizeBytes : entity.SizeBytes;
        var effectiveLastWrite = lastWriteUtc ?? entity.LastWriteUtc ?? record.UpdatedAt;

        var updatedAt = entity.UpdatedAt ?? effectiveLastWrite ?? DateTimeOffset.MinValue;
        var unixMs = updatedAt.ToUniversalTime().ToUnixTimeMilliseconds();
        changed |= SetStructIfDifferent(ref entity.UpdatedAtUnixMs, unixMs);
        changed |= SetStructIfDifferent(ref entity.SizeBytes, effectiveSize);
        changed |= SetNullableStructIfDifferent(ref entity.LastWriteUtc, effectiveLastWrite?.ToUniversalTime());
        changed |= SetStructIfDifferent(ref entity.IndexedAtUtc, indexedAt);

        return changed;
    }

    private sealed record DocumentScanResult(string NormalizedPath, DocumentRecord Record, long SizeBytes, DateTimeOffset? LastWriteUtc);
}
