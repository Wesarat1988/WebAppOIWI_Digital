using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WepAppOIWI_Digital.Services;

public sealed class DocumentCatalogOptions
{
    public string? RelativePath { get; set; }
    public string? AbsolutePath { get; set; }
    public string ManifestFileName { get; set; } = "index.json";
    public bool EnableNetworkConnection { get; set; } = true;
    public string? NetworkUserName { get; set; }
    public string? NetworkPassword { get; set; }
    public string? NetworkDomain { get; set; }
}

public sealed record DocumentRecord(
    string FileName,
    string DisplayName,
    string Line,
    string Station,
    string Model,
    string Machine,
    DateTimeOffset? UpdatedAt,
    string UploadedBy,
    string Comment,
    string DocumentType,
    int? SequenceNumber,
    string? ActiveVersionId,
    string? DocumentCode,
    int Version
)
{
    public string? LinkUrl { get; init; }
}

public sealed record OiwiRow(
    string FileName,
    string DisplayName,
    string Line,
    string Station,
    string Model,
    string Machine,
    DateTimeOffset? UpdatedAt,
    string UploadedBy,
    string Comment,
    string DocumentType,
    int? SequenceNumber,
    string? ActiveVersionId,
    string? DocumentCode,
    int Version,
    string? LinkUrl
);

public readonly record struct OiwiSearchFilters(
    string? Keyword,
    string? DocumentType,
    string? Line,
    string? Station,
    string? Model,
    string? Uploader
);

public sealed class DocumentCatalogService : IDisposable
{
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<DocumentCatalogService> _logger;
    private readonly DocumentCatalogOptions _options;
    private readonly string? _configuredAbsolutePath;
    private readonly RelativeDirectorySettings _relativeSettings;
    private readonly NetworkShareConnector? _networkConnector;
    private readonly object _networkConnectionLock = new();
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
    private readonly FileExtensionContentTypeProvider _contentTypeProvider = new();

    private IReadOnlyList<DocumentRecord>? _cachedDocuments;
    private DateTime _lastCacheTimeUtc;
    private readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(5);
    private readonly object _cacheLock = new();
    private static readonly int[] AllowedPageSizes = new[] { 10, 20, 50, 100 };

    private DocumentCatalogContext _currentContext = DocumentCatalogContext.Uninitialized;
    private string? _connectionError;

