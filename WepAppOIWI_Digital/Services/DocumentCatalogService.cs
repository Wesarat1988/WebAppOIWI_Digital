using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text;
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
    string? DocumentCode
)
{
    public string? LinkUrl { get; init; }
}

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
    private readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(1);
    private readonly object _cacheLock = new();

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
            documentCode)
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
            null
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
            return Directory.EnumerateFiles(rootDirectory, "*", SearchOption.AllDirectories)
                .ToList();
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
    }

    public void Dispose()
    {
        _networkConnector?.Dispose();
    }
}
