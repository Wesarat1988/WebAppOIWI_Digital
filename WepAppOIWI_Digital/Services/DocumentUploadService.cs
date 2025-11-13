using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WepAppOIWI_Digital.Data;
using WepAppOIWI_Digital.Services;
using WepAppOIWI_Digital.Stamps;

namespace WepAppOIWI_Digital.Services;

public sealed class DocumentUploadService
{
    private readonly DocumentCatalogService _catalogService;
    private readonly DocumentCatalogOptions _options;
    private readonly ILogger<DocumentUploadService> _logger;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IVersionStore _versionStore; // จัดการ snapshot/version history
    private readonly IPdfStampService _pdfStampService;
    private readonly SemaphoreSlim _uploadLock = new(1, 1);
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private const string MissingFileSelectionMessage = "ต้องเลือกไฟล์ก่อนบันทึกการแก้ไข";

    public DocumentUploadService(
        DocumentCatalogService catalogService,
        IOptions<DocumentCatalogOptions> options,
        ILogger<DocumentUploadService> logger,
        IDbContextFactory<AppDbContext> dbContextFactory,
        IVersionStore versionStore,
        IPdfStampService pdfStampService)
    {
        _catalogService = catalogService;
        _options = options.Value;
        _logger = logger;
        _dbContextFactory = dbContextFactory;
        _versionStore = versionStore;
        _pdfStampService = pdfStampService;
    }

    // ประวัติย้อนหลังของไฟล์ พร้อม flag เวอร์ชันที่ใช้งานปัจจุบัน
    public async Task<IReadOnlyList<VersionDescriptor>> GetHistoryAsync(string normalizedPath, int take = 5, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return Array.Empty<VersionDescriptor>();
        }

        var descriptors = await LoadHistoryDescriptorsAsync(normalizedPath, ct).ConfigureAwait(false);

        if (take > 0 && descriptors.Count > take)
        {
            return descriptors.Take(take).ToList();
        }

