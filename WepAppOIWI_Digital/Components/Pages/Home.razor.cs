using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.JSInterop;
using WepAppOIWI_Digital.Services;
using WepAppOIWI_Digital.Stamps;

namespace WepAppOIWI_Digital.Components.Pages;

public partial class Home : IDisposable
{
    private readonly List<DocumentRecord> documents = new();
    private PagedResult<OiwiRow> pageData = new(Array.Empty<OiwiRow>(), 0, 1, 20);
    private readonly int[] pageSizes = new[] { 10, 20, 50, 100 };

    private string searchTerm = string.Empty;
    private string selectedDocumentType = string.Empty;
    private string selectedLine = string.Empty;
    private string selectedStation = string.Empty;
    private string selectedModel = string.Empty;
    private string selectedUploader = string.Empty;

    private DocumentCatalogService.DocumentCatalogContext catalogContext = DocumentCatalogService.DocumentCatalogContext.Uninitialized;
    private bool isLoading = true;
    private bool isError;
    private string? errorMessage;
    private bool showSlowMessage;
    private string? activeDirectoryPath;
    private string? statusAlertClass;
    private string? statusMessage;
    private string? storageNoteMessage;

    private bool _documentsLoaded;
    private bool _pendingScroll;
    private bool _pendingFocus;
    private bool _loadQueued;
    private CancellationTokenSource? _cts;

    private int currentPage = 1;
    private int currentPageSize = 20;
    private string currentSortColumn = "Time";
    private bool currentSortDescending = true;
    private string JumpInput { get; set; } = string.Empty;

    private string? configuredRootPath;
    private int totalDocuments;
    private bool isStorageConfigured;

    private bool HasAnyDocuments => totalDocuments > 0;
    private bool ShouldShowMissingFolderBanner => !isStorageConfigured && !HasAnyDocuments;
    private bool ShouldShowStorageNote => !isStorageConfigured && HasAnyDocuments;

    [SupplyParameterFromQuery(Name = "page")] public int PageNumberQuery { get; set; } = 1;
    [SupplyParameterFromQuery(Name = "pageSize")] public int PageSizeQuery { get; set; } = 20;
    [SupplyParameterFromQuery(Name = "search")] public string? SearchQuery { get; set; }
    [SupplyParameterFromQuery(Name = "sort")] public string? SortQuery { get; set; }
    [SupplyParameterFromQuery(Name = "desc")] public bool SortDescQuery { get; set; } = true;

    protected override async Task OnInitializedAsync()
    {
        if (!Nav.ToAbsoluteUri(Nav.Uri).Query.Contains("pageSize=", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var saved = await JS.InvokeAsync<int?>("oiwi_getPageSize", Array.Empty<object?>());
                if (saved is int s && pageSizes.Contains(s))
                {
                    PageSizeQuery = s;
                    currentPageSize = s;
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Unable to read stored page size from localStorage.");
            }
        }
    }

    protected override void OnParametersSet()
    {
        CancelPending();

        currentPage = Math.Max(1, PageNumberQuery);
        currentPageSize = pageSizes.Contains(PageSizeQuery) ? PageSizeQuery : 20;
        currentSortColumn = string.IsNullOrWhiteSpace(SortQuery) ? "Time" : SortQuery!;
        currentSortDescending = SortDescQuery;

        var filters = DocumentCatalogService.ParseOiwiSearchQuery(SearchQuery);
        searchTerm = filters.Keyword ?? string.Empty;
        selectedDocumentType = filters.DocumentType ?? string.Empty;
        selectedLine = filters.Line ?? string.Empty;
        selectedStation = filters.Station ?? string.Empty;
        selectedModel = filters.Model ?? string.Empty;
        selectedUploader = filters.Uploader ?? string.Empty;

        catalogContext = DocumentCatalog.GetCatalogContext();
        UpdateCatalogStatus(catalogContext);

        isLoading = true;
        isError = false;
        errorMessage = null;
        showSlowMessage = false;
        JumpInput = string.Empty;
        _loadQueued = true;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_loadQueued)
        {
            _loadQueued = false;
            _ = Task.Run(async () =>
            {
                try
                {
                    await LoadFirstPageAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Failed to load initial OI/WI data set.");
                }

                try
                {
                    await InvokeAsync(StateHasChanged);
                }
                catch (ObjectDisposedException)
                {
                }
                catch (InvalidOperationException ex)
                {
                    Logger.LogDebug(ex, "Skipping state update after initial load because the component was disposed.");
                }
            });
        }

        if (_pendingScroll)
        {
            _pendingScroll = false;
            try
            {
                await JS.InvokeVoidAsync("oiwi_scrollToId", "oiwiTableTop");
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Failed to scroll table into view.");
            }
        }

        if (_pendingFocus)
        {
            _pendingFocus = false;
            try
            {
                await JS.InvokeVoidAsync("oiwi_focusById", CurrentPageBtnId);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Failed to focus current pagination button.");
            }
        }
    }

