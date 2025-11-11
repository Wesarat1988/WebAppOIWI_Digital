namespace WepAppOIWI_Digital.Services;

public sealed record VersionDescriptor(
    string VersionId,
    DateTimeOffset TimestampUtc,
    string? Actor,
    string? Comment,
    long? SizeBytes
);
