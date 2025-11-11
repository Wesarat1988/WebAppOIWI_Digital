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

                    var versionFile = ResolveVersionFilePath(metadataPath, metadata);
                    var descriptor = metadata.Descriptor with
                    {
                        SizeBytes = TryGetFileLength(versionFile),
                        PublicUrl = null
                    };

                    descriptors.Add(descriptor);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read snapshot metadata at {MetadataPath}.", metadataPath);
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
            _logger.LogError(ex, "Failed to list snapshots for {NormalizedPath}.", normalizedPath);
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
                var fallbackMetadata = Directory
                    .EnumerateFiles(versionDirectory, versionId + "*.json", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault();
                if (!string.IsNullOrEmpty(fallbackMetadata))
                {
                    metadataPath = fallbackMetadata;
                    metadata = await ReadMetadataAsync(metadataPath, ct).ConfigureAwait(false);
                }
            }
            else
            {
                var fallbackMetadata = Directory
                    .EnumerateFiles(versionDirectory, versionId + "*.json", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault();
                if (!string.IsNullOrEmpty(fallbackMetadata))
                {
                    metadataPath = fallbackMetadata;
                    metadata = await ReadMetadataAsync(metadataPath, ct).ConfigureAwait(false);
                }
            }

            var binaryPath = ResolveVersionFilePath(metadataPath, metadata) ??
                Directory.EnumerateFiles(versionDirectory, versionId + ".*", SearchOption.TopDirectoryOnly).FirstOrDefault();

            if (string.IsNullOrEmpty(binaryPath) || !File.Exists(binaryPath))
            {
                _logger.LogWarning("Snapshot {VersionId} for {NormalizedPath} not found.", versionId, normalizedPath);
                return false;
            }

            if (File.Exists(physicalPath))
            {
                var snapshotComment = string.IsNullOrWhiteSpace(comment)
                    ? $"Snapshot before restore to {versionId}"
                    : $"{comment} (before restore)";

            var versionFile = ResolveVersionFilePath(metadataPath, metadata) ??
                Directory.EnumerateFiles(versionDirectory, versionId + ".*", SearchOption.TopDirectoryOnly).FirstOrDefault();

            if (string.IsNullOrEmpty(versionFile) || !File.Exists(versionFile))
            {
                return null;
            }

            var descriptor = metadata?.Descriptor;
            if (descriptor is null)
            {
                var timestamp = new DateTimeOffset(File.GetLastWriteTimeUtc(versionFile), TimeSpan.Zero);
                descriptor = new VersionDescriptor(
                    Path.GetFileNameWithoutExtension(versionFile),
                    timestamp,
                    actor: null,
                    comment: null,
                    SizeBytes: null,
                    PublicUrl: null);
            }

            descriptor = descriptor with
            {
                SizeBytes = TryGetFileLength(versionFile)
            };

            return new VersionSnapshotHandle(
                descriptor,
                versionFile,
                Path.GetFileName(versionFile));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve snapshot {VersionId} for {NormalizedPath}.", versionId, normalizedPath);
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
            if (string.IsNullOrEmpty(versionDirectory))
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
                var fallbackMetadata = Directory
                    .EnumerateFiles(versionDirectory, versionId + "*.json", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault();
                if (!string.IsNullOrEmpty(fallbackMetadata))
                {
                    metadataPath = fallbackMetadata;
                    metadata = await ReadMetadataAsync(metadataPath, ct).ConfigureAwait(false);
                }
            }

            var binaryPath = ResolveVersionFilePath(metadataPath, metadata) ??
                Directory.EnumerateFiles(versionDirectory, versionId + ".*", SearchOption.TopDirectoryOnly).FirstOrDefault();

            if (string.IsNullOrEmpty(binaryPath) || !File.Exists(binaryPath))
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
    }

        string? versionFilePath = null;
        string? metadataPath = null;

        try
        {
            var timestamp = DateTimeOffset.UtcNow;
            var versionDirectory = await GetVersionDirectoryAsync(normalizedPath, ensureExists: true, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(versionDirectory))
            {
                return false;
            }

            var actorSlug = DocumentCatalogService.Slugify(actor);
            if (string.IsNullOrWhiteSpace(actorSlug))
            {
                actorSlug = "system";
            }

            var versionNumber = await ResolveCurrentVersionAsync(normalizedPath, ct).ConfigureAwait(false);
            var extension = Path.GetExtension(physicalPath);
            var versionFileName = CreateVersionFileName(timestamp, actorSlug, versionNumber, extension);
            versionFilePath = Path.Combine(versionDirectory, versionFileName);

            var counter = 1;
            while (File.Exists(versionFilePath))
            {
                var candidateName = CreateVersionFileName(timestamp, actorSlug, versionNumber, extension, counter++);
                versionFilePath = Path.Combine(versionDirectory, candidateName);
            }

            metadataPath = Path.Combine(versionDirectory, Path.GetFileNameWithoutExtension(versionFilePath) + ".json");

            TryCreateDirectory(versionDirectory);
            File.Copy(physicalPath, versionFilePath, overwrite: false);

            var descriptor = new VersionDescriptor(
                Path.GetFileNameWithoutExtension(versionFilePath),
                timestamp,
                string.IsNullOrWhiteSpace(actor) ? null : actor,
                comment,
                TryGetFileLength(versionFilePath),
                PublicUrl: null);

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
            _logger.LogError(ex, "Failed to create snapshot for {NormalizedPath}.", normalizedPath);
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
            _logger.LogWarning("Catalog root is not configured. Skipping version storage for {NormalizedPath}.", normalizedPath);
            return null;
        }

        var documentCode = ExtractDocumentCode(normalizedPath);
        if (string.IsNullOrWhiteSpace(documentCode))
        {
            _logger.LogWarning("Cannot determine document code for {NormalizedPath}.", normalizedPath);
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
            _logger.LogError(ex, "Failed to resolve version directory for {NormalizedPath}.", normalizedPath);
            return null;
        }
    }

    private static string CreateVersionFileName(DateTimeOffset timestamp, string actorSlug, int versionNumber, string? extension, int? suffix = null)
    {
        var safeExtension = string.IsNullOrWhiteSpace(extension) ? string.Empty : extension;
        var suffixPart = suffix is null ? string.Empty : $"_{suffix.Value}";
        return $"{timestamp:yyyyMMdd_HHmmss}__{actorSlug}__v{versionNumber}{suffixPart}{safeExtension}";
    }

    private static string? ResolveVersionFilePath(string? metadataPath, SnapshotMetadata? metadata)
    {
        try
        {
            var directory = string.IsNullOrEmpty(metadataPath) ? null : Path.GetDirectoryName(metadataPath);
            if (string.IsNullOrEmpty(directory))
            {
                return null;
            }

            if (!string.IsNullOrEmpty(metadata?.StoredFileName))
            {
                var candidate = Path.Combine(directory, metadata.StoredFileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            var versionId = metadata?.Descriptor?.VersionId;
            if (!string.IsNullOrEmpty(versionId))
            {
                return Directory.EnumerateFiles(directory, versionId + ".*", SearchOption.TopDirectoryOnly).FirstOrDefault();
            }

            return null;
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
            _logger.LogDebug(ex, "Failed to resolve document version for {Path} while snapshotting.", normalizedPath);
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
                .OrderByDescending(file => Path.GetFileNameWithoutExtension(file), StringComparer.Ordinal)
                .ToList();

            if (metadataPaths.Count <= MaxSnapshotsPerDocument)
            {
                return;
            }

            var keep = new HashSet<string>(StringComparer.Ordinal);

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
                    _logger.LogWarning(ex, "Failed to read snapshot metadata while determining retention for {Path}.", path);
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
                    var versionKey = metadata?.Descriptor?.VersionId ?? Path.GetFileNameWithoutExtension(metadataPath);
                    if (string.IsNullOrEmpty(versionKey) || keep.Contains(versionKey))
                    {
                        continue;
                    }

                    var versionFile = ResolveVersionFilePath(metadataPath, metadata);
                    TryDeleteFile(versionFile);
                    TryDeleteFile(metadataPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to trim snapshot metadata at {Path}.", metadataPath);
                }
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

        try
        {
            Directory.CreateDirectory(path);
        }
        catch
        {
            // Best-effort.
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
            // Best-effort cleanup.
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

        [JsonPropertyName("storedFileName")]
        public string? StoredFileName { get; set; }
    }
}