    private async Task LoadFilterOptionsAsync(CancellationToken cancellationToken)
    {
        if (_documentsLoaded)
        {
            return;
        }

        try
        {
            var result = await DocumentCatalog.GetDocumentsAsync(cancellationToken).ConfigureAwait(false);
            documents.Clear();
            documents.AddRange(result);
            totalDocuments = documents.Count;
            catalogContext = DocumentCatalog.GetCatalogContext();
            UpdateCatalogStatus(catalogContext);
            _documentsLoaded = true;
        }
        catch (OperationCanceledException)
        {
            // ignored when navigation cancels the load
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to refresh filter dataset.");
        }
    }

    private async Task LoadFirstPageAsync()
    {
        var localCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        _cts = localCts;
        var token = localCts.Token;

        isLoading = true;
        isError = false;
        errorMessage = null;
        showSlowMessage = false;
        await InvokeAsync(StateHasChanged);

        _ = ShowSlowNoticeAsync(localCts);

        try
        {
            OiwiIndexingResult indexingResult = OiwiIndexingResult.Empty;
            try
            {
                indexingResult = await IndexingService.RefreshIndexAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to refresh OI/WI index before loading list page.");
            }

            if (indexingResult.TotalChanges > 0)
            {
                DocumentCatalog.InvalidateCache();
            }

            var filters = new OiwiSearchFilters(
                NullIfEmpty(searchTerm),
                NullIfEmpty(selectedDocumentType),
                NullIfEmpty(selectedLine),
                NullIfEmpty(selectedStation),
                NullIfEmpty(selectedModel),
                NullIfEmpty(selectedUploader));

            var searchPayload = DocumentCatalogService.BuildOiwiSearchQuery(filters);
            pageData = await DocumentCatalog.GetOiwiPageAsync(
                currentPage,
                currentPageSize,
                searchPayload,
                currentSortColumn,
                currentSortDescending,
                token);

            isError = false;
            errorMessage = null;
            _pendingScroll = true;
            _pendingFocus = true;
            await LoadFilterOptionsAsync(token);
        }
        catch (OperationCanceledException) when (!ReferenceEquals(_cts, localCts))
        {
            // cancelled due to navigation or manual refresh; no user-facing error
        }
        catch (OperationCanceledException)
        {
            isError = true;
            errorMessage = "การเชื่อมต่อช้าเกินไป โปรดลองอีกครั้ง";
            pageData = new PagedResult<OiwiRow>(Array.Empty<OiwiRow>(), 0, currentPage, currentPageSize);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to load paged OI/WI data.");
            isError = true;
            errorMessage = "ไม่สามารถโหลดรายการเอกสารได้ (ปัญหาการจัดเรียงเวลาในฐานข้อมูล) – กด “ลองอีกครั้ง” หรือแจ้งผู้ดูแลระบบ";
            pageData = new PagedResult<OiwiRow>(Array.Empty<OiwiRow>(), 0, currentPage, currentPageSize);
        }
        finally
        {
            showSlowMessage = false;
            isLoading = false;

            if (ReferenceEquals(_cts, localCts))
            {
                _cts.Dispose();
                _cts = null;
            }
            else
            {
                localCts.Dispose();
            }
        }

        await InvokeAsync(StateHasChanged);
    }