    public DocumentCatalogService(
        IWebHostEnvironment environment,
        IOptions<DocumentCatalogOptions> options,
        ILogger<DocumentCatalogService> logger)
    {
        _environment = environment;
        _logger = logger;
        _options = options.Value;

        _configuredAbsolutePath = ResolveAbsoluteDirectory();
        _relativeSettings = ResolveRelativeDirectory();
        _networkConnector = NetworkShareConnector.TryCreate(_configuredAbsolutePath, _options, logger);
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

    public async Task<PagedResult<OiwiRow>> GetOiwiPageAsync(
        int page,
        int pageSize,
        string? search,
        string? sortColumn,
        bool sortDesc,
        CancellationToken cancellationToken = default)
    {
        var sanitizedPage = Math.Max(1, page);
        var sanitizedPageSize = SanitizePageSize(pageSize);

        var records = await GetDocumentsAsync(cancellationToken).ConfigureAwait(false);
        var filters = ParseOiwiSearchQuery(search);

        var query = ApplyFilters(records, filters);
        var ordered = ApplySort(query, sortColumn, sortDesc).ToList();

        var totalCount = ordered.Count;
        var skip = (sanitizedPage - 1) * sanitizedPageSize;
        if (skip < 0)
        {
            skip = 0;
        }

        var pageItems = ordered
            .Skip(skip)
            .Take(sanitizedPageSize)
            .Select(ToOiwiRow)
            .ToList();

        return new PagedResult<OiwiRow>(pageItems, totalCount, sanitizedPage, sanitizedPageSize);
    }

    private static int SanitizePageSize(int value)
    {
        if (Array.IndexOf(AllowedPageSizes, value) >= 0)
        {
            return value;
        }

        if (value < AllowedPageSizes[0])
        {
            return AllowedPageSizes[0];
        }

        if (value > AllowedPageSizes[^1])
        {
            return AllowedPageSizes[^1];
        }

        return Array.IndexOf(AllowedPageSizes, 20) >= 0 ? 20 : AllowedPageSizes[0];
    }

    private static IEnumerable<DocumentRecord> ApplyFilters(
        IEnumerable<DocumentRecord> records,
        OiwiSearchFilters filters)
    {
        var query = records;

        if (!string.IsNullOrWhiteSpace(filters.DocumentType))
        {
            var candidate = NormalizeForComparison(filters.DocumentType!);
            if (!string.IsNullOrEmpty(candidate))
            {
                query = query.Where(r => string.Equals(
                    NormalizeForComparison(r.DocumentType),
                    candidate,
                    StringComparison.OrdinalIgnoreCase));
            }
        }

        if (!string.IsNullOrWhiteSpace(filters.Line))
        {
            var candidate = NormalizeForComparison(filters.Line!);
            if (!string.IsNullOrEmpty(candidate))
            {
                query = query.Where(r => string.Equals(
                    NormalizeForComparison(r.Line),
                    candidate,
                    StringComparison.OrdinalIgnoreCase));
            }
        }

        if (!string.IsNullOrWhiteSpace(filters.Station))
        {
            var candidate = NormalizeForComparison(filters.Station!);
            if (!string.IsNullOrEmpty(candidate))
            {
                query = query.Where(r => string.Equals(
                    NormalizeForComparison(r.Station),
                    candidate,
                    StringComparison.OrdinalIgnoreCase));
            }
        }

        if (!string.IsNullOrWhiteSpace(filters.Model))
        {
            var candidate = NormalizeForComparison(filters.Model!);
            if (!string.IsNullOrEmpty(candidate))
            {
                query = query.Where(r => string.Equals(
                    NormalizeForComparison(r.Model),
                    candidate,
                    StringComparison.OrdinalIgnoreCase));
            }
        }

        if (!string.IsNullOrWhiteSpace(filters.Uploader))
        {
            var candidate = NormalizeForComparison(filters.Uploader!);
            if (!string.IsNullOrEmpty(candidate))
            {
                query = query.Where(r => string.Equals(
                    NormalizeForComparison(r.UploadedBy),
                    candidate,
                    StringComparison.OrdinalIgnoreCase));
            }
        }

        if (!string.IsNullOrWhiteSpace(filters.Keyword))
        {
            var keyword = filters.Keyword!.Trim();
            if (!string.IsNullOrEmpty(keyword))
            {
                query = query.Where(r => MatchesKeyword(r, keyword));
            }
        }

        return query;
    }

    private static IEnumerable<DocumentRecord> ApplySort(
        IEnumerable<DocumentRecord> records,
        string? sortColumn,
        bool sortDesc)
    {
        static IEnumerable<DocumentRecord> OrderString(
            IEnumerable<DocumentRecord> source,
            Func<DocumentRecord, string?> selector,
            bool descending)
            => descending
                ? source.OrderByDescending(r => selector(r) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                : source.OrderBy(r => selector(r) ?? string.Empty, StringComparer.OrdinalIgnoreCase);

        static IEnumerable<DocumentRecord> OrderDate(
            IEnumerable<DocumentRecord> source,
            Func<DocumentRecord, DateTimeOffset?> selector,
            bool descending)
            => descending
                ? source.OrderByDescending(r => selector(r) ?? DateTimeOffset.MinValue)
                : source.OrderBy(r => selector(r) ?? DateTimeOffset.MinValue);

        var normalized = string.IsNullOrWhiteSpace(sortColumn)
            ? "time"
            : sortColumn.Trim().ToLowerInvariant();

        return normalized switch
        {
            "documentcode" => OrderString(records, r => r.DocumentCode, sortDesc),
            "displayname" => OrderString(records, r => r.DisplayName, sortDesc),
            "line" => OrderString(records, r => r.Line, sortDesc),
            "station" => OrderString(records, r => r.Station, sortDesc),
            "model" => OrderString(records, r => r.Model, sortDesc),
            "machine" => OrderString(records, r => r.Machine, sortDesc),
            "name" => OrderString(records, r => r.UploadedBy, sortDesc),
            "uploadedby" => OrderString(records, r => r.UploadedBy, sortDesc),
            "comment" => OrderString(records, r => r.Comment, sortDesc),
            _ => OrderDate(records, r => r.UpdatedAt, sortDesc)
        };
    }

    private static OiwiRow ToOiwiRow(DocumentRecord record)
        => new(
            record.FileName,
            record.DisplayName,
            record.Line,
            record.Station,
            record.Model,
            record.Machine,
            record.UpdatedAt,
            record.UploadedBy,
            record.Comment,
            record.DocumentType,
            record.SequenceNumber,
            record.ActiveVersionId,
            record.DocumentCode,
            record.Version,
            record.LinkUrl);

    private static string? NormalizeForComparison(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed == "-" ? null : trimmed;
    }

    private static bool MatchesKeyword(DocumentRecord record, string keyword)
        => ContainsKeyword(record.DisplayName, keyword)
            || ContainsKeyword(record.Line, keyword)
            || ContainsKeyword(record.Station, keyword)
            || ContainsKeyword(record.Model, keyword)
            || ContainsKeyword(record.Machine, keyword)
            || ContainsKeyword(record.UploadedBy, keyword)
            || ContainsKeyword(record.Comment, keyword)
            || ContainsKeyword(record.DocumentType, keyword)
            || ContainsKeyword(record.DocumentCode, keyword);

    private static bool ContainsKeyword(string? source, string keyword)
        => !string.IsNullOrWhiteSpace(source)
            && source.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;

    public async Task<DocumentRecord?> TryGetDocumentAsync(string? normalizedPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return null;
        }

        var comparisonPath = normalizedPath
            .Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Trim();

        var records = await GetDocumentsAsync(cancellationToken).ConfigureAwait(false);

        return records.FirstOrDefault(record =>
            string.Equals(
                record.FileName?.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                comparisonPath,
                StringComparison.OrdinalIgnoreCase));
    }

    public DocumentCatalogContext GetCatalogContext()
        => Volatile.Read(ref _currentContext);

    public async Task<DocumentCatalogContext> EnsureCatalogContextAsync(CancellationToken cancellationToken = default)
    {
        await GetDocumentsAsync(cancellationToken).ConfigureAwait(false);
        return GetCatalogContext();
    }

    public string ResolvePhysicalPath(string normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(normalizedPath));
        }

