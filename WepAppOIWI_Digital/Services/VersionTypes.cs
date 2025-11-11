using System;

namespace WepAppOIWI_Digital.Services;

public sealed record VersionDescriptor(
    string VersionId,
    DateTimeOffset TimestampUtc,
    string? Actor,
    string? Comment,
    long? SizeBytes,
    string? PublicUrl = null,
    bool IsActive = false
);

public sealed record VersionSnapshotHandle(
    VersionDescriptor Descriptor,
    string FilePath,
    string FileName
);

public sealed record HistoryItem(
    string VersionId,
    string VersionLabel,
    DateTimeOffset TimestampUtc,
    string? AuthorName,
    string? Comment,
    double? SizeKb,
    bool IsActive
);

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    int Page,
    int PageSize
);
