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
    public string RelativeUrl { get; init; } = string.Empty;
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
                var relativeFileName = entry.FileName ?? string.Empty;
                if (string.IsNullOrWhiteSpace(relativeFileName))
                {
                    continue;
                }

                manifestFiles.Add(Path.GetFileName(relativeFileName));

                var fullPath = Path.Combine(_documentsDirectory, relativeFileName);
                var fileInfo = new FileInfo(fullPath);

                documents.Add(CreateRecord(entry, fileInfo));
            }
        }

        foreach (var filePath in Directory.GetFiles(_documentsDirectory))
        {
            var fileName = Path.GetFileName(filePath);
            if (string.Equals(fileName, _options.ManifestFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (manifestFiles.Contains(fileName))
            {
                continue;
            }

            var fileInfo = new FileInfo(filePath);
            documents.Add(new DocumentRecord(
                fileInfo.Name,
                fileInfo.Name,
                "-",
                "-",
                "-",
                new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero),
                "-"
            )
            {
                RelativeUrl = BuildRelativeUrl(fileInfo.Name)
            });
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

    private DocumentRecord CreateRecord(DocumentManifestEntry entry, FileInfo fileInfo)
    {
        var updatedAt = entry.UpdatedAt;

        if (updatedAt is null && fileInfo.Exists)
        {
            updatedAt = new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero);
        }

        var fileName = entry.FileName ?? fileInfo.Name;
        var displayName = string.IsNullOrWhiteSpace(entry.DisplayName)
            ? Path.GetFileName(fileName)
            : entry.DisplayName!;

        return new DocumentRecord(
            fileName,
            displayName ?? fileName,
            string.IsNullOrWhiteSpace(entry.Line) ? "-" : entry.Line!,
            string.IsNullOrWhiteSpace(entry.Station) ? "-" : entry.Station!,
            string.IsNullOrWhiteSpace(entry.Model) ? "-" : entry.Model!,
            updatedAt,
            string.IsNullOrWhiteSpace(entry.UploadedBy) ? "-" : entry.UploadedBy!)
        {
            RelativeUrl = BuildRelativeUrl(fileName)
        };
    }

    private string BuildRelativeUrl(string fileName)
    {
        var relativePath = string.IsNullOrWhiteSpace(_options.RelativePath)
            ? DefaultRelativePath
            : _options.RelativePath!;

        var sanitizedRelative = relativePath.Trim('/').Replace("\\", "/");
        var normalizedFileName = fileName.Replace("\\", "/");
        var segments = normalizedFileName.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var encoded = string.Join('/', segments.Select(Uri.EscapeDataString));

        if (string.IsNullOrEmpty(sanitizedRelative))
        {
            return $"/{encoded}";
        }

        return $"/{sanitizedRelative}/{encoded}";
    }

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
