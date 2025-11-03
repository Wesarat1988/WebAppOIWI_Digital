using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WepAppOIWI_Digital.Services;

public sealed class DocumentCatalogOptions
{
    public string? RelativePath { get; set; }
    public string? AbsolutePath { get; set; }
    public string ManifestFileName { get; set; } = "index.json";
}

public sealed record DocumentRecord(
    string FileName,
    string DisplayName,
    string Line,
    string Station,
    string Model,
    DateTimeOffset? UpdatedAt,
    string UploadedBy
)
{
    public string? LinkUrl { get; init; }
}

public sealed class DocumentCatalogService
{
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<DocumentCatalogService> _logger;
    private readonly DocumentCatalogOptions _options;
    private readonly string _documentsDirectory;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private IReadOnlyList<DocumentRecord>? _cachedDocuments;
    private DateTime _lastCacheTimeUtc;
    private readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(1);
    private readonly object _cacheLock = new();

    public DocumentCatalogService(
        IWebHostEnvironment environment,
        IOptions<DocumentCatalogOptions> options,
        ILogger<DocumentCatalogService> logger)
    {
        _environment = environment;
        _logger = logger;
        _options = options.Value;

        _documentsDirectory = ResolveDocumentsDirectory();
    }

    public async Task<IReadOnlyList<DocumentRecord>> GetDocumentsAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedDocuments is not null && DateTime.UtcNow - _lastCacheTimeUtc < _cacheDuration)
        {
            return _cachedDocuments;
        }

        lock (_cacheLock)
        {
            if (_cachedDocuments is not null && DateTime.UtcNow - _lastCacheTimeUtc < _cacheDuration)
            {
                return _cachedDocuments;
            }
        }

        var records = await LoadDocumentsAsync(cancellationToken).ConfigureAwait(false);

        lock (_cacheLock)
        {
            _cachedDocuments = records;
            _lastCacheTimeUtc = DateTime.UtcNow;
        }

        return records;
    }

    private async Task<IReadOnlyList<DocumentRecord>> LoadDocumentsAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_documentsDirectory) || !Directory.Exists(_documentsDirectory))
        {
            _logger.LogInformation("Document directory '{Directory}' does not exist.", _documentsDirectory);
            return Array.Empty<DocumentRecord>();
        }

        var manifestRecords = await TryLoadManifestAsync(cancellationToken).ConfigureAwait(false);
        var manifestFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var documents = new List<DocumentRecord>();

        if (manifestRecords is not null)
        {
            foreach (var entry in manifestRecords)
            {
                var normalizedRelativePath = NormalizeRelativePath(entry.FileName);
                if (string.IsNullOrEmpty(normalizedRelativePath))
                {
                    continue;
                }

                manifestFiles.Add(normalizedRelativePath);

                var fileSystemRelativePath = normalizedRelativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                var fullPath = Path.Combine(_documentsDirectory, fileSystemRelativePath);
                var fileInfo = new FileInfo(fullPath);

                documents.Add(CreateRecord(entry, fileInfo, normalizedRelativePath));
            }
        }

        foreach (var filePath in EnumerateDocumentFiles())
        {
            var normalizedRelativePath = NormalizeRelativePath(Path.GetRelativePath(_documentsDirectory, filePath));
            if (string.IsNullOrEmpty(normalizedRelativePath))
            {
                continue;
            }

            if (string.Equals(normalizedRelativePath, _options.ManifestFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (manifestFiles.Contains(normalizedRelativePath))
            {
                continue;
            }

            var fileInfo = new FileInfo(filePath);
            documents.Add(CreateRecord(fileInfo, normalizedRelativePath));
        }

        return documents
            .OrderByDescending(d => d.UpdatedAt ?? DateTimeOffset.MinValue)
            .ThenBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlyList<DocumentManifestEntry>?> TryLoadManifestAsync(CancellationToken cancellationToken)
    {
        try
        {
            var manifestPath = Path.Combine(_documentsDirectory, _options.ManifestFileName);
            if (!File.Exists(manifestPath))
            {
                return null;
            }

            await using var stream = File.OpenRead(manifestPath);
            var manifest = await JsonSerializer.DeserializeAsync<DocumentManifest>(stream, _serializerOptions, cancellationToken)
                .ConfigureAwait(false);

            return manifest?.Documents ?? Array.Empty<DocumentManifestEntry>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load document manifest.");
            return null;
        }
    }

    private DocumentRecord CreateRecord(DocumentManifestEntry entry, FileInfo fileInfo, string normalizedRelativePath)
    {
        var updatedAt = entry.UpdatedAt;

        if (updatedAt is null && fileInfo.Exists)
        {
            updatedAt = new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero);
        }

        var fallbackDisplayName = Path.GetFileName(normalizedRelativePath);
        var displayName = string.IsNullOrWhiteSpace(entry.DisplayName)
            ? fallbackDisplayName
            : entry.DisplayName!;

        return new DocumentRecord(
            normalizedRelativePath,
            string.IsNullOrWhiteSpace(displayName) ? fallbackDisplayName : displayName,
            NormalizeMetadata(entry.Line),
            NormalizeMetadata(entry.Station),
            NormalizeMetadata(entry.Model),
            updatedAt,
            NormalizeMetadata(entry.UploadedBy))
        {
            LinkUrl = BuildDocumentLink(normalizedRelativePath, fileInfo.FullName)
        };
    }

    private DocumentRecord CreateRecord(FileInfo fileInfo, string normalizedRelativePath)
    {
        var updatedAt = fileInfo.Exists
            ? new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero)
            : (DateTimeOffset?)null;

        var displayName = Path.GetFileName(normalizedRelativePath);

        return new DocumentRecord(
            normalizedRelativePath,
            displayName,
            "-",
            "-",
            "-",
            updatedAt,
            "-"
        )
        {
            LinkUrl = BuildDocumentLink(normalizedRelativePath, fileInfo.FullName)
        };
    }

    private string? BuildDocumentLink(string normalizedRelativePath, string fullPath)
    {
        if (!string.IsNullOrWhiteSpace(_options.AbsolutePath))
        {
            if (Uri.TryCreate(fullPath, UriKind.Absolute, out var absoluteUri))
            {
                return absoluteUri.AbsoluteUri;
            }

            return null;
        }

        var relativePath = string.IsNullOrWhiteSpace(_options.RelativePath)
            ? DefaultRelativePath
            : _options.RelativePath!;

        var sanitizedRelative = relativePath.Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace(Path.DirectorySeparatorChar.ToString(), Path.AltDirectorySeparatorChar.ToString());
        var segments = normalizedRelativePath.Split(Path.AltDirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        var encoded = string.Join(Path.AltDirectorySeparatorChar, segments.Select(Uri.EscapeDataString));

        if (string.IsNullOrEmpty(sanitizedRelative))
        {
            return $"/{encoded}";
        }

        return $"/{sanitizedRelative}/{encoded}";
    }

    private IEnumerable<string> EnumerateDocumentFiles()
    {
        try
        {
            return Directory.EnumerateFiles(_documentsDirectory, "*", SearchOption.AllDirectories)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enumerate document files under '{Directory}'.", _documentsDirectory);
            return Array.Empty<string>();
        }
    }

    private string? NormalizeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var candidate = path.Trim();

        try
        {
            if (Path.IsPathRooted(candidate))
            {
                candidate = Path.GetRelativePath(_documentsDirectory, candidate);
            }
        }
        catch
        {
            return null;
        }

        candidate = candidate
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.IsNullOrWhiteSpace(candidate) || candidate.StartsWith("../", StringComparison.Ordinal) || candidate == "..")
        {
            return null;
        }

        return candidate;
    }

    private static string NormalizeMetadata(string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value!;

    private string ResolveDocumentsDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_options.AbsolutePath))
        {
            if (Path.IsPathRooted(_options.AbsolutePath))
            {
                return _options.AbsolutePath;
            }

            var contentRoot = _environment.ContentRootPath ?? AppContext.BaseDirectory;
            return Path.Combine(contentRoot, _options.AbsolutePath);
        }

        var basePath = _environment.WebRootPath ?? _environment.ContentRootPath ?? AppContext.BaseDirectory;
        var relative = string.IsNullOrWhiteSpace(_options.RelativePath)
            ? DefaultRelativePath
            : _options.RelativePath!;

        return Path.Combine(basePath, relative);
    }

    private const string DefaultRelativePath = "oiwi-documents";

    private sealed record DocumentManifest
    {
        public IReadOnlyList<DocumentManifestEntry> Documents { get; init; } = Array.Empty<DocumentManifestEntry>();
    }

    private sealed record DocumentManifestEntry
    {
        public string? FileName { get; init; }
        public string? DisplayName { get; init; }
        public string? Line { get; init; }
        public string? Station { get; init; }
        public string? Model { get; init; }
        public string? UploadedBy { get; init; }
        public DateTimeOffset? UpdatedAt { get; init; }
    }
}