    private async Task ShowSlowNoticeAsync(CancellationTokenSource source)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), source.Token).ConfigureAwait(false);
            if (!source.IsCancellationRequested)
            {
                showSlowMessage = true;
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException)
        {
            // expected when the load completes quickly or is cancelled
        }
    }

    private void CancelPending()
    {
        if (_cts is null)
        {
            return;
        }

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
        }
    }

    private async Task TriggerReloadAsync(bool refreshFilters = false)
    {
        if (refreshFilters)
        {
            _documentsLoaded = false;
        }

        CancelPending();

        isLoading = true;
        isError = false;
        errorMessage = null;
        showSlowMessage = false;
        await InvokeAsync(StateHasChanged);

        await LoadFirstPageAsync();
    }

    private Task RetryAsync() => TriggerReloadAsync();

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private IEnumerable<int?> GetPageList()
    {
        const int pageWindow = 2;
        var lastPage = LastPage;

        if (lastPage <= 7)
        {
            for (var i = 1; i <= lastPage; i++)
            {
                yield return i;
            }
            yield break;
        }

        yield return 1;

        var left = Math.Max(2, pageData.Page - pageWindow);
        if (left > 2)
        {
            yield return null;
        }

        var right = Math.Min(lastPage - 1, pageData.Page + pageWindow);
        for (var i = left; i <= right; i++)
        {
            yield return i;
        }

        if (right < lastPage - 1)
        {
            yield return null;
        }

        yield return lastPage;
    }

    private int StartIndex => pageData.TotalCount == 0 ? 0 : ((pageData.Page - 1) * pageData.PageSize) + 1;
    private int EndIndex => Math.Min(pageData.Page * pageData.PageSize, pageData.TotalCount);
    private int LastPage => pageData.TotalCount == 0 ? 1 : (int)Math.Ceiling(pageData.TotalCount / (double)pageData.PageSize);
    private string CurrentPageBtnId => $"oiwi-pagebtn-{pageData.Page}";

    private void Go(int newPage)
    {
        var sanitized = Math.Clamp(newPage, 1, LastPage);
        var filters = new OiwiSearchFilters(
            NullIfEmpty(searchTerm),
            NullIfEmpty(selectedDocumentType),
            NullIfEmpty(selectedLine),
            NullIfEmpty(selectedStation),
            NullIfEmpty(selectedModel),
            NullIfEmpty(selectedUploader));

        var queryValues = new Dictionary<string, string?>
        {
            ["page"] = sanitized.ToString(CultureInfo.InvariantCulture),
            ["pageSize"] = currentPageSize.ToString(CultureInfo.InvariantCulture),
            ["desc"] = currentSortDescending ? "true" : "false"
        };

        if (!string.IsNullOrWhiteSpace(currentSortColumn))
        {
            queryValues["sort"] = currentSortColumn;
        }

        var payload = DocumentCatalogService.BuildOiwiSearchQuery(filters);
        if (!string.IsNullOrWhiteSpace(payload))
        {
            queryValues["search"] = payload;
        }

        var target = QueryHelpers.AddQueryString(GetCurrentRouteBase(), queryValues);
        Nav.NavigateTo(target, forceLoad: false);
    }

    private async Task ChangePageSize(ChangeEventArgs args)
    {
        if (int.TryParse(args.Value?.ToString(), out var selected) && pageSizes.Contains(selected))
        {
            currentPageSize = selected;
            PageSizeQuery = selected;
            try
            {
                await JS.InvokeVoidAsync("oiwi_setPageSize", selected);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Unable to persist page size to localStorage.");
            }

            Go(1);
        }
    }

    private void Jump()
    {
        if (int.TryParse(JumpInput, NumberStyles.Integer, CultureInfo.InvariantCulture, out var requested))
        {
            Go(requested);
        }
    }

    private void OnJumpInputChanged(ChangeEventArgs args)
    {
        JumpInput = args.Value?.ToString() ?? string.Empty;
    }

    private void OnJumpInputKeyDown(KeyboardEventArgs args)
    {
        if (string.Equals(args.Key, "Enter", StringComparison.OrdinalIgnoreCase))
        {
            Jump();
        }
    }

    private void OnDocumentTypeChanged(ChangeEventArgs args)
        => UpdateFilterSelection(ref selectedDocumentType, args.Value?.ToString());

    private void OnLineChanged(ChangeEventArgs args)
        => UpdateFilterSelection(ref selectedLine, args.Value?.ToString());

    private void OnStationChanged(ChangeEventArgs args)
        => UpdateFilterSelection(ref selectedStation, args.Value?.ToString());

    private void OnModelChanged(ChangeEventArgs args)
        => UpdateFilterSelection(ref selectedModel, args.Value?.ToString());

    private void OnUploaderChanged(ChangeEventArgs args)
        => UpdateFilterSelection(ref selectedUploader, args.Value?.ToString());

    private void OnSearchInput(ChangeEventArgs args)
    {
        var newValue = args.Value?.ToString() ?? string.Empty;
        if (!string.Equals(newValue, searchTerm, StringComparison.Ordinal))
        {
            searchTerm = newValue;
            Go(1);
        }
    }

    private void ApplyFiltersFromModal()
    {
        Go(1);
    }

    private void ClearAllFilters()
    {
        searchTerm = string.Empty;
        selectedDocumentType = string.Empty;
        selectedLine = string.Empty;
        selectedStation = string.Empty;
        selectedModel = string.Empty;
        selectedUploader = string.Empty;
        Go(1);
    }

    private bool IsClearDisabled()
        => string.IsNullOrWhiteSpace(searchTerm)
            && string.IsNullOrWhiteSpace(selectedDocumentType)
            && string.IsNullOrWhiteSpace(selectedLine)
            && string.IsNullOrWhiteSpace(selectedStation)
            && string.IsNullOrWhiteSpace(selectedModel)
            && string.IsNullOrWhiteSpace(selectedUploader);

    private IEnumerable<string> GetAvailableLines()
        => GetDistinctValues(document => document.Line);

    private IEnumerable<string> GetAvailableStations()
        => GetDistinctValues(document => document.Station);

    private IEnumerable<string> GetAvailableModels()
        => GetDistinctValues(document => document.Model);

    private IEnumerable<string> GetAvailableUploaders()
        => GetDistinctValues(document => document.UploadedBy);

    private IEnumerable<string> GetAvailableDocumentTypes()
        => GetDistinctValues(document => document.DocumentType);

    private IEnumerable<string> GetDistinctValues(Func<DocumentRecord, string?> selector)
        => documents
            .Select(selector)
            .Select(NormalizeValue)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase);

    private void UpdateFilterSelection(ref string field, string? newValue)
    {
        var normalized = newValue ?? string.Empty;
        if (!string.Equals(field, normalized, StringComparison.Ordinal))
        {
            field = normalized;
            Go(1);
        }
    }

    private static string? NormalizeValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "-")
        {
            return null;
        }

        return value.Trim();
    }

    private void UpdateCatalogStatus(DocumentCatalogService.DocumentCatalogContext context)
    {
        activeDirectoryPath = string.Empty;
        statusAlertClass = null;
        statusMessage = null;

        configuredRootPath = SelectConfiguredRootPath(context);
        isStorageConfigured = !string.IsNullOrWhiteSpace(configuredRootPath) && Directory.Exists(configuredRootPath);

        var primaryPath = string.IsNullOrWhiteSpace(context.RequestedAbsolutePath)
            ? null
            : context.RequestedAbsolutePath;

        var fallbackPath = string.IsNullOrWhiteSpace(context.RelativePhysicalPath)
            ? null
            : context.RelativePhysicalPath;

        storageNoteMessage = null;

        if (!string.IsNullOrWhiteSpace(context.ActiveRootPath))
        {
            activeDirectoryPath = context.ActiveRootPath;
        }
        else if (!string.IsNullOrWhiteSpace(primaryPath))
        {
            activeDirectoryPath = primaryPath;
        }
        else if (!string.IsNullOrWhiteSpace(fallbackPath))
        {
            activeDirectoryPath = fallbackPath;
        }

        if (!string.IsNullOrWhiteSpace(context.ConnectionErrorMessage))
        {
            statusAlertClass = "alert alert-danger";
            statusMessage = $"ไม่สามารถเชื่อมต่อไปยังโฟลเดอร์หลักได้: {context.ConnectionErrorMessage}";
            return;
        }

        if (context.IsFallback && !string.IsNullOrWhiteSpace(primaryPath))
        {
            statusAlertClass = "alert alert-warning";
            statusMessage = $"ไม่พบโฟลเดอร์ \"{primaryPath}\" จึงแสดงข้อมูลจาก \"{fallbackPath ?? "ตัวอย่าง"}\" แทน";
            return;
        }

        if (!context.RootExists)
        {
            statusAlertClass = "alert alert-warning";
            if (!string.IsNullOrWhiteSpace(primaryPath))
            {
                if (ShouldShowMissingFolderBanner)
                {
                    statusMessage = $"ไม่พบโฟลเดอร์ \"{primaryPath}\" กรุณาตรวจสอบค่า DocumentCatalog.AbsolutePath ในไฟล์ appsettings";
                }
                else if (ShouldShowStorageNote)
                {
                    statusAlertClass = null;
                    statusMessage = null;
                    storageNoteMessage = $"หมายเหตุ: ไม่พบโฟลเดอร์ \"{primaryPath}\" กรุณาตรวจสอบค่า DocumentCatalog.AbsolutePath ในไฟล์ appsettings";
                }
                return;
            }
            else if (!string.IsNullOrWhiteSpace(fallbackPath))
            {
                if (ShouldShowMissingFolderBanner)
                {
                    statusMessage = $"ไม่พบโฟลเดอร์ \"{fallbackPath}\" กรุณาตรวจสอบค่า DocumentCatalog.RelativePath หรือสร้างโฟลเดอร์ดังกล่าว";
                }
                else if (ShouldShowStorageNote)
                {
                    statusAlertClass = null;
                    statusMessage = null;
                    storageNoteMessage = $"หมายเหตุ: ไม่พบโฟลเดอร์ \"{fallbackPath}\" กรุณาตรวจสอบค่า DocumentCatalog.RelativePath หรือสร้างโฟลเดอร์ดังกล่าว";
                }
                return;
            }
            else if (ShouldShowMissingFolderBanner)
            {
                statusMessage = "ยังไม่ได้กำหนดโฟลเดอร์สำหรับเก็บเอกสาร OI/WI";
                return;
            }
            else if (ShouldShowStorageNote)
            {
                statusAlertClass = null;
                statusMessage = null;
                storageNoteMessage = "หมายเหตุ: ยังไม่ได้ตั้งค่าที่เก็บไฟล์เอกสาร OI/WI แต่ยังมีข้อมูลเดิมในระบบ";
                return;
            }

            statusAlertClass = null;
            statusMessage = null;
            return;
        }
    }

    private static string? SelectConfiguredRootPath(DocumentCatalogService.DocumentCatalogContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.ActiveRootPath))
        {
            return context.ActiveRootPath;
        }

        if (!string.IsNullOrWhiteSpace(context.RequestedAbsolutePath))
        {
            return context.RequestedAbsolutePath;
        }

        if (!string.IsNullOrWhiteSpace(context.RelativePhysicalPath))
        {
            return context.RelativePhysicalPath;
        }

        return null;
    }

    private async Task RefreshNow()
    {
        using var refreshCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        try
        {
            statusAlertClass = "alert alert-info";
            statusMessage = "กำลังรีเฟรชข้อมูลจากโฟลเดอร์...";
            await InvokeAsync(StateHasChanged);

            var result = await IndexingService.RefreshIndexAsync(refreshCts.Token).ConfigureAwait(false);

            if (result.TotalChanges > 0)
            {
                statusAlertClass = "alert alert-success";
                statusMessage = $"รีเฟรชสำเร็จ: เพิ่ม {result.Added} รายการ, อัปเดต {result.Updated} รายการ, ลบ {result.Removed} รายการ.";
            }
            else
            {
                statusAlertClass = "alert alert-secondary";
                statusMessage = "ข้อมูลเป็นปัจจุบันแล้ว (ไม่มีการเปลี่ยนแปลง)";
            }
        }
        catch (OperationCanceledException)
        {
            statusAlertClass = "alert alert-warning";
            statusMessage = "ยกเลิกการรีเฟรชข้อมูล (ใช้เวลานานเกินไป)";
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to refresh OI/WI index manually.");
            statusAlertClass = "alert alert-danger";
            statusMessage = "ไม่สามารถรีเฟรชข้อมูลจากโฟลเดอร์ได้ โปรดลองอีกครั้ง";
        }
        finally
        {
            DocumentCatalog.InvalidateCache();
            await TriggerReloadAsync(refreshFilters: true);
        }
    }

    public void Dispose()
        => CancelPending();

    private static string DisplayOrDash(string? value)
        => string.IsNullOrWhiteSpace(value) || value == "-"
            ? "-"
            : value;

    private static string FormatTimestamp(DateTimeOffset? timestamp)
        => timestamp.HasValue
            ? timestamp.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : "-";

    private static string FormatStampInfo(StampMode mode, DateOnly? date)
        => StampDisplay.GetDisplayText(mode, date);

    private string? BuildViewerUrl(OiwiRow row)
    {
        if (string.IsNullOrWhiteSpace(row.FileName))
        {
            return null;
        }

        try
        {
            var token = DocumentCatalogService.EncodeDocumentToken(row.FileName);
            return $"/documents/viewer/{Uri.EscapeDataString(token)}";
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to build viewer link for document '{DocumentName}'.", row.FileName);
            return row.LinkUrl;
        }
    }

    private string? BuildEditUrl(OiwiRow row)
    {
        if (string.IsNullOrWhiteSpace(row.FileName))
        {
            return null;
        }

        try
        {
            var token = DocumentCatalogService.EncodeDocumentToken(row.FileName);
            return $"/documents/edit/{Uri.EscapeDataString(token)}";
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to build edit link for document '{DocumentName}'.", row.FileName);
            return null;
        }
    }

    private string GetCurrentRouteBase()
    {
        var relative = Nav.ToBaseRelativePath(Nav.Uri);
        var path = relative.Split('?', 2)[0];
        return string.IsNullOrWhiteSpace(path) ? "/" : "/" + path.Trim('/');
    }
}
