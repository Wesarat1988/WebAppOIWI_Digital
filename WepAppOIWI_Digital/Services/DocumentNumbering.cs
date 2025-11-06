using System;
using System.Collections.Generic;
using System.Linq;

namespace WepAppOIWI_Digital.Services;

internal static class DocumentNumbering
{
    public const string DocumentTypeOi = "OI";
    public const string DocumentTypeWi = "WI";

    public static IReadOnlyList<string> KnownTypes { get; } = new[]
    {
        DocumentTypeOi,
        DocumentTypeWi
    };

    public static string NormalizeType(string? documentType)
    {
        if (string.IsNullOrWhiteSpace(documentType))
        {
            return string.Empty;
        }

        var normalized = documentType.Trim().ToUpperInvariant();
        return normalized;
    }

    public static bool IsKnownType(string? documentType)
    {
        if (string.IsNullOrEmpty(documentType))
        {
            return false;
        }

        return KnownTypes.Contains(documentType, StringComparer.OrdinalIgnoreCase);
    }

    public static string? FormatCode(string? documentType, int? sequenceNumber)
    {
        var normalizedType = NormalizeType(documentType);
        if (string.IsNullOrEmpty(normalizedType) || sequenceNumber is null || sequenceNumber <= 0)
        {
            return null;
        }

        return $"{normalizedType}-{sequenceNumber.Value:0000}";
    }
}