        return descriptors;
    }

    public async Task<PagedResult<HistoryItem>> GetHistoryPageAsync(
        string normalizedPath,
        int page,
        int pageSize,
        string? search,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            var fallbackSize = Math.Clamp(pageSize, 1, 100);
            return new PagedResult<HistoryItem>(Array.Empty<HistoryItem>(), 0, 1, fallbackSize);
        }

        var descriptors = await LoadHistoryDescriptorsAsync(normalizedPath, ct).ConfigureAwait(false);

        IEnumerable<VersionDescriptor> query = descriptors;

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(d => Matches(term, d.VersionId) || Matches(term, d.Actor) || Matches(term, d.Comment));
        }

        var ordered = query
            .OrderByDescending(d => d.TimestampUtc)
            .ThenByDescending(d => d.VersionId, StringComparer.Ordinal)
            .ToList();

        var sanitizedPageSize = Math.Clamp(pageSize, 1, 100);
        var sanitizedPage = Math.Max(1, page);
        var skip = (sanitizedPage - 1) * sanitizedPageSize;

        if (skip < 0)
        {
            skip = 0;
        }

        var pageItems = ordered
            .Skip(skip)
            .Take(sanitizedPageSize)
            .Select(ToHistoryItem)
            .ToList();

        return new PagedResult<HistoryItem>(pageItems, ordered.Count, sanitizedPage, sanitizedPageSize);

        static bool Matches(string term, string? candidate)
            => !string.IsNullOrWhiteSpace(candidate) && candidate.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;

        HistoryItem ToHistoryItem(VersionDescriptor descriptor)
        {
            double? sizeKb = descriptor.SizeBytes.HasValue
                ? descriptor.SizeBytes.Value / 1024d
                : null;

            return new HistoryItem(
                descriptor.VersionId,
                descriptor.VersionId,
                descriptor.TimestampUtc,
                Normalize(descriptor.Actor),
                Normalize(descriptor.Comment),
                sizeKb,
                descriptor.IsActive);
        }

        static string? Normalize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim();
            return string.Equals(trimmed, "-", StringComparison.Ordinal) ? null : trimmed;
        }
    }

    private async Task<List<VersionDescriptor>> LoadHistoryDescriptorsAsync(string normalizedPath, CancellationToken ct)
    {
        var history = await _versionStore.ListAsync(normalizedPath, int.MaxValue, ct).ConfigureAwait(false);
        var descriptors = new List<VersionDescriptor>(history.Count);

        DocumentRecord? record = null;
        try
        {
            record = await _catalogService.TryGetDocumentAsync(normalizedPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load document metadata for history at {Path}.", normalizedPath);
        }

        var activeId = record?.ActiveVersionId;

        foreach (var descriptor in history)
        {
            var isActive = !string.IsNullOrEmpty(activeId)
                && string.Equals(descriptor.VersionId, activeId, StringComparison.OrdinalIgnoreCase);
            descriptors.Add(descriptor with { IsActive = isActive });
        }

        if (!string.IsNullOrWhiteSpace(activeId))
        {
            if (descriptors.All(d => !string.Equals(d.VersionId, activeId, StringComparison.OrdinalIgnoreCase)))
            {
                var activeHandle = await _versionStore.TryGetAsync(normalizedPath, activeId!, ct).ConfigureAwait(false);
                if (activeHandle?.Descriptor is not null)
                {
                    descriptors.Insert(0, activeHandle.Descriptor with { IsActive = true });
                }
            }
        }
        else if (record is not null)
        {
            var descriptor = CreateCurrentVersionDescriptor(record, normalizedPath);
            if (descriptor is not null)
            {
                descriptors.Insert(0, descriptor);
            }
        }

        return descriptors
            .OrderByDescending(d => d.TimestampUtc)
            .ThenByDescending(d => d.VersionId, StringComparer.Ordinal)
            .ToList();
    }

    private VersionDescriptor? CreateCurrentVersionDescriptor(DocumentRecord current, string normalizedPath)
    {
        try
        {
            var relativePath = string.IsNullOrWhiteSpace(current.FileName)
                ? normalizedPath
                : current.FileName;

            var physicalPath = _catalogService.ResolveDocumentPhysicalPath(relativePath);
            long? size = null;

            try
            {
                size = File.Exists(physicalPath) ? new FileInfo(physicalPath).Length : null;
            }
            catch
            {
                size = null;
            }

            return new VersionDescriptor(
                VersionId: "current",
                TimestampUtc: current.UpdatedAt ?? DateTimeOffset.UtcNow,
                Actor: NormalizeField(current.UploadedBy),
                Comment: NormalizeField(current.Comment),
                SizeBytes: size,
                PublicUrl: null,
                IsActive: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to compose active version descriptor for {Path}.", normalizedPath);
            return null;
        }

        static string? NormalizeField(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim();
            return string.Equals(trimmed, "-", StringComparison.Ordinal) ? null : trimmed;
        }
    }

    public async Task<bool> RevertToAsync(string normalizedPath, string versionId, string? actor, string? comment, CancellationToken ct = default)
    {
        await _catalogService.EnsureCatalogContextAsync(ct).ConfigureAwait(false);

        string physical;
        try
        {
            physical = _catalogService.ResolveDocumentPhysicalPath(normalizedPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve physical path for {Path} while reverting.", normalizedPath);
            return false;
        }

        var ok = await _versionStore.RestoreAsync(normalizedPath, versionId, physical, actor, comment, ct).ConfigureAwait(false);

        if (ok) _catalogService.InvalidateCache();
        return ok;
    }

    public async Task<VersionActivationResult> SetActiveVersionAsync(
        string normalizedPath,
        string versionId,
        string? actor,
        string? comment,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath) || string.IsNullOrWhiteSpace(versionId))
        {
            return VersionActivationResult.Failure("Missing document path or version identifier.");
        }

        await _uploadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var normalized = NormalizeManifestPath(normalizedPath);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return VersionActivationResult.Failure("ไม่พบเอกสารที่ต้องการตั้งค่า");
            }

            var context = await _catalogService.EnsureCatalogContextAsync(cancellationToken).ConfigureAwait(false);
            var rootPath = context.ActiveRootPath;

            if (string.IsNullOrWhiteSpace(rootPath))
            {
                _logger.LogWarning("Cannot set active version because the catalog root path is not configured.");
                return VersionActivationResult.Failure("ยังไม่ได้กำหนดโฟลเดอร์สำหรับเก็บเอกสาร OI/WI");
            }

            var manifestPath = Path.Combine(rootPath, GetManifestFileName());
            ManifestDocument manifest;
            ManifestDocument manifestSnapshot;
            try
            {
                manifest = await LoadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            }
            catch (DocumentManifestReadException ex)
            {
                _logger.LogError(ex, "Cannot set active version because the manifest could not be read.");
                return VersionActivationResult.Failure("ไม่สามารถอ่านข้อมูลเอกสารได้");
            }
            manifestSnapshot = CloneManifest(manifest);

            var lookupIndex = manifest.Documents.FindIndex(entry =>
                string.Equals(NormalizeManifestPath(entry.FileName), normalized, StringComparison.OrdinalIgnoreCase));

            if (lookupIndex < 0)
            {
                return VersionActivationResult.Failure("ไม่พบเอกสารที่ต้องการตั้งค่า");
            }

            var entry = manifest.Documents[lookupIndex];
            var existingRelative = entry.FileName ?? normalized;
            var rootFullPath = Path.GetFullPath(rootPath);
            var existingFullPath = Path.GetFullPath(Path.Combine(rootFullPath, existingRelative.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)));

            var versionHandle = await _versionStore.TryGetAsync(normalized, versionId, cancellationToken).ConfigureAwait(false);
            if (versionHandle is null)
            {
                return VersionActivationResult.Failure("ไม่พบไฟล์เวอร์ชันที่เลือก");
            }

            var versionFilePath = versionHandle.FilePath;
            if (!File.Exists(versionFilePath))
            {
                return VersionActivationResult.Failure("ไม่พบไฟล์เวอร์ชันที่เลือก");
            }

            var normalizedType = DocumentNumbering.NormalizeType(entry.DocumentType);
            var documentCode = DocumentNumbering.FormatCode(normalizedType, entry.SequenceNumber);

            var targetDirectory = Path.GetDirectoryName(existingFullPath) ?? rootFullPath;
            try
            {
                if (!string.IsNullOrEmpty(documentCode))
                {
                    (targetDirectory, _) = _catalogService.GetDocumentStorageDirectories(documentCode);
                }

                Directory.CreateDirectory(targetDirectory);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to prepare destination directory for activating {Path}.", normalized);
                return VersionActivationResult.Failure("ไม่สามารถเตรียมโฟลเดอร์ปลายทางได้");
            }

            var displayName = string.IsNullOrWhiteSpace(entry.DisplayName)
                ? Path.GetFileNameWithoutExtension(versionHandle.FileName)
                : entry.DisplayName!;
            var targetFileName = BuildCurrentFileName(displayName, versionHandle.FileName);
            var newFullPath = Path.Combine(targetDirectory, targetFileName);
            var newRelativePath = Path.GetRelativePath(rootFullPath, newFullPath)
                .Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            string? backupPath = null;
            string? tempPath = null;
            try
            {
                if (File.Exists(newFullPath))
                {
                    backupPath = CreateBackup(newFullPath);
                }
                else if (File.Exists(existingFullPath))
                {
                    backupPath = CreateBackup(existingFullPath);
                }

                tempPath = newFullPath + $".tmp-{Guid.NewGuid():N}";
                Directory.CreateDirectory(Path.GetDirectoryName(newFullPath)!);
                File.Copy(versionFilePath, tempPath, overwrite: true);
                File.Move(tempPath, newFullPath, overwrite: true);

                if (!string.Equals(existingFullPath, newFullPath, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(existingFullPath))
                {
                    TryDeleteFile(existingFullPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to activate version {VersionId} for {Path}.", versionId, normalized);
                if (!string.IsNullOrEmpty(tempPath))
                {
                    TryDeleteFile(tempPath);
                }

                if (!string.IsNullOrEmpty(backupPath))
                {
                    TryRestoreFromBackup(backupPath, existingFullPath);
                }

                return VersionActivationResult.Failure("ไม่สามารถตั้งไฟล์ให้ใช้งานได้");
            }
            finally
            {
                if (!string.IsNullOrEmpty(tempPath))
                {
                    TryDeleteFile(tempPath);
                }
            }

            var request = new DocumentUploadRequest(
                DisplayName: displayName,
                DocumentType: normalizedType ?? string.Empty,
                Line: entry.Line,
                Station: entry.Station,
                Model: entry.Model,
                MachineName: entry.MachineName,
                UploadedBy: string.IsNullOrWhiteSpace(actor) ? entry.UploadedBy : actor,
                Comment: string.IsNullOrWhiteSpace(comment) ? entry.Comment : comment,
                OriginalFileName: Path.GetFileName(newFullPath),
                Content: Stream.Null,
                UploadedAt: DateTimeOffset.UtcNow,
                StampMode: ParseStampMode(entry.StampMode),
                StampDate: entry.StampDate,
                ActiveVersionId: versionId);

            var manifestUpdateResult = await UpdateManifestAsync(
                    manifestPath,
                    manifest,
                    existingRelative,
                    newRelativePath,
                    request,
                    normalizedType ?? string.Empty,
                    entry.SequenceNumber,
                    incrementVersion: false,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!manifestUpdateResult.Succeeded)
            {
                if (!string.IsNullOrEmpty(backupPath))
                {
                    TryRestoreFromBackup(backupPath, existingFullPath);
                    TryDeleteFile(backupPath);
                }
                else
                {
                    _logger.LogWarning("Manifest update failed after activating version '{VersionId}' for '{Path}'.", versionId, normalized);
                }

                if (!string.Equals(existingFullPath, newFullPath, StringComparison.OrdinalIgnoreCase))
                {
                    TryDeleteFile(newFullPath);
                }

                return VersionActivationResult.Failure("ไม่สามารถอัปเดตข้อมูลเอกสารได้");
            }

            var manifestEntry = FindManifestEntry(manifest, newRelativePath);
            if (manifestEntry is null)
            {
                _logger.LogWarning("Manifest entry for {Path} not found after activating version {Version}.", newRelativePath, versionId);

                if (!string.IsNullOrEmpty(backupPath))
                {
                    TryRestoreFromBackup(backupPath, existingFullPath);
                    TryDeleteFile(backupPath);
                }

                if (!string.Equals(existingFullPath, newFullPath, StringComparison.OrdinalIgnoreCase))
                {
                    TryDeleteFile(newFullPath);
                }

                await PersistManifestAsync(manifestPath, manifestSnapshot, cancellationToken).ConfigureAwait(false);
                return VersionActivationResult.Failure("ไม่สามารถอัปเดตข้อมูลเอกสารได้");
            }

            try
            {
                await RegisterDocumentEntryAsync(
                        context,
                        newRelativePath,
                        newFullPath,
                        request,
                        manifestEntry,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to register active version {Version} for {Path} in database.", versionId, newRelativePath);

                await PersistManifestAsync(manifestPath, manifestSnapshot, cancellationToken).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(backupPath))
                {
                    TryRestoreFromBackup(backupPath, existingFullPath);
                    TryDeleteFile(backupPath);
                }

                if (!string.Equals(existingFullPath, newFullPath, StringComparison.OrdinalIgnoreCase))
                {
                    TryDeleteFile(newFullPath);
                }

                return VersionActivationResult.Failure("ไม่สามารถอัปเดตข้อมูลเอกสารได้");
            }

            if (!string.IsNullOrEmpty(backupPath))
            {
                TryDeleteFile(backupPath);
            }

            _catalogService.InvalidateCache();
            _logger.LogInformation("Set active version {Version} for {Path}", versionId, newRelativePath);

            return VersionActivationResult.Success(versionId, request.UploadedAt, manifestUpdateResult.DocumentCode ?? documentCode, newRelativePath);
        }
        finally
        {
            _uploadLock.Release();
        }
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
            ManifestDocument manifest;
            ManifestDocument manifestSnapshot;
            try
            {
                manifest = await LoadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            }
            catch (DocumentManifestReadException ex)
            {
                _logger.LogError(ex, "Cannot upload document because the manifest could not be read.");
                return DocumentUploadResult.Failed("ไม่สามารถอ่านข้อมูลเอกสารเดิมได้ กรุณาติดต่อผู้ดูแลระบบ");
            }
            manifestSnapshot = CloneManifest(manifest);

            int? preferredSequence = null;
            string? documentCode = null;

            if (!string.IsNullOrEmpty(normalizedDocumentType))
            {
                preferredSequence = CalculateNextSequenceSmart(rootPath, manifest, normalizedDocumentType);
                documentCode = DocumentNumbering.FormatCode(normalizedDocumentType, preferredSequence);
            }

            var destinationRoot = rootPath;
            string? currentDirectory = null;
            string? versionsDirectory = null;
            string? documentRoot = null;
            var createdCurrentDirectory = false;
            var createdVersionsDirectory = false;

            if (!string.IsNullOrWhiteSpace(documentCode))
            {
                try
                {
                    (currentDirectory, versionsDirectory) = _catalogService.GetDocumentStorageDirectories(documentCode);

                    if (!Directory.Exists(currentDirectory))
                    {
                        Directory.CreateDirectory(currentDirectory!);
                        createdCurrentDirectory = true;
                    }
                    else
                    {
                        Directory.CreateDirectory(currentDirectory!);
                    }

                    if (!string.IsNullOrEmpty(versionsDirectory))
                    {
                        if (!Directory.Exists(versionsDirectory))
                        {
                            Directory.CreateDirectory(versionsDirectory);
                            createdVersionsDirectory = true;
                        }
                        else
                        {
                            Directory.CreateDirectory(versionsDirectory);
                        }
                    }

                    destinationRoot = currentDirectory!;
                    documentRoot = Path.GetDirectoryName(currentDirectory);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to ensure document folder '{Folder}' exists before uploading.", documentCode);
                    return DocumentUploadResult.Failed("ไม่สามารถสร้างโฟลเดอร์สำหรับเลขเอกสารนี้ได้");
                }
            }

            var destinationFileName = BuildCurrentFileName(request.DisplayName, sanitizedFileName);
            var destinationPath = ResolveDestinationPath(destinationRoot, destinationFileName);

            byte[] fileBytes;
            try
            {
                if (request.Content.CanSeek)
                {
                    request.Content.Seek(0, SeekOrigin.Begin);
                }

                using var buffer = new MemoryStream();
                await request.Content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                fileBytes = buffer.ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read uploaded content before saving.");
                return DocumentUploadResult.Failed("ไม่สามารถอ่านไฟล์ได้ กรุณาลองใหม่อีกครั้ง");
            }

            if (request.StampMode != StampMode.None)
            {
                if (request.StampDate is null)
                {
                    _logger.LogWarning("Stamp mode '{StampMode}' selected without a stamp date. Rejecting upload.", request.StampMode);

                    if (createdCurrentDirectory)
                    {
                        TryDeleteDirectoryIfEmpty(currentDirectory);
                    }

                    if (createdVersionsDirectory)
                    {
                        TryDeleteDirectoryIfEmpty(versionsDirectory);
                    }

                    if (!string.IsNullOrEmpty(documentRoot))
                    {
                        TryDeleteDirectoryIfEmpty(documentRoot);
                    }

                    return DocumentUploadResult.Failed("กรุณาเลือกวันที่สำหรับตราประทับ");
                }

                if (!sanitizedFileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    if (createdCurrentDirectory)
                    {
                        TryDeleteDirectoryIfEmpty(currentDirectory);
                    }

                    if (createdVersionsDirectory)
                    {
                        TryDeleteDirectoryIfEmpty(versionsDirectory);
                    }

                    if (!string.IsNullOrEmpty(documentRoot))
                    {
                        TryDeleteDirectoryIfEmpty(documentRoot);
                    }

                    return DocumentUploadResult.Failed("สามารถประทับตราได้เฉพาะไฟล์ PDF เท่านั้น");
                }

                try
                {
                    _logger.LogInformation(
                        "Applying PDF stamp mode {StampMode} (date: {StampDate}) for upload '{FileName}'.",
                        request.StampMode,
                        request.StampDate,
                        request.OriginalFileName);

                    fileBytes = await _pdfStampService
                        .ApplyStampAsync(fileBytes, request.StampMode, request.StampDate, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to stamp PDF before saving to '{Destination}'.", destinationPath);
                    if (createdCurrentDirectory)
                    {
                        TryDeleteDirectoryIfEmpty(currentDirectory);
                    }

                    if (createdVersionsDirectory)
                    {
                        TryDeleteDirectoryIfEmpty(versionsDirectory);
                    }

                    if (!string.IsNullOrEmpty(documentRoot))
                    {
                        TryDeleteDirectoryIfEmpty(documentRoot);
                    }
                    return DocumentUploadResult.Failed("ไม่สามารถประทับตราเอกสารได้ กรุณาลองใหม่อีกครั้ง");
                }
            }

            try
            {
                await using var targetStream = File.Create(destinationPath);
                await targetStream.WriteAsync(fileBytes, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save uploaded document to '{Destination}'.", destinationPath);
                TryDeleteFile(destinationPath);
                if (createdCurrentDirectory)
                {
                    TryDeleteDirectoryIfEmpty(currentDirectory);
                }

                if (createdVersionsDirectory)
                {
                    TryDeleteDirectoryIfEmpty(versionsDirectory);
                }

                if (!string.IsNullOrEmpty(documentRoot))
                {
                    TryDeleteDirectoryIfEmpty(documentRoot);
                }
                return DocumentUploadResult.Failed("ไม่สามารถบันทึกไฟล์ได้ กรุณาลองใหม่อีกครั้ง");
            }

            var relativeFileName = Path.GetRelativePath(rootPath, destinationPath)
                .Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            LogStorageDestination(rootPath, relativeFileName);

            var manifestUpdateResult = await UpdateManifestAsync(
                    manifestPath,
                    manifest,
                    relativeFileName,
                    relativeFileName,
                    request,
                    normalizedDocumentType,
                    preferredSequence,
                    incrementVersion: false,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!manifestUpdateResult.Succeeded)
            {
                TryDeleteFile(destinationPath);
                if (createdCurrentDirectory)
                {
                    TryDeleteDirectoryIfEmpty(currentDirectory);
                }

                if (createdVersionsDirectory)
                {
                    TryDeleteDirectoryIfEmpty(versionsDirectory);
                }

                if (!string.IsNullOrEmpty(documentRoot))
                {
                    TryDeleteDirectoryIfEmpty(documentRoot);
                }
                return DocumentUploadResult.Failed("บันทึกไฟล์สำเร็จ แต่ไม่สามารถอัปเดตข้อมูลไฟล์ได้");
            }

            var manifestEntry = FindManifestEntry(manifest, relativeFileName);
            if (manifestEntry is null)
            {
                _logger.LogWarning("Manifest entry for {Path} not found after update; rolling back upload.", relativeFileName);
                TryDeleteFile(destinationPath);
                if (createdCurrentDirectory)
                {
                    TryDeleteDirectoryIfEmpty(currentDirectory);
                }

                if (createdVersionsDirectory)
                {
                    TryDeleteDirectoryIfEmpty(versionsDirectory);
                }

                if (!string.IsNullOrEmpty(documentRoot))
                {
                    TryDeleteDirectoryIfEmpty(documentRoot);
                }

                await PersistManifestAsync(manifestPath, manifestSnapshot, cancellationToken).ConfigureAwait(false);
                return DocumentUploadResult.Failed("บันทึกไฟล์สำเร็จ แต่ไม่สามารถอัปเดตข้อมูลไฟล์ได้");
            }

            try
            {
                await RegisterDocumentEntryAsync(
                        context,
                        relativeFileName,
                        destinationPath,
                        request,
                        manifestEntry,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to register uploaded document {Path} in database.", relativeFileName);
                TryDeleteFile(destinationPath);

                if (createdCurrentDirectory)
                {
                    TryDeleteDirectoryIfEmpty(currentDirectory);
                }

                if (createdVersionsDirectory)
                {
                    TryDeleteDirectoryIfEmpty(versionsDirectory);
                }

                if (!string.IsNullOrEmpty(documentRoot))
                {
                    TryDeleteDirectoryIfEmpty(documentRoot);
                }

                await PersistManifestAsync(manifestPath, manifestSnapshot, cancellationToken).ConfigureAwait(false);
                return DocumentUploadResult.Failed("บันทึกไฟล์สำเร็จ แต่ไม่สามารถลงทะเบียนข้อมูลได้");
            }

            try
            {
                await _versionStore.SnapshotAsync(relativeFileName, destinationPath, request.UploadedBy, request.Comment, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to snapshot newly uploaded document {Path}.", relativeFileName);
            }

            _catalogService.InvalidateCache();

            _logger.LogInformation("Uploaded document {Code} to {Path}", documentCode ?? "(no-code)", destinationPath);

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

    public async Task<DocumentUpdateResult> UpdateAsync(DocumentUpdateRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (request.Content is null || ReferenceEquals(request.Content, Stream.Null))
        {
            return DocumentUpdateResult.Failed(MissingFileSelectionMessage);
        }

        if (request.Content.CanSeek)
        {
            try
            {
                if (request.Content.Length <= 0)
                {
                    return DocumentUpdateResult.Failed(MissingFileSelectionMessage);
                }
            }
            catch (NotSupportedException)
            {
                // ignore and continue when length is unavailable even though seeking is allowed
            }
        }

        await _uploadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var normalizedPath = NormalizeManifestPath(request.NormalizedPath);
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                return DocumentUpdateResult.Failed("ไม่พบเอกสารที่ต้องการแก้ไข");
            }

            var context = await _catalogService.EnsureCatalogContextAsync(cancellationToken).ConfigureAwait(false);
            var rootPath = context.ActiveRootPath;

            if (string.IsNullOrWhiteSpace(rootPath))
            {
                _logger.LogWarning("Cannot update document because the catalog root path is not configured.");
                return DocumentUpdateResult.Failed("ยังไม่ได้กำหนดโฟลเดอร์สำหรับเก็บเอกสาร OI/WI");
            }

            var rootFullPath = Path.GetFullPath(rootPath);
            var manifestPath = Path.Combine(rootPath, GetManifestFileName());
            ManifestDocument manifest;
            ManifestDocument manifestSnapshot;
            try
            {
                manifest = await LoadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            }
            catch (DocumentManifestReadException ex)
            {
                _logger.LogError(ex, "Cannot update document because the manifest could not be read.");
                return DocumentUpdateResult.Failed("ไม่สามารถอ่านข้อมูลเอกสารเดิมได้ กรุณาติดต่อผู้ดูแลระบบ");
            }
            manifestSnapshot = CloneManifest(manifest);

            var existingIndex = manifest.Documents.FindIndex(entry =>
                string.Equals(NormalizeManifestPath(entry.FileName), normalizedPath, StringComparison.OrdinalIgnoreCase));

            if (existingIndex < 0)
            {
                return DocumentUpdateResult.Failed("ไม่พบเอกสารที่ต้องการแก้ไข");
            }

            var existingEntry = manifest.Documents[existingIndex];
            var existingNormalizedType = DocumentNumbering.NormalizeType(existingEntry.DocumentType);
            var requestedType = DocumentNumbering.NormalizeType(request.DocumentType);

            if (!string.Equals(existingNormalizedType, requestedType, StringComparison.OrdinalIgnoreCase))
            {
                return DocumentUpdateResult.Failed("ไม่สามารถเปลี่ยนประเภทเอกสารได้ในการแก้ไข");
            }

            var relativeWithSystemSeparators = normalizedPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            var candidatePath = Path.GetFullPath(Path.Combine(rootFullPath, relativeWithSystemSeparators));

            if (!IsPathWithinRoot(rootFullPath, candidatePath))
            {
                _logger.LogWarning("Requested update path '{Path}' is outside of the catalog root '{Root}'.", candidatePath, rootFullPath);
                return DocumentUpdateResult.Failed("ไม่สามารถแก้ไขไฟล์นอกโฟลเดอร์ที่กำหนดได้");
            }

            var documentCode = DocumentNumbering.FormatCode(existingNormalizedType, existingEntry.SequenceNumber);
            string targetDirectory = Path.GetDirectoryName(candidatePath) ?? rootFullPath;
            string? currentDirectory = null;
            string? versionsDirectory = null;

            if (!string.IsNullOrWhiteSpace(documentCode))
            {
                try
                {
                    (currentDirectory, versionsDirectory) = _catalogService.GetDocumentStorageDirectories(documentCode);
                    Directory.CreateDirectory(currentDirectory!);
                    if (!string.IsNullOrEmpty(versionsDirectory))
                    {
                        Directory.CreateDirectory(versionsDirectory);
                    }

                    targetDirectory = currentDirectory!;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to ensure document folder '{Folder}' exists before updating.", documentCode);
                    return DocumentUpdateResult.Failed("ไม่สามารถสร้างโฟลเดอร์สำหรับเลขเอกสารนี้ได้");
                }
            }
            else
            {
                try
                {
                    Directory.CreateDirectory(targetDirectory);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to ensure destination directory '{Directory}' exists before updating document.", targetDirectory);
                    return DocumentUpdateResult.Failed("ไม่สามารถสร้างโฟลเดอร์สำหรับไฟล์ได้");
                }
            }

            var existingExtension = Path.GetExtension(candidatePath);
            var desiredBaseName = DocumentCatalogService.Slugify(request.DisplayName);
            if (string.IsNullOrWhiteSpace(desiredBaseName))
            {
                desiredBaseName = DocumentCatalogService.Slugify(Path.GetFileNameWithoutExtension(candidatePath));
            }

            if (string.IsNullOrWhiteSpace(desiredBaseName))
            {
                desiredBaseName = Path.GetFileNameWithoutExtension(candidatePath) ?? "document";
            }

            var newFileName = string.IsNullOrWhiteSpace(existingExtension)
                ? desiredBaseName
                : desiredBaseName + existingExtension;
            var newFullPath = Path.Combine(targetDirectory, newFileName);
            var newRelativePath = Path.GetRelativePath(rootFullPath, newFullPath)
                .Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var pathChanged = !string.Equals(candidatePath, newFullPath, StringComparison.OrdinalIgnoreCase);

            string? backupPath = null;
            var fileReplaced = false;
            var renameApplied = false;
            string? tempPath = null;

            if (request.Content is not null)
            {
                try
                {
                    if (File.Exists(candidatePath))
                    {
                        try
                        {
                            await _versionStore.SnapshotAsync(normalizedPath, candidatePath, request.UploadedBy, request.Comment, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to snapshot document before updating {Path}.", normalizedPath);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to prepare snapshot for {Path} before updating.", normalizedPath);
                }

                try
                {
                    if (File.Exists(candidatePath))
                    {
                        backupPath = CreateBackup(candidatePath);
                    }

                    if (request.Content.CanSeek)
                    {
                        request.Content.Seek(0, SeekOrigin.Begin);
                    }

                    tempPath = newFullPath + $".tmp-{Guid.NewGuid():N}";

                    await using (var targetStream = File.Create(tempPath))
                    {
                        await request.Content.CopyToAsync(targetStream, cancellationToken).ConfigureAwait(false);
                    }

                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(newFullPath)!);
                    }
                    catch (Exception dirEx)
                    {
                        _logger.LogWarning(dirEx, "Failed to ensure directory '{Directory}' before replacing file.", Path.GetDirectoryName(newFullPath));
                    }

                    File.Move(tempPath, newFullPath, overwrite: true);
                    fileReplaced = true;

                    if (pathChanged)
                    {
                        TryDeleteFile(candidatePath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to replace document content at '{Destination}'.", newFullPath);
                    if (!string.IsNullOrEmpty(backupPath))
                    {
                        TryRestoreFromBackup(backupPath, candidatePath);
                        TryDeleteFile(backupPath);
                        backupPath = null;
                    }

                    if (!string.IsNullOrEmpty(tempPath))
                    {
                        TryDeleteFile(tempPath);
                        tempPath = null;
                    }

                    return DocumentUpdateResult.Failed("ไม่สามารถบันทึกไฟล์ที่แก้ไขได้ กรุณาลองใหม่อีกครั้ง");
                }
                finally
                {
                    if (!string.IsNullOrEmpty(tempPath))
                    {
                        TryDeleteFile(tempPath);
                        tempPath = null;
                    }
                }
            }
            else if (pathChanged && File.Exists(candidatePath))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(newFullPath)!);
                    File.Move(candidatePath, newFullPath, overwrite: true);
                    renameApplied = true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to move document to '{Destination}'.", newFullPath);
                    return DocumentUpdateResult.Failed("ไม่สามารถย้ายไฟล์ไปยังโฟลเดอร์ใหม่ได้");
                }
            }

            var metadataRequest = new DocumentUploadRequest(
                DisplayName: request.DisplayName!,
                DocumentType: existingNormalizedType,
                Line: request.Line,
                Station: request.Station,
                Model: request.Model,
                MachineName: request.MachineName,
                UploadedBy: request.UploadedBy,
                Comment: request.Comment,
                OriginalFileName: Path.GetFileName(newFullPath),
                Content: Stream.Null,
                UploadedAt: request.UpdatedAt,
                StampMode: ParseStampMode(existingEntry.StampMode),
                StampDate: existingEntry.StampDate);

            var manifestUpdateResult = await UpdateManifestAsync(
                    manifestPath,
                    manifest,
                    normalizedPath,
                    newRelativePath,
                    metadataRequest,
                    existingNormalizedType,
                    existingEntry.SequenceNumber,
                    incrementVersion: fileReplaced,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!manifestUpdateResult.Succeeded)
            {
                if (!string.IsNullOrEmpty(backupPath))
                {
                    TryRestoreFromBackup(backupPath, candidatePath);
                }
                else if (fileReplaced && request.Content is not null)
                {
                    _logger.LogWarning("Manifest update failed after replacing file '{File}'. Original backup was not available.", candidatePath);
                }

                if (renameApplied && pathChanged)
                {
                    try
                    {
                        File.Move(newFullPath, candidatePath, overwrite: true);
                    }
                    catch (Exception moveBackEx)
                    {
                        _logger.LogError(moveBackEx, "Failed to restore file location for '{Path}' after manifest error.", normalizedPath);
                    }
                }

                if (fileReplaced && pathChanged)
                {
                    TryDeleteFile(newFullPath);
                }

                if (!string.IsNullOrEmpty(backupPath))
                {
                    TryDeleteFile(backupPath);
                }
                return DocumentUpdateResult.Failed("ไม่สามารถอัปเดตข้อมูลไฟล์ได้");
            }

            var finalRelativePath = pathChanged ? newRelativePath : normalizedPath;
            var finalFullPath = pathChanged ? newFullPath : candidatePath;
            var manifestEntry = FindManifestEntry(manifest, finalRelativePath);

            if (manifestEntry is null)
            {
                _logger.LogWarning("Manifest entry for {Path} not found after update; rolling back.", finalRelativePath);

                if (!string.IsNullOrEmpty(backupPath))
                {
                    TryRestoreFromBackup(backupPath, candidatePath);
                }
                else if (fileReplaced && request.Content is not null)
                {
                    _logger.LogWarning("Manifest entry missing after updating file '{File}'.", candidatePath);
                }

                if (renameApplied && pathChanged)
                {
                    try
                    {
                        File.Move(newFullPath, candidatePath, overwrite: true);
                    }
                    catch (Exception moveBackEx)
                    {
                        _logger.LogError(moveBackEx, "Failed to restore file location for '{Path}' after manifest lookup error.", normalizedPath);
                    }
                }

                if (pathChanged && fileReplaced)
                {
                    TryDeleteFile(newFullPath);
                }

                await PersistManifestAsync(manifestPath, manifestSnapshot, cancellationToken).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(backupPath))
                {
                    TryDeleteFile(backupPath);
                }

                return DocumentUpdateResult.Failed("ไม่สามารถอัปเดตข้อมูลไฟล์ได้");
            }

            try
            {
                await RegisterDocumentEntryAsync(
                        context,
                        finalRelativePath,
                        finalFullPath,
                        metadataRequest,
                        manifestEntry,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to register updated document {Path} in database.", finalRelativePath);

                await PersistManifestAsync(manifestPath, manifestSnapshot, cancellationToken).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(backupPath))
                {
                    TryRestoreFromBackup(backupPath, candidatePath);
                }
                else if (fileReplaced && request.Content is not null)
                {
                    _logger.LogWarning("Database registration failed after replacing file '{File}'.", candidatePath);
                }

                if (renameApplied && pathChanged)
                {
                    try
                    {
                        File.Move(newFullPath, candidatePath, overwrite: true);
                    }
                    catch (Exception moveBackEx)
                    {
                        _logger.LogError(moveBackEx, "Failed to restore file location for '{Path}' after database error.", normalizedPath);
                    }
                }

                if (pathChanged && fileReplaced)
                {
                    TryDeleteFile(newFullPath);
                }

                if (!string.IsNullOrEmpty(backupPath))
                {
                    TryDeleteFile(backupPath);
                }

                return DocumentUpdateResult.Failed("ไม่สามารถอัปเดตข้อมูลไฟล์ได้");
            }

            if (!string.IsNullOrEmpty(backupPath))
            {
                TryDeleteFile(backupPath);
            }

            if (pathChanged || fileReplaced)
            {
                normalizedPath = newRelativePath;
            }

            _catalogService.InvalidateCache();

            _logger.LogInformation("Updated document {Code} -> {Path}", documentCode ?? "(no-code)", finalFullPath);

            return DocumentUpdateResult.Success(normalizedPath, manifestUpdateResult.DocumentType, manifestUpdateResult.SequenceNumber, manifestUpdateResult.DocumentCode);
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
            ManifestDocument manifest;
            try
            {
                manifest = await LoadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            }
            catch (DocumentManifestReadException ex)
            {
                _logger.LogError(ex, "Failed to preview next document code because the manifest could not be read.");
                return null;
            }

            var nextSequence = CalculateNextSequenceSmart(rootPath, manifest, normalizedType);
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
        string lookupRelativeFileName,
        string newRelativeFileName,
        DocumentUploadRequest request,
        string normalizedDocumentType,
        int? preferredSequence,
        bool incrementVersion,
        CancellationToken cancellationToken)
    {
        var entry = new ManifestEntry
        {
            FileName = newRelativeFileName,
            DisplayName = request.DisplayName?.Trim(),
            Line = request.Line?.Trim(),
            Station = request.Station?.Trim(),
            Model = request.Model?.Trim(),
            MachineName = request.MachineName?.Trim(),
            UploadedBy = request.UploadedBy?.Trim(),
            Comment = request.Comment?.Trim(),
            UpdatedAt = request.UploadedAt,
            DocumentType = string.IsNullOrEmpty(normalizedDocumentType) ? null : normalizedDocumentType,
            StampMode = request.StampMode != StampMode.None ? request.StampMode.ToString() : null,
            StampDate = request.StampMode != StampMode.None ? request.StampDate : null
        };

        var lookupNormalized = NormalizeManifestPath(lookupRelativeFileName);
        var existingIndex = manifest.Documents.FindIndex(d =>
            string.Equals(NormalizeManifestPath(d.FileName), lookupNormalized, StringComparison.OrdinalIgnoreCase));
        ManifestEntry? existingEntry = existingIndex >= 0 ? manifest.Documents[existingIndex] : null;

        int? resolvedSequence = preferredSequence;
        var rootPath = Path.GetDirectoryName(manifestPath) ?? string.Empty;

        if (existingEntry is not null)
        {
            var existingType = DocumentNumbering.NormalizeType(existingEntry.DocumentType);

            if (string.Equals(existingType, normalizedDocumentType, StringComparison.OrdinalIgnoreCase))
            {
                resolvedSequence = existingEntry.SequenceNumber;

                if (resolvedSequence is null || resolvedSequence <= 0)
                {
                    resolvedSequence = CalculateNextSequenceSmart(rootPath, manifest, normalizedDocumentType);
                }
            }
            else
            {
                resolvedSequence = string.IsNullOrEmpty(normalizedDocumentType)
                    ? existingEntry.SequenceNumber
                    : CalculateNextSequenceSmart(rootPath, manifest, normalizedDocumentType);
            }
        }
        else
        {
            if (resolvedSequence is null)
            {
                resolvedSequence = string.IsNullOrEmpty(normalizedDocumentType)
                    ? null
                    : CalculateNextSequenceSmart(rootPath, manifest, normalizedDocumentType);
            }
        }

        entry.SequenceNumber = resolvedSequence;

        var previousVersion = existingEntry?.Version ?? 0;
        if (previousVersion <= 0)
        {
            previousVersion = 1;
        }

        entry.Version = existingEntry is null
            ? 1
            : incrementVersion
                ? previousVersion + 1
                : previousVersion;

        if (incrementVersion)
        {
            entry.ActiveVersionId = null;
        }
        else
        {
            entry.ActiveVersionId = request.ActiveVersionId ?? existingEntry?.ActiveVersionId;
        }

        if (existingIndex >= 0)
        {
            manifest.Documents[existingIndex] = entry;
        }
        else
        {
            manifest.Documents.Add(entry);
        }

        manifest.Documents.Sort(CompareManifestEntries);

        var persisted = await PersistManifestAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
        return persisted
            ? ManifestUpdateResult.Success(entry.DocumentType, entry.SequenceNumber)
            : ManifestUpdateResult.Failure(entry.DocumentType, entry.SequenceNumber);
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
            throw new DocumentManifestReadException($"Failed to read manifest '{manifestPath}'.", ex);
        }
    }

    private async Task<bool> PersistManifestAsync(string manifestPath, ManifestDocument manifest, CancellationToken cancellationToken)
    {
        var manifestDirectory = Path.GetDirectoryName(manifestPath);
        var tempDirectory = string.IsNullOrEmpty(manifestDirectory) ? Path.GetTempPath() : manifestDirectory;
        var tempManifestPath = Path.Combine(tempDirectory, $"{Path.GetFileName(manifestPath)}.tmp-{Guid.NewGuid():N}");

        try
        {
            await using (var stream = File.Create(tempManifestPath))
            {
                await JsonSerializer.SerializeAsync(stream, manifest, _serializerOptions, cancellationToken).ConfigureAwait(false);
            }

            if (!string.IsNullOrEmpty(manifestDirectory) && !Directory.Exists(manifestDirectory))
            {
                Directory.CreateDirectory(manifestDirectory);
            }

            File.Copy(tempManifestPath, manifestPath, overwrite: true);
            File.Delete(tempManifestPath);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write manifest '{Manifest}'.", manifestPath);
            TryDeleteFile(tempManifestPath);
            return false;
        }
    }

    private static StampMode ParseStampMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return StampMode.None;
        }

        return Enum.TryParse<StampMode>(value, ignoreCase: true, out var result)
            ? result
            : StampMode.None;
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
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

    // รวมรหัสเอกสารที่มีอยู่จริง (ทั้งจากไฟล์ manifest และจากชื่อโฟลเดอร์ใน root)
    // รวมรหัสเอกสารที่ “มีอยู่จริง” (ทั้งจาก manifest ที่ไฟล์ยังอยู่ และจากชื่อโฟลเดอร์ใน root)
    private static IEnumerable<string> CollectExistingCodes(string rootPath, ManifestDocument manifest)
    {
        var list = new List<string>();

        // 1) จาก manifest: นับเฉพาะเอกสารที่ยังมีไฟล์อยู่จริง
        foreach (var e in manifest.Documents)
        {
            if (e is null) continue;
            if (e.SequenceNumber is int seq && seq > 0 && !string.IsNullOrWhiteSpace(e.DocumentType))
            {
                try
                {
                    // path ที่บันทึกใน manifest อาจเป็นไฟล์ใต้โฟลเดอร์เอกสาร หรือไฟล์ที่ root
                    var relative = (e.FileName ?? string.Empty)
                        .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                    var fullPath = Path.GetFullPath(Path.Combine(rootPath, relative));

                    // นับเฉพาะเมื่อไฟล์ยังอยู่จริง
                    if (!File.Exists(fullPath))
                    {
                        continue;
                    }

                    var code = DocumentNumbering.FormatCode(e.DocumentType, seq);
                    if (!string.IsNullOrEmpty(code)) list.Add(code!);
                }
                catch
                {
                    // best-effort
                }
            }
        }

        // 2) จากโฟลเดอร์ระดับบนใน root (ชื่อโฟลเดอร์เป็นเลขเอกสาร)
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(rootPath))
            {
                var name = Path.GetFileName(dir);
                if (string.IsNullOrWhiteSpace(name)) continue;

                // รองรับ OI-0001 / WI0002 (เราได้แก้ TryParseCode ให้รองรับ - แบบ optional แล้ว)
                if (DocumentNumbering.TryParseCode(name, out var type, out var seq) && seq > 0)
                {
                    var code = DocumentNumbering.FormatCode(type, seq);
                    if (!string.IsNullOrEmpty(code)) list.Add(code!);
                }
            }
        }
        catch
        {
            // best-effort
        }

        return list;
    }

    // คำนวณเลขถัดไป โดยใช้ smallest-missing จากชุดรหัสที่เก็บรวบรวมมาแล้ว
    // คำนวณเลขถัดไปจากชุดรหัสที่ตรวจสอบแล้วว่า “มีอยู่จริง”
    private static int CalculateNextSequenceSmart(string rootPath, ManifestDocument manifest, string normalizedDocumentType)
    {
        var existingCodes = CollectExistingCodes(rootPath, manifest);
        return DocumentNumbering.GetNextSequence(existingCodes, normalizedDocumentType);
    }



    private static void TryDeleteDirectoryIfEmpty(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path, recursive: false);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static string CreateBackup(string originalPath)
    {
        var directory = Path.GetDirectoryName(originalPath);
        var fileName = Path.GetFileName(originalPath);
        var backupName = $"{fileName}.bak-{Guid.NewGuid():N}";
        var backupPath = string.IsNullOrEmpty(directory)
            ? Path.Combine(Path.GetTempPath(), backupName)
            : Path.Combine(directory, backupName);

        File.Copy(originalPath, backupPath, overwrite: true);
        return backupPath;
    }

    private void TryRestoreFromBackup(string backupPath, string destinationPath)
    {
        try
        {
            if (!File.Exists(backupPath))
            {
                return;
            }

            File.Copy(backupPath, destinationPath, overwrite: true);
        }
        catch (Exception restoreEx)
        {
            _logger.LogError(restoreEx, "Failed to restore original file '{Destination}' from backup '{Backup}'.", destinationPath, backupPath);
        }
    }

    private async Task RegisterDocumentEntryAsync(
        DocumentCatalogService.DocumentCatalogContext context,
        string normalizedRelativePath,
        string physicalPath,
        DocumentUploadRequest request,
        ManifestEntry manifestEntry,
        CancellationToken cancellationToken)
    {
        if (manifestEntry is null)
        {
            throw new ArgumentNullException(nameof(manifestEntry));
        }

        var normalizedPath = NormalizeManifestPath(manifestEntry.FileName ?? normalizedRelativePath);
        if (string.IsNullOrEmpty(normalizedPath))
        {
            throw new InvalidOperationException("Cannot determine normalized path for document registration.");
        }

        var now = DateTimeOffset.UtcNow;
        var normalizedType = DocumentNumbering.NormalizeType(manifestEntry.DocumentType);
        var documentCode = DocumentNumbering.FormatCode(normalizedType, manifestEntry.SequenceNumber);

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var entity = await dbContext.Documents
            .FirstOrDefaultAsync(d => d.NormalizedPath == normalizedPath, cancellationToken)
            .ConfigureAwait(false);

        if (entity is null)
        {
            entity = new DocumentEntity
            {
                Id = Guid.NewGuid(),
                NormalizedPath = normalizedPath
            };

            dbContext.Documents.Add(entity);
        }
        else
        {
            entity.NormalizedPath = normalizedPath;
        }

        var fileInfo = new FileInfo(physicalPath);
        var displayName = !string.IsNullOrWhiteSpace(manifestEntry.DisplayName)
            ? manifestEntry.DisplayName!.Trim()
            : request.DisplayName;

        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = Path.GetFileNameWithoutExtension(physicalPath);
        }

        entity.DisplayName = string.IsNullOrWhiteSpace(displayName)
            ? Path.GetFileNameWithoutExtension(physicalPath)
            : displayName;
        entity.RelativePath = normalizedPath;
        entity.FileName = Path.GetFileName(physicalPath);
        entity.Line = NormalizeOptional(request.Line);
        entity.Station = NormalizeOptional(request.Station);
        entity.Model = NormalizeOptional(request.Model);
        entity.Machine = NormalizeOptional(request.MachineName);
        entity.UploadedBy = NormalizeOptional(request.UploadedBy);
        entity.Comment = NormalizeOptional(request.Comment);
        entity.DocumentType = normalizedType;
        entity.SequenceNumber = manifestEntry.SequenceNumber;
        entity.DocumentCode = documentCode;

        var manifestVersion = manifestEntry.Version.GetValueOrDefault();
        if (manifestVersion <= 0 && entity.Version > 0)
        {
            manifestVersion = entity.Version;
        }
        else if (manifestVersion <= 0)
        {
            manifestVersion = 1;
        }

        entity.Version = manifestVersion;
        entity.ActiveVersionId = manifestEntry.ActiveVersionId;
        entity.UpdatedAt = request.UploadedAt.ToUniversalTime();
        entity.UpdatedAtUnixMs = request.UploadedAt.ToUnixTimeMilliseconds();
        entity.IndexedAtUtc = now;
        entity.SizeBytes = fileInfo.Exists ? fileInfo.Length : 0L;
        entity.LastWriteUtc = fileInfo.Exists
            ? new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero)
            : (DateTimeOffset?)null;
        entity.LinkUrl = BuildDocumentLink(context, normalizedPath, fileInfo.Exists ? fileInfo.FullName : physicalPath);
        entity.StampMode = request.StampMode;
        entity.StampDate = request.StampDate;

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private ManifestEntry? FindManifestEntry(ManifestDocument manifest, string normalizedRelativePath)
    {
        if (manifest is null)
        {
            return null;
        }

        var lookup = NormalizeManifestPath(normalizedRelativePath);
        if (string.IsNullOrEmpty(lookup))
        {
            return null;
        }

        return manifest.Documents.FirstOrDefault(entry =>
            string.Equals(NormalizeManifestPath(entry.FileName), lookup, StringComparison.OrdinalIgnoreCase));
    }

    private static ManifestDocument CloneManifest(ManifestDocument manifest)
    {
        var clone = new ManifestDocument
        {
            Documents = manifest.Documents
                .Select(entry => new ManifestEntry
                {
                    FileName = entry.FileName,
                    DisplayName = entry.DisplayName,
                    Line = entry.Line,
                    Station = entry.Station,
                    Model = entry.Model,
                    MachineName = entry.MachineName,
                    UploadedBy = entry.UploadedBy,
                    Comment = entry.Comment,
                    UpdatedAt = entry.UpdatedAt,
                    DocumentType = entry.DocumentType,
                    SequenceNumber = entry.SequenceNumber,
                    Version = entry.Version,
                    ActiveVersionId = entry.ActiveVersionId,
                    StampMode = entry.StampMode,
                    StampDate = entry.StampDate
                })
                .ToList()
        };

        return clone;
    }

    private void LogStorageDestination(string rootPath, string relativePath)
    {
        try
        {
            var normalizedRoot = Path.GetFullPath(rootPath);
            if (!string.IsNullOrWhiteSpace(_options.AbsolutePath))
            {
                var configuredRoot = Path.GetFullPath(_options.AbsolutePath);
                if (!string.Equals(normalizedRoot, configuredRoot, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "Active storage root '{ActiveRoot}' differs from configured absolute path '{Configured}'.",
                        normalizedRoot,
                        configuredRoot);
                }
            }

            _logger.LogInformation("Saving upload to {Root}/{Rel}", normalizedRoot, relativePath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to normalize storage paths for logging.");
            _logger.LogInformation("Saving upload to {Root}/{Rel}", rootPath, relativePath);
        }
    }

    private static string? NormalizeOptional(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static string? BuildDocumentLink(
        DocumentCatalogService.DocumentCatalogContext context,
        string normalizedRelativePath,
        string physicalPath)
    {
        if (context.LinkKind == DocumentCatalogService.CatalogLinkKind.Absolute)
        {
            if (Uri.TryCreate(physicalPath, UriKind.Absolute, out var absoluteUri))
            {
                return absoluteUri.AbsoluteUri;
            }

            return null;
        }

        var relativePrefix = (context.RelativeRequestPrefix ?? string.Empty)
            .Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var segments = normalizedRelativePath.Split(
            Path.AltDirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        var encoded = string.Join(Path.AltDirectorySeparatorChar, segments.Select(Uri.EscapeDataString));

        return string.IsNullOrEmpty(relativePrefix)
            ? $"/{encoded}"
            : $"/{relativePrefix}/{encoded}";
    }

    private static string BuildCurrentFileName(string displayName, string originalFileName)
    {
        var extension = Path.GetExtension(originalFileName);
        var baseName = DocumentCatalogService.Slugify(displayName);

        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = DocumentCatalogService.Slugify(Path.GetFileNameWithoutExtension(originalFileName));
        }

        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = Path.GetFileNameWithoutExtension(originalFileName) ?? "document";
        }

        return string.IsNullOrWhiteSpace(extension) ? baseName : baseName + extension;
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

    private static string NormalizeManifestPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return path
            .Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Trim()
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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

    // (ไม่ถูกใช้แล้ว แต่เก็บเผื่ออ้างอิง)
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
    DateTimeOffset UploadedAt,
    StampMode StampMode = StampMode.None,
    DateOnly? StampDate = null,
    string? ActiveVersionId = null);

public sealed record DocumentUploadResult(bool Succeeded, string? NormalizedPath, string? ErrorMessage, string? DocumentType, int? SequenceNumber, string? DocumentCode)
{
    public static DocumentUploadResult Success(string normalizedPath, string? documentType, int? sequenceNumber, string? documentCode)
        => new(true, normalizedPath, null, documentType, sequenceNumber, documentCode);

    public static DocumentUploadResult Failed(string error)
        => new(false, null, error, null, null, null);
}

public sealed record DocumentUpdateRequest(
    string NormalizedPath,
    string DisplayName,
    string DocumentType,
    string? Line,
    string? Station,
    string? Model,
    string? MachineName,
    string? UploadedBy,
    string? Comment,
    Stream? Content,
    DateTimeOffset UpdatedAt);

public sealed record DocumentUpdateResult(bool Succeeded, string? NormalizedPath, string? ErrorMessage, string? DocumentType, int? SequenceNumber, string? DocumentCode)
{
    public static DocumentUpdateResult Success(string normalizedPath, string? documentType, int? sequenceNumber, string? documentCode)
        => new(true, normalizedPath, null, documentType, sequenceNumber, documentCode);

    public static DocumentUpdateResult Failed(string error)
        => new(false, null, error, null, null, null);
}

public sealed record VersionActivationResult(
    bool Succeeded,
    string? ActiveVersionId,
    DateTimeOffset? UpdatedAtUtc,
    string? DocumentCode,
    string? NormalizedPath,
    string? ErrorMessage)
{
    public static VersionActivationResult Success(string activeVersionId, DateTimeOffset updatedAtUtc, string? documentCode, string? normalizedPath)
        => new(true, activeVersionId, updatedAtUtc, documentCode, normalizedPath, null);

    public static VersionActivationResult Failure(string? error)
        => new(false, null, null, null, null, error);
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
    public int? Version { get; set; }
    public string? ActiveVersionId { get; set; }
    public string? StampMode { get; set; }
    public DateOnly? StampDate { get; set; }
}

internal sealed class DocumentManifestReadException : Exception
{
    public DocumentManifestReadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
