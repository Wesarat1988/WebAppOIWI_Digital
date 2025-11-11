using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace WepAppOIWI_Digital.Services;

public sealed class FilesystemVersionStore : IVersionStore
{
    private const int MaxSnapshotsPerDocument = 20;
    private static readonly HashSet<char> InvalidSegmentCharacters = new(Path.GetInvalidFileNameChars());

    private readonly DocumentCatalogService _catalogService;
    private readonly ILogger<FilesystemVersionStore> _logger;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public FilesystemVersionStore(DocumentCatalogService catalogService, ILogger<FilesystemVersionStore> logger)
    {
        _catalogService = catalogService;
        _logger = logger;
    }

    public async Task<IReadOnlyList<VersionDescriptor>> ListAsync(string normalizedPath, int take = 5, CancellationToken ct = default)
    {
        try
        {
            var versionDirectory = await GetVersionDirectoryAsync(normalizedPath, ensureExists: false, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(versionDirectory) || !Directory.Exists(versionDirectory))
            {
                return Array.Empty<VersionDescriptor>();
            }

            var descriptors = new List<VersionDescriptor>();
            var metadataFiles = Directory
                .EnumerateFiles(versionDirectory, "*.json", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => Path.GetFileNameWithoutExtension(file), StringComparer.Ordinal);

            foreach (var metadataPath in metadataFiles)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var metadata = await ReadMetadataAsync(metadataPath, ct).ConfigureAwait(false);
                    if (metadata?.Descriptor is null)
                    {
                        continue;
                    }

                    var baseName = Path.GetFileNameWithoutExtension(metadataPath);
                    var binaryPath = Path.Combine(Path.GetDirectoryName(metadataPath)!, baseName + ".bin");
                    var size = TryGetFileLength(binaryPath);

                    var descriptor = metadata.Descriptor with { SizeBytes = size, PublicUrl = null };
                    descriptors.Add(descriptor);

                    if (descriptors.Count >= take)
                    {
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read snapshot metadata at {MetadataPath}.", metadataPath);
                }
            }

            return descriptors;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list snapshots for {NormalizedPath}.", normalizedPath);
            return Array.Empty<VersionDescriptor>();
        }
    }

    public Task<bool> SnapshotAsync(
        string normalizedPath,
        string physicalPath,
        string? actor,
        string? comment,
        CancellationToken ct = default)
        => SnapshotInternalAsync(normalizedPath, physicalPath, actor, comment, protectedVersionId: null, ct);

    public async Task<bool> RestoreAsync(
        string normalizedPath,
        string versionId,
        string physicalPath,
        string? actor,
        string? comment,
        CancellationToken ct = default)
    {
        string? backupPath = null;

        try
        {
            var versionDirectory = await GetVersionDirectoryAsync(normalizedPath, ensureExists: false, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(versionDirectory))
            {
                return false;
            }

            var binaryPath = Path.Combine(versionDirectory, versionId + ".bin");
            if (!File.Exists(binaryPath))
            {
                _logger.LogWarning("Snapshot {VersionId} for {NormalizedPath} not found.", versionId, normalizedPath);
                return false;
            }

            if (File.Exists(physicalPath))
            {
                var snapshotComment = string.IsNullOrWhiteSpace(comment)
                    ? $"Snapshot before restore to {versionId}"
                    : $"{comment} (before restore)";

                await SnapshotInternalAsync(normalizedPath, physicalPath, actor, snapshotComment, versionId, ct).ConfigureAwait(false);

                backupPath = $"{physicalPath}.restore-bak-{Guid.NewGuid():N}";
                TryCreateDirectory(Path.GetDirectoryName(backupPath));
                File.Copy(physicalPath, backupPath, overwrite: false);
            }

            TryCreateDirectory(Path.GetDirectoryName(physicalPath));
            await CopyFileAsync(binaryPath, physicalPath, ct).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(backupPath))
            {
                TryDeleteFile(backupPath);
                backupPath = null;
            }

            await TrimSnapshotsAsync(versionDirectory, protectedVersionId: null, ct).ConfigureAwait(false);

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore version {VersionId} for {NormalizedPath}.", versionId, normalizedPath);

            if (!string.IsNullOrEmpty(backupPath) && File.Exists(backupPath))
            {
                try
                {
                    TryCreateDirectory(Path.GetDirectoryName(physicalPath));
                    File.Copy(backupPath, physicalPath, overwrite: true);
                }
                catch (Exception restoreEx)
                {
                    _logger.LogError(restoreEx, "Failed to restore backup after unsuccessful restore for {NormalizedPath}.", normalizedPath);
                }
            }

            return false;
        }
        finally
        {
            if (!string.IsNullOrEmpty(backupPath))
            {
                TryDeleteFile(backupPath);
            }
        }
    }

    private async Task<bool> SnapshotInternalAsync(
        string normalizedPath,
        string physicalPath,
        string? actor,
        string? comment,
        string? protectedVersionId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(physicalPath) || !File.Exists(physicalPath))
        {
            _logger.LogDebug("Skipping snapshot for {NormalizedPath} because the file '{PhysicalPath}' does not exist.", normalizedPath, physicalPath);
            return false;
        }

        string? binaryPath = null;
        string? metadataPath = null;

        try
        {
            var timestamp = DateTimeOffset.UtcNow;
            var versionDirectory = await GetVersionDirectoryAsync(normalizedPath, ensureExists: true, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(versionDirectory))
            {
                return false;
            }

            var baseName = CreateSnapshotBaseName(timestamp);
            binaryPath = Path.Combine(versionDirectory, baseName + ".bin");
            metadataPath = Path.Combine(versionDirectory, baseName + ".json");

            await CopyFileAsync(physicalPath, binaryPath, ct).ConfigureAwait(false);

            var descriptor = new VersionDescriptor(
                baseName,
                timestamp,
                actor,
                comment,
                TryGetFileLength(binaryPath));

            var metadata = new SnapshotMetadata
            {
                Descriptor = descriptor,
                OriginalFileName = Path.GetFileName(physicalPath)
            };

            await WriteMetadataAsync(metadataPath, metadata, ct).ConfigureAwait(false);
            await TrimSnapshotsAsync(versionDirectory, protectedVersionId, ct).ConfigureAwait(false);

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create snapshot for {NormalizedPath}.", normalizedPath);
            if (!string.IsNullOrEmpty(binaryPath))
            {
                TryDeleteFile(binaryPath);
            }
            if (!string.IsNullOrEmpty(metadataPath))
            {
                TryDeleteFile(metadataPath);
            }
            return false;
        }
    }

    private async Task<string?> GetVersionDirectoryAsync(string normalizedPath, bool ensureExists, CancellationToken ct)
    {
        var context = await _catalogService.EnsureCatalogContextAsync(ct).ConfigureAwait(false);
        var root = context.ActiveRootPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            _logger.LogWarning("Catalog root is not configured. Skipping version storage for {NormalizedPath}.", normalizedPath);
            return null;
        }

        var versionRoot = Path.Combine(root, ".versions");
        var path = versionRoot;

        foreach (var segment in NormalizeSegments(normalizedPath))
        {
            path = Path.Combine(path, segment);
        }

        if (!ensureExists)
        {
            return path;
        }

        try
        {
            Directory.CreateDirectory(path);
            return path;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create version directory {Directory} for {NormalizedPath}.", path, normalizedPath);
            return null;
        }
    }

    private static IEnumerable<string> NormalizeSegments(string normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            yield break;
        }

        var segments = normalizedPath
            .Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        foreach (var rawSegment in segments)
        {
            if (string.Equals(rawSegment, ".", StringComparison.Ordinal) || string.Equals(rawSegment, "..", StringComparison.Ordinal))
            {
                continue;
            }

            var filtered = string.Concat(rawSegment.Where(ch => !InvalidSegmentCharacters.Contains(ch) && ch != Path.DirectorySeparatorChar && ch != Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(filtered))
            {
                continue;
            }

            yield return filtered;
        }
    }

    private static string CreateSnapshotBaseName(DateTimeOffset timestamp)
    {
        var ticks = timestamp.UtcTicks;
        var hash = Guid.NewGuid().ToString("N")[..8];
        return $"{ticks}-{hash}";
    }

    private static async Task CopyFileAsync(string sourcePath, string destinationPath, CancellationToken ct)
    {
        const int bufferSize = 81920;
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true);
        await using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true);
        await source.CopyToAsync(destination, bufferSize, ct).ConfigureAwait(false);
    }

    private static long? TryGetFileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<SnapshotMetadata?> ReadMetadataAsync(string metadataPath, CancellationToken ct)
    {
        await using var stream = new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        return await JsonSerializer.DeserializeAsync<SnapshotMetadata>(stream, _serializerOptions, ct).ConfigureAwait(false);
    }

    private async Task WriteMetadataAsync(string metadataPath, SnapshotMetadata metadata, CancellationToken ct)
    {
        await using var stream = new FileStream(metadataPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await JsonSerializer.SerializeAsync(stream, metadata, _serializerOptions, ct).ConfigureAwait(false);
    }

    private async Task TrimSnapshotsAsync(string directory, string? protectedVersionId, CancellationToken ct)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            var snapshots = Directory
                .EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => Path.GetFileNameWithoutExtension(file), StringComparer.Ordinal)
                .ToList();

            if (snapshots.Count <= MaxSnapshotsPerDocument)
            {
                return;
            }

            var keep = new HashSet<string>(snapshots.Take(MaxSnapshotsPerDocument).Select(file => Path.GetFileNameWithoutExtension(file) ?? string.Empty), StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(protectedVersionId))
            {
                keep.Add(protectedVersionId);
            }

            foreach (var metadataPath in snapshots)
            {
                ct.ThrowIfCancellationRequested();

                var baseName = Path.GetFileNameWithoutExtension(metadataPath);
                if (baseName is null || keep.Contains(baseName))
                {
                    continue;
                }

                var binaryPath = Path.Combine(directory, baseName + ".bin");
                TryDeleteFile(binaryPath);
                TryDeleteFile(metadataPath);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to trim snapshots in {Directory}.", directory);
        }
    }

    private static void TryCreateDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        Directory.CreateDirectory(path);
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private sealed class SnapshotMetadata
    {
        [JsonPropertyName("descriptor")]
        public VersionDescriptor? Descriptor { get; set; }

        [JsonPropertyName("originalFileName")]
        public string? OriginalFileName { get; set; }
    }
}
