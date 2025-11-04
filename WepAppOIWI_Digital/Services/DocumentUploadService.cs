using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WepAppOIWI_Digital.Services;

public sealed class DocumentUploadService
{
    private readonly DocumentCatalogService _catalogService;
    private readonly DocumentCatalogOptions _options;
    private readonly ILogger<DocumentUploadService> _logger;
    private readonly SemaphoreSlim _uploadLock = new(1, 1);
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public DocumentUploadService(
        DocumentCatalogService catalogService,
        IOptions<DocumentCatalogOptions> options,
        ILogger<DocumentUploadService> logger)
    {
        _catalogService = catalogService;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<DocumentUploadResult> UploadAsync(DocumentUploadRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        await _uploadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var context = await _catalogService.EnsureCatalogContextAsync(cancellationToken).ConfigureAwait(false);
            var rootPath = context.ActiveRootPath;

            if (string.IsNullOrWhiteSpace(rootPath))
            {
                _logger.LogWarning("Cannot upload document because the catalog root path is not configured.");
                return DocumentUploadResult.Failed("ยังไม่ได้กำหนดโฟลเดอร์สำหรับเก็บเอกสาร OI/WI");
            }

            try
            {
                Directory.CreateDirectory(rootPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to ensure directory '{Directory}' exists before uploading document.", rootPath);
                return DocumentUploadResult.Failed("ไม่สามารถสร้างหรือเข้าถึงโฟลเดอร์ปลายทางได้");
            }

            var normalizedDocumentType = DocumentNumbering.NormalizeType(request.DocumentType);

            if (!DocumentNumbering.IsKnownType(normalizedDocumentType))
            {
                return DocumentUploadResult.Failed("กรุณาเลือกประเภทเอกสารให้ถูกต้อง (OI หรือ WI)");
            }

            if (string.Equals(normalizedDocumentType, DocumentNumbering.DocumentTypeWi, StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(request.MachineName))
            {
                return DocumentUploadResult.Failed("กรุณากรอก Machine name สำหรับเอกสาร WI");
            }

            var sanitizedFileName = SanitizeFileName(request.OriginalFileName);
            if (string.IsNullOrWhiteSpace(sanitizedFileName))
            {
                return DocumentUploadResult.Failed("ไม่สามารถระบุชื่อไฟล์ได้");
            }

            var manifestPath = Path.Combine(rootPath, GetManifestFileName());
            var manifest = await LoadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);

            int? preferredSequence = null;
            string? documentCode = null;

            if (!string.IsNullOrEmpty(normalizedDocumentType))
            {
                preferredSequence = CalculateNextSequence(manifest.Documents, normalizedDocumentType);
                documentCode = DocumentNumbering.FormatCode(normalizedDocumentType, preferredSequence);
            }

            var destinationRoot = rootPath;

            if (!string.IsNullOrWhiteSpace(documentCode))
            {
                destinationRoot = Path.Combine(rootPath, documentCode);

                try
                {
                    Directory.CreateDirectory(destinationRoot);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to ensure document folder '{Folder}' exists before uploading.", destinationRoot);
                    return DocumentUploadResult.Failed("ไม่สามารถสร้างโฟลเดอร์สำหรับเลขเอกสารนี้ได้");
                }
            }

            var destinationPath = ResolveDestinationPath(destinationRoot, sanitizedFileName);

            try
            {
                if (request.Content.CanSeek)
                {
                    request.Content.Seek(0, SeekOrigin.Begin);
                }

                await using var targetStream = File.Create(destinationPath);
                await request.Content.CopyToAsync(targetStream, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save uploaded document to '{Destination}'.", destinationPath);
                return DocumentUploadResult.Failed("ไม่สามารถบันทึกไฟล์ได้ กรุณาลองใหม่อีกครั้ง");
            }

            var relativeFileName = Path.GetRelativePath(rootPath, destinationPath)
                .Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            var manifestUpdateResult = await UpdateManifestAsync(
                    manifestPath,
                    manifest,
                    relativeFileName,
                    request,
                    normalizedDocumentType,
                    preferredSequence,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!manifestUpdateResult.Succeeded)
            {
                return DocumentUploadResult.Failed("บันทึกไฟล์สำเร็จ แต่ไม่สามารถอัปเดตข้อมูลไฟล์ได้");
            }

            _catalogService.InvalidateCache();

            return DocumentUploadResult.Success(
                normalizedPath: relativeFileName,
                documentType: manifestUpdateResult.DocumentType,
                sequenceNumber: manifestUpdateResult.SequenceNumber,
                documentCode: manifestUpdateResult.DocumentCode);
        }
        finally
        {
            _uploadLock.Release();
        }
    }

    public async Task<string?> GetNextDocumentCodeAsync(string? documentType, CancellationToken cancellationToken = default)
    {
        var normalizedType = DocumentNumbering.NormalizeType(documentType);

        if (!DocumentNumbering.IsKnownType(normalizedType))
        {
            return null;
        }

        await _uploadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var context = await _catalogService.EnsureCatalogContextAsync(cancellationToken).ConfigureAwait(false);
            var rootPath = context.ActiveRootPath;

            if (string.IsNullOrWhiteSpace(rootPath))
            {
                return null;
            }

            var manifestPath = Path.Combine(rootPath, GetManifestFileName());
            var manifest = await LoadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);

            var nextSequence = CalculateNextSequence(manifest.Documents, normalizedType);
            return DocumentNumbering.FormatCode(normalizedType, nextSequence);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to preview next document code for type {DocumentType}.", documentType);
            return null;
        }
        finally
        {
            _uploadLock.Release();
        }
    }

