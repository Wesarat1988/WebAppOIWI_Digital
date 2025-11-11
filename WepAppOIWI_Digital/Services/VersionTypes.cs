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
