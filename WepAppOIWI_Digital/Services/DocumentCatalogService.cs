using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WepAppOIWI_Digital.Data;

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
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IMemoryCache _memoryCache;
    private readonly ConcurrentDictionary<string, byte> _pageCacheKeys = new(StringComparer.Ordinal);
    private static readonly int[] AllowedPageSizes = new[] { 10, 20, 50, 100 };
    private static readonly MemoryCacheEntryOptions DocumentCacheOptions = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30)
    };
    private static readonly MemoryCacheEntryOptions PageCacheOptions = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30)
    };
    private const string DocumentsCacheKey = "catalog:documents";
    private const string LatestCachePrefix = "catalog:latest:";

    private DocumentCatalogContext _currentContext = DocumentCatalogContext.Uninitialized;
    private string? _connectionError;

    public DocumentCatalogService(
        IWebHostEnvironment environment,
        IOptions<DocumentCatalogOptions> options,
        ILogger<DocumentCatalogService> logger,
        IDbContextFactory<AppDbContext> dbContextFactory,
        IMemoryCache memoryCache)
    {
        _environment = environment;
        _logger = logger;
        _options = options.Value;
        _dbContextFactory = dbContextFactory;
        _memoryCache = memoryCache;

        _configuredAbsolutePath = ResolveAbsoluteDirectory();
        _relativeSettings = ResolveRelativeDirectory();
        _networkConnector = NetworkShareConnector.TryCreate(_configuredAbsolutePath, _options, logger);
    }

    public async Task<IReadOnlyList<DocumentRecord>> GetDocumentsAsync(CancellationToken cancellationToken = default)
    {
        if (_memoryCache.TryGetValue(DocumentsCacheKey, out IReadOnlyList<DocumentRecord>? cachedDocuments))
        {
            return cachedDocuments;
        }

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entities = await dbContext.Documents
            .AsNoTracking()
            .OrderByDescending(d => d.UpdatedAtUnixMs)
            .ThenByDescending(d => d.UpdatedAt ?? DateTimeOffset.MinValue)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var records = entities
            .Select(ToDocumentRecord)
            .ToList();

        _memoryCache.Set(DocumentsCacheKey, records, DocumentCacheOptions);

        return records;
    }

    public async Task<IReadOnlyList<DocumentRecord>> GetLatestDocumentsAsync(int take = 5, CancellationToken cancellationToken = default)
    {
        var sanitizedTake = Math.Clamp(take, 1, 50);
        var cacheKey = LatestCachePrefix + sanitizedTake.ToStringInvariant();

        if (_memoryCache.TryGetValue(cacheKey, out IReadOnlyList<DocumentRecord>? cachedDocuments))
        {
            return cachedDocuments;
        }

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entities = await dbContext.Documents
            .AsNoTracking()
            .OrderByDescending(entity => entity.UpdatedAtUnixMs)
            .ThenByDescending(entity => entity.UpdatedAt ?? DateTimeOffset.MinValue)
            .Take(sanitizedTake)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var records = entities
            .Select(ToDocumentRecord)
            .ToList();

        _memoryCache.Set(cacheKey, records, DocumentCacheOptions);
        _pageCacheKeys[cacheKey] = 0;

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
        var cacheKey = BuildPageCacheKey(sanitizedPage, sanitizedPageSize, search, sortColumn, sortDesc);

        if (_memoryCache.TryGetValue(cacheKey, out PagedResult<OiwiRow>? cachedPage))
        {
            return cachedPage;
        }

        var filters = ParseOiwiSearchQuery(search);

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var isSqliteProvider = IsSqliteProvider(dbContext);
        var query = dbContext.Documents.AsNoTracking();
        query = ApplyFilters(query, filters);
        query = ApplySort(query, sortColumn, sortDesc, isSqliteProvider);

        var totalCount = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var skip = (sanitizedPage - 1) * sanitizedPageSize;
        if (skip < 0)
        {
            skip = 0;
        }

        var items = await query
            .Skip(skip)
            .Take(sanitizedPageSize)
            .Select(static entity => new OiwiRow(
                entity.NormalizedPath,
                entity.DisplayName,
                entity.Line ?? "-",
                entity.Station ?? "-",
                entity.Model ?? "-",
                entity.Machine ?? "-",
                entity.UpdatedAt,
                entity.UploadedBy ?? "-",
                entity.Comment ?? "-",
                entity.DocumentType ?? "-",
                entity.SequenceNumber,
                entity.ActiveVersionId,
                entity.DocumentCode,
                entity.Version,
                entity.LinkUrl))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var result = new PagedResult<OiwiRow>(items, totalCount, sanitizedPage, sanitizedPageSize);
        _memoryCache.Set(cacheKey, result, PageCacheOptions);
        _pageCacheKeys[cacheKey] = 0;

        return result;
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

    private static IQueryable<DocumentEntity> ApplyFilters(
        IQueryable<DocumentEntity> query,
        OiwiSearchFilters filters)
    {
        if (!string.IsNullOrWhiteSpace(filters.DocumentType))
        {
            var documentType = filters.DocumentType.Trim();
            query = query.Where(entity => entity.DocumentType != null && entity.DocumentType == documentType);
        }

        if (!string.IsNullOrWhiteSpace(filters.Line))
        {
            var line = filters.Line.Trim();
            query = query.Where(entity => entity.Line != null && entity.Line == line);
        }

        if (!string.IsNullOrWhiteSpace(filters.Station))
        {
            var station = filters.Station.Trim();
            query = query.Where(entity => entity.Station != null && entity.Station == station);
        }

        if (!string.IsNullOrWhiteSpace(filters.Model))
        {
            var model = filters.Model.Trim();
            query = query.Where(entity => entity.Model != null && entity.Model == model);
        }

        if (!string.IsNullOrWhiteSpace(filters.Uploader))
        {
            var uploader = filters.Uploader.Trim();
            query = query.Where(entity => entity.UploadedBy != null && entity.UploadedBy == uploader);
        }

        if (!string.IsNullOrWhiteSpace(filters.Keyword))
        {
            var keyword = filters.Keyword.Trim();
            if (!string.IsNullOrEmpty(keyword))
            {
                var like = $"%{keyword}%";
                query = query.Where(entity =>
                    (entity.DisplayName != null && EF.Functions.Like(entity.DisplayName, like)) ||
                    (entity.DocumentCode != null && EF.Functions.Like(entity.DocumentCode, like)) ||
                    (entity.Line != null && EF.Functions.Like(entity.Line, like)) ||
                    (entity.Station != null && EF.Functions.Like(entity.Station, like)) ||
                    (entity.Model != null && EF.Functions.Like(entity.Model, like)) ||
                    (entity.Machine != null && EF.Functions.Like(entity.Machine, like)) ||
                    (entity.UploadedBy != null && EF.Functions.Like(entity.UploadedBy, like)) ||
                    (entity.Comment != null && EF.Functions.Like(entity.Comment, like)));
            }
        }

        return query;
    }

    private static IQueryable<DocumentEntity> ApplySort(
        IQueryable<DocumentEntity> query,
        string? sortColumn,
        bool sortDesc,
        bool useUnixTimestamp)
    {
        var normalized = string.IsNullOrWhiteSpace(sortColumn)
            ? "time"
            : sortColumn.Trim().ToLowerInvariant();

        return normalized switch
        {
            "documentcode" => sortDesc
                ? query.OrderByDescending(entity => entity.DocumentCode ?? string.Empty)
                : query.OrderBy(entity => entity.DocumentCode ?? string.Empty),
            "displayname" or "name" => sortDesc
                ? query.OrderByDescending(entity => entity.DisplayName)
                : query.OrderBy(entity => entity.DisplayName),
            "line" => sortDesc
                ? query.OrderByDescending(entity => entity.Line ?? string.Empty)
                : query.OrderBy(entity => entity.Line ?? string.Empty),
            "station" => sortDesc
                ? query.OrderByDescending(entity => entity.Station ?? string.Empty)
                : query.OrderBy(entity => entity.Station ?? string.Empty),
            "model" => sortDesc
                ? query.OrderByDescending(entity => entity.Model ?? string.Empty)
                : query.OrderBy(entity => entity.Model ?? string.Empty),
            "machine" => sortDesc
                ? query.OrderByDescending(entity => entity.Machine ?? string.Empty)
                : query.OrderBy(entity => entity.Machine ?? string.Empty),
            "uploadedby" => sortDesc
                ? query.OrderByDescending(entity => entity.UploadedBy ?? string.Empty)
                : query.OrderBy(entity => entity.UploadedBy ?? string.Empty),
            "comment" => sortDesc
                ? query.OrderByDescending(entity => entity.Comment ?? string.Empty)
                : query.OrderBy(entity => entity.Comment ?? string.Empty),
            _ when useUnixTimestamp => sortDesc
                ? query.OrderByDescending(entity => entity.UpdatedAtUnixMs)
                : query.OrderBy(entity => entity.UpdatedAtUnixMs),
            _ => sortDesc
                ? query.OrderByDescending(entity => entity.UpdatedAt ?? DateTimeOffset.MinValue)
                : query.OrderBy(entity => entity.UpdatedAt ?? DateTimeOffset.MinValue)
        };
    }

    private static string BuildPageCacheKey(int page, int pageSize, string? search, string? sortColumn, bool sortDesc)
        => $"catalog:page:{page}:{pageSize}:{search ?? string.Empty}:{sortColumn ?? string.Empty}:{sortDesc}";

    private static bool IsSqliteProvider(DbContext context)
        => context.Database.ProviderName?.IndexOf("Sqlite", StringComparison.OrdinalIgnoreCase) >= 0;

    private static DocumentRecord ToDocumentRecord(DocumentEntity entity)
        => new(
            entity.NormalizedPath,
            entity.DisplayName,
            entity.Line ?? "-",
            entity.Station ?? "-",
            entity.Model ?? "-",
            entity.Machine ?? "-",
            entity.UpdatedAt,
            entity.UploadedBy ?? "-",
            entity.Comment ?? "-",
            entity.DocumentType ?? "-",
            entity.SequenceNumber,
            entity.ActiveVersionId,
            entity.DocumentCode,
            entity.Version)
        {
            LinkUrl = entity.LinkUrl
        };

    public async Task<DocumentRecord?> TryGetDocumentAsync(string? normalizedPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return null;
        }

        var comparisonPath = normalizedPath
            .Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Trim();

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var entity = await dbContext.Documents
            .AsNoTracking()
            .FirstOrDefaultAsync(
                d => d.NormalizedPath == comparisonPath,
                cancellationToken)
            .ConfigureAwait(false);

        return entity is null ? null : ToDocumentRecord(entity);
    }

    public DocumentCatalogContext GetCatalogContext()
        => Volatile.Read(ref _currentContext);

    public Task<DocumentCatalogContext> EnsureCatalogContextAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        EnsureNetworkShareConnection();
        var context = ResolveActiveContext();
        Volatile.Write(ref _currentContext, context);
        return Task.FromResult(context);
    }

    public string ResolveDocumentPhysicalPath(string normalizedPath)
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

    public string GetDocumentRootDirectory(string documentCode)
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

    public (string CurrentDirectory, string VersionsDirectory) GetDocumentStorageDirectories(string documentCode)
    {
        var root = GetDocumentRootDirectory(documentCode);
        return (Path.Combine(root, "current"), Path.Combine(root, "versions"));
    }

    public void InvalidateCache()
    {
        _memoryCache.Remove(DocumentsCacheKey);

        foreach (var key in _pageCacheKeys.Keys)
        {
            _memoryCache.Remove(key);
            _pageCacheKeys.TryRemove(key, out _);
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

    internal async Task<IReadOnlyList<DocumentRecord>> LoadDocumentsFromSourceAsync(CancellationToken cancellationToken = default)
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
        var updatedAt = entry.UpdatedAt?.ToUniversalTime();

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

internal static class DocumentCatalogFormattingExtensions
{
    public static string ToStringInvariant(this int value)
        => value.ToString(CultureInfo.InvariantCulture);
}