    private async Task<ManifestUpdateResult> UpdateManifestAsync(
        string manifestPath,
        ManifestDocument manifest,
        string relativeFileName,
        DocumentUploadRequest request,
        string normalizedDocumentType,
        int? preferredSequence,
        CancellationToken cancellationToken)
    {
        var entry = new ManifestEntry
        {
            FileName = relativeFileName,
            DisplayName = request.DisplayName?.Trim(),
            Line = request.Line?.Trim(),
            Station = request.Station?.Trim(),
            Model = request.Model?.Trim(),
            MachineName = request.MachineName?.Trim(),
            UploadedBy = request.UploadedBy?.Trim(),
            Comment = request.Comment?.Trim(),
            UpdatedAt = request.UploadedAt,
            DocumentType = string.IsNullOrEmpty(normalizedDocumentType) ? null : normalizedDocumentType
        };

        var existingIndex = manifest.Documents.FindIndex(d => string.Equals(d.FileName, relativeFileName, StringComparison.OrdinalIgnoreCase));
        ManifestEntry? existingEntry = existingIndex >= 0 ? manifest.Documents[existingIndex] : null;

        int? resolvedSequence = preferredSequence;

        if (existingEntry is not null)
        {
            var existingType = DocumentNumbering.NormalizeType(existingEntry.DocumentType);

            if (string.Equals(existingType, normalizedDocumentType, StringComparison.OrdinalIgnoreCase))
            {
                resolvedSequence = existingEntry.SequenceNumber;

                if (resolvedSequence is null || resolvedSequence <= 0)
                {
                    resolvedSequence = CalculateNextSequence(manifest.Documents, normalizedDocumentType);
                }
            }
            else
            {
                resolvedSequence = string.IsNullOrEmpty(normalizedDocumentType)
                    ? existingEntry.SequenceNumber
                    : CalculateNextSequence(manifest.Documents, normalizedDocumentType);
            }
        }
        else
        {
            if (resolvedSequence is null)
            {
                resolvedSequence = string.IsNullOrEmpty(normalizedDocumentType)
                    ? null
                    : CalculateNextSequence(manifest.Documents, normalizedDocumentType);
            }
        }

        entry.SequenceNumber = resolvedSequence;

        if (existingIndex >= 0)
        {
            manifest.Documents[existingIndex] = entry;
        }
        else
        {
            manifest.Documents.Add(entry);
        }

        manifest.Documents.Sort(CompareManifestEntries);

        try
        {
            await using var stream = File.Create(manifestPath);
            await JsonSerializer.SerializeAsync(stream, manifest, _serializerOptions, cancellationToken).ConfigureAwait(false);
            return ManifestUpdateResult.Success(entry.DocumentType, entry.SequenceNumber);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write manifest '{Manifest}'.", manifestPath);
            return ManifestUpdateResult.Failure(entry.DocumentType, entry.SequenceNumber);
        }
    }

    private async Task<ManifestDocument> LoadManifestAsync(string manifestPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(manifestPath))
        {
            return new ManifestDocument();
        }

