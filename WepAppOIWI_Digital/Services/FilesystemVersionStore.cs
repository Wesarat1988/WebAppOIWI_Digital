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

    private readonly DocumentCatalogService _catalogService;
    private readonly ILogger<FilesystemVersionStore> _logger;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
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
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                .ToList();

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

                    var versionId = metadata.Descriptor.VersionId;
                    var binaryPath = ResolveVersionFilePath(versionDirectory, metadata, versionId);
                    var descriptor = metadata.Descriptor with
                    {
                        SizeBytes = TryGetFileLength(binaryPath),
                        PublicUrl = null
                    };

                    descriptors.Add(descriptor);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read version metadata at {MetadataPath}.", metadataPath);
                }
            }

            return descriptors
                .OrderByDescending(d => d.TimestampUtc)
                .ThenByDescending(d => d.VersionId, StringComparer.Ordinal)
                .Take(take)
                .ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list version history for {Path}.", normalizedPath);
            return Array.Empty<VersionDescriptor>();
        }
    }

    public async Task<VersionSnapshotHandle?> TryGetAsync(string normalizedPath, string versionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(versionId))
        {
            return null;
        }

        try
        {
            var versionDirectory = await GetVersionDirectoryAsync(normalizedPath, ensureExists: false, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(versionDirectory) || !Directory.Exists(versionDirectory))
            {
                return null;
            }

            var metadataPath = Path.Combine(versionDirectory, versionId + ".json");
            SnapshotMetadata? metadata = null;

            if (File.Exists(metadataPath))
            {
                metadata = await ReadMetadataAsync(metadataPath, ct).ConfigureAwait(false);
            }
            else
            {
                metadataPath = Directory
                    .EnumerateFiles(versionDirectory, versionId + "*.json", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault() ?? metadataPath;

                if (File.Exists(metadataPath))
                {
                    metadata = await ReadMetadataAsync(metadataPath, ct).ConfigureAwait(false);
                }
            }

            var binaryPath = ResolveVersionFilePath(versionDirectory, metadata, versionId);
            if (string.IsNullOrEmpty(binaryPath) || !File.Exists(binaryPath))
            {
                return null;
            }

            var descriptor = metadata?.Descriptor ?? new VersionDescriptor(
                versionId,
                new DateTimeOffset(File.GetLastWriteTimeUtc(binaryPath), TimeSpan.Zero),
                Actor: null,
                Comment: null,
                SizeBytes: null,
                PublicUrl: null,
                IsActive: false);

            descriptor = descriptor with { SizeBytes = TryGetFileLength(binaryPath) };

            return new VersionSnapshotHandle(descriptor, binaryPath, Path.GetFileName(binaryPath));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve version {VersionId} for {Path}.", versionId, normalizedPath);
            return null;
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
            if (string.IsNullOrEmpty(versionDirectory) || !Directory.Exists(versionDirectory))
            {
                return false;
            }

            var metadataPath = Path.Combine(versionDirectory, versionId + ".json");
            SnapshotMetadata? metadata = null;

            if (File.Exists(metadataPath))
            {
                metadata = await ReadMetadataAsync(metadataPath, ct).ConfigureAwait(false);
            }
            else
            {
                metadataPath = Directory
                    .EnumerateFiles(versionDirectory, versionId + "*.json", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault() ?? metadataPath;

                if (File.Exists(metadataPath))
                {
                    metadata = await ReadMetadataAsync(metadataPath, ct).ConfigureAwait(false);
                }
            }

            var binaryPath = ResolveVersionFilePath(versionDirectory, metadata, versionId);
            if (string.IsNullOrEmpty(binaryPath) || !File.Exists(binaryPath))
            {
                _logger.LogWarning("Cannot restore {Path} because snapshot {VersionId} is missing.", normalizedPath, versionId);
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
            _logger.LogError(ex, "Failed to restore {Path} to version {VersionId}.", normalizedPath, versionId);

            if (!string.IsNullOrEmpty(backupPath) && File.Exists(backupPath))
            {
                try
                {
                    TryCreateDirectory(Path.GetDirectoryName(physicalPath));
                    File.Copy(backupPath, physicalPath, overwrite: true);
                }
                catch (Exception rollbackEx)
                {
                    _logger.LogError(rollbackEx, "Failed to restore backup after unsuccessful restore for {Path}.", normalizedPath);
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
            _logger.LogDebug("Skip snapshot for {Path} because source file does not exist.", physicalPath);
            return false;
        }

        string? versionFilePath = null;
        string? metadataPath = null;

        try
        {
            var versionDirectory = await GetVersionDirectoryAsync(normalizedPath, ensureExists: true, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(versionDirectory))
            {
                return false;
            }

            var timestamp = DateTimeOffset.UtcNow;
            var actorSlug = DocumentCatalogService.Slugify(actor);
            if (string.IsNullOrWhiteSpace(actorSlug))
            {
                actorSlug = "system";
            }

            var versionNumber = await ResolveCurrentVersionAsync(normalizedPath, ct).ConfigureAwait(false);
            var extension = Path.GetExtension(physicalPath);
            var fileName = CreateVersionFileName(timestamp, actorSlug, versionNumber, extension);
            versionFilePath = Path.Combine(versionDirectory, fileName);

            var counter = 1;
            while (File.Exists(versionFilePath))
            {
                var candidate = CreateVersionFileName(timestamp, actorSlug, versionNumber, extension, counter++);
                versionFilePath = Path.Combine(versionDirectory, candidate);
            }

            metadataPath = Path.ChangeExtension(versionFilePath, ".json");

            TryCreateDirectory(versionDirectory);
            File.Copy(physicalPath, versionFilePath, overwrite: false);

            var descriptor = new VersionDescriptor(
                Path.GetFileNameWithoutExtension(versionFilePath),
                timestamp,
                string.IsNullOrWhiteSpace(actor) ? null : actor,
                comment,
                TryGetFileLength(versionFilePath),
                PublicUrl: null,
                IsActive: false);

            var metadata = new SnapshotMetadata
            {
                Descriptor = descriptor,
                OriginalFileName = Path.GetFileName(physicalPath),
                StoredFileName = Path.GetFileName(versionFilePath)
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
            _logger.LogError(ex, "Failed to snapshot {Path}.", normalizedPath);

            if (!string.IsNullOrEmpty(versionFilePath))
            {
                TryDeleteFile(versionFilePath);
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
            _logger.LogWarning("Catalog root is not configured. Cannot store versions for {Path}.", normalizedPath);
            return null;
        }

        var documentCode = ExtractDocumentCode(normalizedPath);
        if (string.IsNullOrWhiteSpace(documentCode))
        {
            _logger.LogWarning("Cannot determine document code from {Path} while resolving version directory.", normalizedPath);
            return null;
        }

        try
        {
            var (_, versionsDirectory) = _catalogService.GetDocumentDirectories(documentCode);
            if (ensureExists)
            {
                Directory.CreateDirectory(versionsDirectory);
            }

            return versionsDirectory;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve version directory for {Path}.", normalizedPath);
            return null;
        }
    }

    private static string CreateVersionFileName(DateTimeOffset timestamp, string actorSlug, int versionNumber, string? extension, int? suffix = null)
    {
        var safeExtension = string.IsNullOrWhiteSpace(extension) ? string.Empty : extension;
        var suffixPart = suffix is null ? string.Empty : $"_{suffix.Value}";
        return $"{timestamp:yyyyMMdd_HHmmss}__{actorSlug}__v{versionNumber}{suffixPart}{safeExtension}";
    }

    private static string? ResolveVersionFilePath(string versionDirectory, SnapshotMetadata? metadata, string versionId)
    {
        try
        {
            if (!string.IsNullOrEmpty(metadata?.StoredFileName))
            {
                var candidate = Path.Combine(versionDirectory, metadata.StoredFileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return Directory
                .EnumerateFiles(versionDirectory, versionId + ".*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private async Task<int> ResolveCurrentVersionAsync(string normalizedPath, CancellationToken ct)
    {
        try
        {
            var record = await _catalogService.TryGetDocumentAsync(normalizedPath, ct).ConfigureAwait(false);
            if (record?.Version > 0)
            {
                return record.Version;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve current version number for {Path}.", normalizedPath);
        }

        return 1;
    }

    private static string? ExtractDocumentCode(string normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return null;
        }

        var sanitized = normalizedPath.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var segments = sanitized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 ? segments[0] : null;
    }

    private static async Task CopyFileAsync(string sourcePath, string destinationPath, CancellationToken ct)
    {
        const int bufferSize = 81920;
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true);
        await using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true);
        await source.CopyToAsync(destination, bufferSize, ct).ConfigureAwait(false);
    }

    private static long? TryGetFileLength(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

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

            var metadataPaths = Directory
                .EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                .OrderByDescending(Path.GetFileNameWithoutExtension, StringComparer.Ordinal)
                .ToList();

            if (metadataPaths.Count <= MaxSnapshotsPerDocument)
            {
                return;
            }

            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in metadataPaths.Take(MaxSnapshotsPerDocument))
            {
                try
                {
                    var metadata = await ReadMetadataAsync(path, ct).ConfigureAwait(false);
                    if (metadata?.Descriptor?.VersionId is { } keepId)
                    {
                        keep.Add(keepId);
                    }
                    else
                    {
                        keep.Add(Path.GetFileNameWithoutExtension(path) ?? string.Empty);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read metadata while trimming {Path}.", path);
                    keep.Add(Path.GetFileNameWithoutExtension(path) ?? string.Empty);
                }
            }

            if (!string.IsNullOrEmpty(protectedVersionId))
            {
                keep.Add(protectedVersionId);
            }

            foreach (var metadataPath in metadataPaths)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var metadata = await ReadMetadataAsync(metadataPath, ct).ConfigureAwait(false);
                    var versionId = metadata?.Descriptor?.VersionId ?? Path.GetFileNameWithoutExtension(metadataPath);
                    if (string.IsNullOrEmpty(versionId) || keep.Contains(versionId))
                    {
                        continue;
                    }

                    var binaryPath = ResolveVersionFilePath(Path.GetDirectoryName(metadataPath)!, metadata, versionId);
                    TryDeleteFile(binaryPath);
                    TryDeleteFile(metadataPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete old snapshot at {Path}.", metadataPath);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to trim snapshots in directory {Directory}.", directory);
        }
    }

    private static void TryCreateDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(path);
        }
        catch
        {
            // best-effort
        }
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
            // best-effort cleanup
        }
    }

    private sealed class SnapshotMetadata
    {
        [JsonPropertyName("descriptor")]
        public VersionDescriptor? Descriptor { get; set; }

        [JsonPropertyName("originalFileName")]
        public string? OriginalFileName { get; set; }

        [JsonPropertyName("storedFileName")]
        public string? StoredFileName { get; set; }
    }
}