        var context = GetCatalogContext();
        var root = context.ActiveRootPath ?? throw new InvalidOperationException("Catalog root not set.");
        var relative = normalizedPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(root, relative));
    }

    public string GetDocumentRootPath(string documentCode)
    {
        if (string.IsNullOrWhiteSpace(documentCode))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(documentCode));
        }

        var context = GetCatalogContext();
        var root = context.ActiveRootPath ?? throw new InvalidOperationException("Catalog root not set.");
        var safeCode = Slugify(documentCode);
        return Path.Combine(root, safeCode);
    }

    public (string CurrentDirectory, string VersionsDirectory) GetDocumentDirectories(string documentCode)
    {
        var root = GetDocumentRootPath(documentCode);
        return (Path.Combine(root, "current"), Path.Combine(root, "versions"));
    }

    public void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _cachedDocuments = null;
            _lastCacheTimeUtc = DateTime.MinValue;
        }
    }

    public static string EncodeDocumentToken(string normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(normalizedPath));
        }

        var bytes = Encoding.UTF8.GetBytes(normalizedPath);
        return WebEncoders.Base64UrlEncode(bytes);
    }

    public static string BuildOiwiSearchQuery(OiwiSearchFilters filters)
    {
        static string? Normalize(string? value)
            => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        var builder = new List<string>();

        void Add(string key, string? value)
        {
            var normalized = Normalize(value);
            if (!string.IsNullOrEmpty(normalized))
            {
                builder.Add($"{key}={normalized}");
            }
        }

        Add("keyword", filters.Keyword);
        Add("type", filters.DocumentType);
        Add("line", filters.Line);
        Add("station", filters.Station);
        Add("model", filters.Model);
        Add("uploader", filters.Uploader);

        return string.Join('&', builder);
    }

    public static OiwiSearchFilters ParseOiwiSearchQuery(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return new OiwiSearchFilters(null, null, null, null, null, null);
        }

        var query = QueryHelpers.ParseQuery(search.StartsWith("?") ? search : "?" + search);

        string? Read(string key)
        {
            if (!query.TryGetValue(key, out var value))
            {
                return null;
            }

            var candidate = value.ToString();
            return string.IsNullOrWhiteSpace(candidate) ? null : candidate.Trim();
        }

        return new OiwiSearchFilters(
            Read("keyword"),
            Read("type"),
            Read("line"),
            Read("station"),
            Read("model"),
            Read("uploader"));
    }

    public static bool TryDecodeDocumentToken(string? token, out string normalizedPath)
    {
        normalizedPath = string.Empty;

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        try
        {
            var bytes = WebEncoders.Base64UrlDecode(token);
            var decoded = Encoding.UTF8.GetString(bytes);
            if (string.IsNullOrWhiteSpace(decoded))
            {
                return false;
            }

            normalizedPath = decoded
                .Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Trim();

            return !string.IsNullOrWhiteSpace(normalizedPath);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private async Task<IReadOnlyList<DocumentRecord>> LoadDocumentsAsync(CancellationToken cancellationToken)
    {
        EnsureNetworkShareConnection();

        var context = ResolveActiveContext();
        Volatile.Write(ref _currentContext, context);

        if (!context.RootExists)
        {
            if (!string.IsNullOrWhiteSpace(context.ActiveRootPath))
            {
                _logger.LogWarning("Document directory '{Directory}' does not exist.", context.ActiveRootPath);
            }
            else
            {
                _logger.LogWarning("Document directory is not configured.");
            }

            return Array.Empty<DocumentRecord>();
        }

        var manifestRecords = await TryLoadManifestAsync(context.ActiveRootPath!, cancellationToken).ConfigureAwait(false);
        var manifestFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var documents = new List<DocumentRecord>();

        if (manifestRecords is not null)
        {
            foreach (var entry in manifestRecords)
            {
                var normalizedRelativePath = NormalizeRelativePath(entry.FileName, context.ActiveRootPath!);
                if (string.IsNullOrEmpty(normalizedRelativePath))
                {
                    continue;
                }

                manifestFiles.Add(normalizedRelativePath);

                var fileSystemRelativePath = normalizedRelativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                var fullPath = Path.Combine(context.ActiveRootPath!, fileSystemRelativePath);
                var fileInfo = new FileInfo(fullPath);

                // ⬇️ ข้ามรายการ manifest ที่ถูกลบไฟล์จริงไปแล้ว
                if (!fileInfo.Exists)
                {
                    _logger.LogDebug("Skip manifest entry '{NormalizedPath}' because file not found on disk.", normalizedRelativePath);
                    continue;
                }

                documents.Add(CreateRecord(context, entry, fileInfo, normalizedRelativePath));
            }
        }

        foreach (var filePath in EnumerateDocumentFiles(context.ActiveRootPath!))
        {
            var normalizedRelativePath = NormalizeRelativePath(Path.GetRelativePath(context.ActiveRootPath!, filePath), context.ActiveRootPath!);
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
            documents.Add(CreateRecord(context, fileInfo, normalizedRelativePath));
        }

        return documents
            .OrderByDescending(d => d.UpdatedAt ?? DateTimeOffset.MinValue)
            .ThenBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Task<DocumentFileHandle?> TryGetDocumentFileAsync(string? requestedPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return Task.FromResult<DocumentFileHandle?>(null);
        }

        EnsureNetworkShareConnection();

        var context = ResolveActiveContext();
        Volatile.Write(ref _currentContext, context);

        if (!context.RootExists || string.IsNullOrWhiteSpace(context.ActiveRootPath))
        {
            return Task.FromResult<DocumentFileHandle?>(null);
        }

        var normalized = NormalizeRelativePath(requestedPath, context.ActiveRootPath!);
        if (string.IsNullOrEmpty(normalized))
        {
            return Task.FromResult<DocumentFileHandle?>(null);
        }

        var rootFullPath = Path.GetFullPath(context.ActiveRootPath!);
        var relativePath = normalized.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var combinedPath = Path.GetFullPath(Path.Combine(rootFullPath, relativePath));

        if (!IsPathWithinRoot(rootFullPath, combinedPath) || !File.Exists(combinedPath))
        {
            return Task.FromResult<DocumentFileHandle?>(null);
        }

        if (!_contentTypeProvider.TryGetContentType(combinedPath, out var contentType) || string.IsNullOrWhiteSpace(contentType))
        {
            contentType = "application/octet-stream";
        }

        var fileName = Path.GetFileName(combinedPath);

        return Task.FromResult<DocumentFileHandle?>(new DocumentFileHandle(
            NormalizedPath: normalized,
            PhysicalPath: combinedPath,
            FileName: fileName,
            ContentType: contentType));
    }

    private async Task<IReadOnlyList<DocumentManifestEntry>?> TryLoadManifestAsync(string rootDirectory, CancellationToken cancellationToken)
    {
        try
        {
            var manifestPath = Path.Combine(rootDirectory, _options.ManifestFileName);
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

    private DocumentRecord CreateRecord(DocumentCatalogContext context, DocumentManifestEntry entry, FileInfo fileInfo, string normalizedRelativePath)
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

        var normalizedDocumentType = DocumentNumbering.NormalizeType(entry.DocumentType);
        var displayDocumentType = string.IsNullOrEmpty(normalizedDocumentType)
            ? "-"
            : normalizedDocumentType;
        var sequenceNumber = entry.SequenceNumber;
        var documentCode = DocumentNumbering.FormatCode(normalizedDocumentType, sequenceNumber);

        var version = entry.Version.GetValueOrDefault();
        if (version <= 0)
        {
            version = 1;
        }

        return new DocumentRecord(
            normalizedRelativePath,
            string.IsNullOrWhiteSpace(displayName) ? fallbackDisplayName : displayName,
            NormalizeMetadata(entry.Line),
            NormalizeMetadata(entry.Station),
            NormalizeMetadata(entry.Model),
            NormalizeMetadata(entry.MachineName),
            updatedAt,
            NormalizeMetadata(entry.UploadedBy),
            NormalizeMetadata(entry.Comment),
            displayDocumentType,
            sequenceNumber,
            entry.ActiveVersionId,
            documentCode,
            version)
        {
            LinkUrl = BuildDocumentLink(context, normalizedRelativePath, fileInfo.FullName)
        };
    }

    private DocumentRecord CreateRecord(DocumentCatalogContext context, FileInfo fileInfo, string normalizedRelativePath)
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
            "-",
            updatedAt,
            "-",
            "-",
            "-",
            null,
            null,
            null,
            1
        )
        {
            LinkUrl = BuildDocumentLink(context, normalizedRelativePath, fileInfo.FullName)
        };
    }

    private string? BuildDocumentLink(DocumentCatalogContext context, string normalizedRelativePath, string fullPath)
    {
        if (context.LinkKind == CatalogLinkKind.Absolute)
        {
            if (Uri.TryCreate(fullPath, UriKind.Absolute, out var absoluteUri))
            {
                return absoluteUri.AbsoluteUri;
            }

            return null;
        }

        var relativePrefix = (context.RelativeRequestPrefix ?? string.Empty)
            .Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var segments = normalizedRelativePath.Split(Path.AltDirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        var encoded = string.Join(Path.AltDirectorySeparatorChar, segments.Select(Uri.EscapeDataString));

        if (string.IsNullOrEmpty(relativePrefix))
        {
            return $"/{encoded}";
        }

        return $"/{relativePrefix}/{encoded}";
    }

    private IEnumerable<string> EnumerateDocumentFiles(string rootDirectory)
    {
        try
        {
            var results = new List<string>();
            var stack = new Stack<string>();
            stack.Push(rootDirectory);

            while (stack.Count > 0)
            {
                var current = stack.Pop();

                IEnumerable<string> directories;
                try
                {
                    directories = Directory.EnumerateDirectories(current);
                }
                catch
                {
                    continue;
                }

                foreach (var directory in directories)
                {
                    var name = Path.GetFileName(directory);
                    if (string.Equals(name, "versions", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    stack.Push(directory);
                }

                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(current, "*", SearchOption.TopDirectoryOnly);
                }
                catch
                {
                    continue;
                }

                foreach (var file in files)
                {
                    var fileName = Path.GetFileName(file);
                    if (string.Equals(fileName, "meta.json", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    results.Add(file);
                }
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enumerate document files under '{Directory}'.", rootDirectory);
            return Array.Empty<string>();
        }
    }

    private string? NormalizeRelativePath(string? path, string rootDirectory)
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
                candidate = Path.GetRelativePath(rootDirectory, candidate);
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

    private static bool IsPathWithinRoot(string rootPath, string candidatePath)
    {
        if (string.IsNullOrEmpty(rootPath) || string.IsNullOrEmpty(candidatePath))
        {
            return false;
        }

        var normalizedRoot = EnsureTrailingSeparator(Path.GetFullPath(rootPath));
        var normalizedCandidate = Path.GetFullPath(candidatePath);

        return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string path)
        => path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;

    private static string NormalizeMetadata(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }

        var trimmed = value.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? "-" : trimmed;
    }

    public static string Slugify(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);

        foreach (var ch in value.Trim())
        {
            if (invalid.Contains(ch))
            {
                continue;
            }

            builder.Append(char.IsWhiteSpace(ch) ? '_' : ch);
        }

        var slug = builder.ToString().Trim('_');

        while (slug.Contains("__", StringComparison.Ordinal))
        {
            slug = slug.Replace("__", "_", StringComparison.Ordinal);
        }

        return slug;
    }

    private DocumentCatalogContext ResolveActiveContext()
    {
        var absolutePath = _configuredAbsolutePath;
        var relativePhysical = _relativeSettings.PhysicalPath;
        var relativeExists = Directory.Exists(relativePhysical);

        if (!string.IsNullOrWhiteSpace(absolutePath))
        {
            var absoluteExists = Directory.Exists(absolutePath);

            if (absoluteExists)
            {
                return new DocumentCatalogContext(
                    ActiveRootPath: absolutePath,
                    RootExists: true,
                    LinkKind: CatalogLinkKind.Absolute,
                    IsFallback: false,
                    RequestedAbsolutePath: absolutePath,
                    RelativeRequestPrefix: _relativeSettings.RequestPrefix,
                    RelativePhysicalPath: relativePhysical,
                    ConnectionErrorMessage: _connectionError);
            }

            if (relativeExists)
            {
                return new DocumentCatalogContext(
                    ActiveRootPath: relativePhysical,
                    RootExists: true,
                    LinkKind: CatalogLinkKind.Relative,
                    IsFallback: true,
                    RequestedAbsolutePath: absolutePath,
                    RelativeRequestPrefix: _relativeSettings.RequestPrefix,
                    RelativePhysicalPath: relativePhysical,
                    ConnectionErrorMessage: _connectionError);
            }

            return new DocumentCatalogContext(
                ActiveRootPath: absolutePath,
                RootExists: false,
                LinkKind: CatalogLinkKind.Absolute,
                IsFallback: false,
                RequestedAbsolutePath: absolutePath,
                RelativeRequestPrefix: _relativeSettings.RequestPrefix,
                RelativePhysicalPath: relativePhysical,
                ConnectionErrorMessage: _connectionError);
        }

        return new DocumentCatalogContext(
            ActiveRootPath: relativePhysical,
            RootExists: relativeExists,
            LinkKind: CatalogLinkKind.Relative,
            IsFallback: false,
            RequestedAbsolutePath: null,
            RelativeRequestPrefix: _relativeSettings.RequestPrefix,
            RelativePhysicalPath: relativePhysical,
            ConnectionErrorMessage: _connectionError);
    }

    private string? ResolveAbsoluteDirectory()
    {
        var absolute = _options.AbsolutePath;
        if (string.IsNullOrWhiteSpace(absolute))
        {
            return null;
        }

        var trimmed = absolute.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (Path.IsPathRooted(trimmed))
        {
            return trimmed;
        }

        var contentRoot = _environment.ContentRootPath ?? AppContext.BaseDirectory;
        return Path.Combine(contentRoot, trimmed);
    }

    private RelativeDirectorySettings ResolveRelativeDirectory()
    {
        var basePath = _environment.WebRootPath ?? _environment.ContentRootPath ?? AppContext.BaseDirectory;
        var relative = string.IsNullOrWhiteSpace(_options.RelativePath)
            ? DefaultRelativePath
            : _options.RelativePath!;

        var trimmed = relative.Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ' ');
        var physical = string.IsNullOrEmpty(trimmed)
            ? basePath
            : Path.Combine(basePath, trimmed.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));

        var requestPrefix = trimmed.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return new RelativeDirectorySettings(physical, requestPrefix);
    }

    private const string DefaultRelativePath = "oiwi-documents";

    private void EnsureNetworkShareConnection()
    {
        if (!_options.EnableNetworkConnection)
        {
            _connectionError = null;
            return;
        }

        if (_networkConnector is null)
        {
            _connectionError = null;
            return;
        }

        lock (_networkConnectionLock)
        {
            if (_networkConnector.EnsureConnected())
            {
                _connectionError = null;
            }
            else
            {
                _connectionError = _networkConnector.LastErrorMessage;
            }
        }
    }

    public enum CatalogLinkKind
    {
        Relative,
        Absolute
    }

    public sealed record DocumentCatalogContext(
        string? ActiveRootPath,
        bool RootExists,
        CatalogLinkKind LinkKind,
        bool IsFallback,
        string? RequestedAbsolutePath,
        string? RelativeRequestPrefix,
        string? RelativePhysicalPath,
        string? ConnectionErrorMessage)
    {
        public static DocumentCatalogContext Uninitialized { get; } = new("", false, CatalogLinkKind.Relative, false, null, null, null, null);
    }

    public sealed record DocumentFileHandle(
        string NormalizedPath,
        string PhysicalPath,
        string FileName,
        string ContentType);

    private sealed record RelativeDirectorySettings(string PhysicalPath, string RequestPrefix);

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
        public string? MachineName { get; init; }
        public string? UploadedBy { get; init; }
        public string? Comment { get; init; }
        public DateTimeOffset? UpdatedAt { get; init; }
        public string? DocumentType { get; init; }
        public int? SequenceNumber { get; init; }
        public int? Version { get; init; }
        public string? ActiveVersionId { get; init; }
    }

    public void Dispose()
    {
        _networkConnector?.Dispose();
    }
}
