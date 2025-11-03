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

            var sanitizedFileName = SanitizeFileName(request.OriginalFileName);
            if (string.IsNullOrWhiteSpace(sanitizedFileName))
            {
                return DocumentUploadResult.Failed("ไม่สามารถระบุชื่อไฟล์ได้");
            }

            var destinationPath = ResolveDestinationPath(rootPath, sanitizedFileName);

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

            var relativeFileName = Path.GetFileName(destinationPath)
                .Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            var manifestUpdateResult = await UpdateManifestAsync(rootPath, relativeFileName, request, cancellationToken).ConfigureAwait(false);
            if (!manifestUpdateResult)
            {
                return DocumentUploadResult.Failed("บันทึกไฟล์สำเร็จ แต่ไม่สามารถอัปเดตข้อมูลไฟล์ได้");
            }

            _catalogService.InvalidateCache();

            return DocumentUploadResult.Success(relativeFileName);
        }
        finally
        {
            _uploadLock.Release();
        }
    }

    private async Task<bool> UpdateManifestAsync(string rootPath, string relativeFileName, DocumentUploadRequest request, CancellationToken cancellationToken)
    {
        var manifestFileName = string.IsNullOrWhiteSpace(_options.ManifestFileName)
            ? "index.json"
            : _options.ManifestFileName;

        var manifestPath = Path.Combine(rootPath, manifestFileName);
        var manifest = await LoadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);

        var entry = new ManifestEntry
        {
            FileName = relativeFileName,
            DisplayName = request.DisplayName?.Trim(),
            Line = request.Line?.Trim(),
            Station = request.Station?.Trim(),
            Model = request.Model?.Trim(),
            UploadedBy = request.UploadedBy?.Trim(),
            Comment = request.Comment?.Trim(),
            UpdatedAt = request.UploadedAt
        };

        var existingIndex = manifest.Documents.FindIndex(d => string.Equals(d.FileName, relativeFileName, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            manifest.Documents[existingIndex] = entry;
        }
        else
        {
            manifest.Documents.Add(entry);
        }

        manifest.Documents.Sort((a, b) => string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase));

        try
        {
            await using var stream = File.Create(manifestPath);
            await JsonSerializer.SerializeAsync(stream, manifest, _serializerOptions, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write manifest '{Manifest}'.", manifestPath);
            return false;
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
}

public sealed record DocumentUploadRequest(
    string DisplayName,
    string? Line,
    string? Station,
    string? Model,
    string? UploadedBy,
    string? Comment,
    string OriginalFileName,
    Stream Content,
    DateTimeOffset UploadedAt);

public sealed record DocumentUploadResult(bool Succeeded, string? NormalizedPath, string? ErrorMessage)
{
    public static DocumentUploadResult Success(string normalizedPath)
        => new(true, normalizedPath, null);

    public static DocumentUploadResult Failed(string error)
        => new(false, null, error);
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
    public string? UploadedBy { get; set; }
    public string? Comment { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
