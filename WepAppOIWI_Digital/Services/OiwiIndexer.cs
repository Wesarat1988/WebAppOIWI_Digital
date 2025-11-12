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

        static bool UpdateString(
            DocumentEntity e,
            Func<DocumentEntity, string?> getter,
            Action<DocumentEntity, string?> setter,
            string? value)
        {
            var current = getter(e);
            if (string.Equals(current, value, StringComparison.Ordinal))
            {
                return false;
            }

            setter(e, value);
            return true;
        }

        static bool UpdateStruct<T>(
            DocumentEntity e,
            Func<DocumentEntity, T> getter,
            Action<DocumentEntity, T> setter,
            T value) where T : struct
        {
            if (EqualityComparer<T>.Default.Equals(getter(e), value))
            {
                return false;
            }

            setter(e, value);
            return true;
        }

        static bool UpdateNullableStruct<T>(
            DocumentEntity e,
            Func<DocumentEntity, T?> getter,
            Action<DocumentEntity, T?> setter,
            T? value) where T : struct
        {
            if (Nullable.Equals(getter(e), value))
            {
                return false;
            }

            setter(e, value);
            return true;
        }

        changed |= UpdateString(entity, static e => e.FileName, static (e, v) => e.FileName = v, Path.GetFileName(normalizedPath));
        changed |= UpdateString(entity, static e => e.RelativePath, static (e, v) => e.RelativePath = v, normalizedPath);
        changed |= UpdateString(entity, static e => e.DisplayName, static (e, v) => e.DisplayName = v, record.DisplayName ?? string.Empty);
        changed |= UpdateString(entity, static e => e.Line, static (e, v) => e.Line = v, TrimOrNull(record.Line));
        changed |= UpdateString(entity, static e => e.Station, static (e, v) => e.Station = v, TrimOrNull(record.Station));
        changed |= UpdateString(entity, static e => e.Model, static (e, v) => e.Model = v, TrimOrNull(record.Model));
        changed |= UpdateString(entity, static e => e.Machine, static (e, v) => e.Machine = v, TrimOrNull(record.Machine));
        changed |= UpdateNullableStruct(entity, static e => e.UpdatedAt, static (e, v) => e.UpdatedAt = v, record.UpdatedAt?.ToUniversalTime());
        changed |= UpdateString(entity, static e => e.UploadedBy, static (e, v) => e.UploadedBy = v, TrimOrNull(record.UploadedBy));
        changed |= UpdateString(entity, static e => e.Comment, static (e, v) => e.Comment = v, TrimOrNull(record.Comment));
        changed |= UpdateString(entity, static e => e.DocumentType, static (e, v) => e.DocumentType = v, TrimOrNull(record.DocumentType));
        changed |= UpdateNullableStruct(entity, static e => e.SequenceNumber, static (e, v) => e.SequenceNumber = v, record.SequenceNumber);
        changed |= UpdateString(entity, static e => e.ActiveVersionId, static (e, v) => e.ActiveVersionId = v, TrimOrNull(record.ActiveVersionId));
        changed |= UpdateString(entity, static e => e.DocumentCode, static (e, v) => e.DocumentCode = v, TrimOrNull(record.DocumentCode));
        changed |= UpdateStruct(entity, static e => e.Version, static (e, v) => e.Version = v, record.Version);
        changed |= UpdateString(entity, static e => e.LinkUrl, static (e, v) => e.LinkUrl = v, record.LinkUrl);

        var effectiveSize = sizeBytes > 0 ? sizeBytes : entity.SizeBytes;
        var effectiveLastWrite = lastWriteUtc ?? entity.LastWriteUtc ?? record.UpdatedAt;

        var updatedAt = entity.UpdatedAt ?? effectiveLastWrite ?? DateTimeOffset.MinValue;
        var unixMs = updatedAt.ToUniversalTime().ToUnixTimeMilliseconds();
        changed |= UpdateStruct(entity, static e => e.UpdatedAtUnixMs, static (e, v) => e.UpdatedAtUnixMs = v, unixMs);
        changed |= UpdateStruct(entity, static e => e.SizeBytes, static (e, v) => e.SizeBytes = v, effectiveSize);
        changed |= UpdateNullableStruct(entity, static e => e.LastWriteUtc, static (e, v) => e.LastWriteUtc = v, effectiveLastWrite?.ToUniversalTime());
        changed |= UpdateStruct(entity, static e => e.IndexedAtUtc, static (e, v) => e.IndexedAtUtc = v, indexedAt);

        return changed;
    }

    private sealed record DocumentScanResult(string NormalizedPath, DocumentRecord Record, long SizeBytes, DateTimeOffset? LastWriteUtc);
}