        try
        {
            await using var stream = File.OpenRead(manifestPath);
            var manifest = await JsonSerializer.DeserializeAsync<ManifestDocument>(stream, _serializerOptions, cancellationToken).ConfigureAwait(false);
            return manifest ?? new ManifestDocument();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read manifest '{Manifest}', creating a new one.", manifestPath);
            return new ManifestDocument();
        }
    }

    private static string ResolveDestinationPath(string rootPath, string fileName)
    {
        var uniqueFileName = fileName;
        var destination = Path.Combine(rootPath, uniqueFileName);
        var counter = 1;

        while (File.Exists(destination))
        {
            var name = Path.GetFileNameWithoutExtension(fileName);
            var extension = Path.GetExtension(fileName);
            uniqueFileName = $"{name} ({counter}){extension}";
            destination = Path.Combine(rootPath, uniqueFileName);
            counter++;
        }

        return destination;
    }

    private static string SanitizeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        var name = Path.GetFileName(fileName.Trim());
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());

        return sanitized.Replace(' ', '_');
    }

    private string GetManifestFileName()
        => string.IsNullOrWhiteSpace(_options.ManifestFileName)
            ? "index.json"
            : _options.ManifestFileName!;

    private static int CalculateNextSequence(IReadOnlyCollection<ManifestEntry> entries, string normalizedDocumentType)
    {
        var max = 0;

        foreach (var entry in entries)
        {
            if (entry is null)
            {
                continue;
            }

            var entryType = DocumentNumbering.NormalizeType(entry.DocumentType);
            if (!string.Equals(entryType, normalizedDocumentType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (entry.SequenceNumber is > 0)
            {
                max = Math.Max(max, entry.SequenceNumber.Value);
            }
        }

        return max + 1;
    }

    private static int CompareManifestEntries(ManifestEntry? left, ManifestEntry? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return 1;
        }

        if (right is null)
        {
            return -1;
        }

        var leftType = DocumentNumbering.NormalizeType(left.DocumentType);
        var rightType = DocumentNumbering.NormalizeType(right.DocumentType);
        var typeComparison = string.Compare(leftType, rightType, StringComparison.OrdinalIgnoreCase);
        if (typeComparison != 0)
        {
            return typeComparison;
        }

        var leftSequence = left.SequenceNumber ?? int.MaxValue;
        var rightSequence = right.SequenceNumber ?? int.MaxValue;
        var sequenceComparison = leftSequence.CompareTo(rightSequence);
        if (sequenceComparison != 0)
        {
            return sequenceComparison;
        }

        return string.Compare(left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ManifestUpdateResult(bool Succeeded, string? DocumentType, int? SequenceNumber, string? DocumentCode)
    {
        public static ManifestUpdateResult Success(string? documentType, int? sequenceNumber)
            => new(true, documentType, sequenceNumber, DocumentNumbering.FormatCode(documentType, sequenceNumber));

        public static ManifestUpdateResult Failure(string? documentType, int? sequenceNumber)
            => new(false, documentType, sequenceNumber, DocumentNumbering.FormatCode(documentType, sequenceNumber));
    }
}

public sealed record DocumentUploadRequest(
    string DisplayName,
    string DocumentType,
    string? Line,
    string? Station,
    string? Model,
    string? MachineName,
    string? UploadedBy,
    string? Comment,
    string OriginalFileName,
    Stream Content,
    DateTimeOffset UploadedAt);

public sealed record DocumentUploadResult(bool Succeeded, string? NormalizedPath, string? ErrorMessage, string? DocumentType, int? SequenceNumber, string? DocumentCode)
{
    public static DocumentUploadResult Success(string normalizedPath, string? documentType, int? sequenceNumber, string? documentCode)
        => new(true, normalizedPath, null, documentType, sequenceNumber, documentCode);

    public static DocumentUploadResult Failed(string error)
        => new(false, null, error, null, null, null);
}

internal sealed class ManifestDocument
{
    public List<ManifestEntry> Documents { get; set; } = new();
}

internal sealed class ManifestEntry
{
    public string? FileName { get; set; }
    public string? DisplayName { get; set; }
    public string? Line { get; set; }
    public string? Station { get; set; }
    public string? Model { get; set; }
    public string? MachineName { get; set; }
    public string? UploadedBy { get; set; }
    public string? Comment { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? DocumentType { get; set; }
    public int? SequenceNumber { get; set; }
}
