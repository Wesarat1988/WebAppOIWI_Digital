using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace WepAppOIWI_Digital.Services;

internal static class DocumentNumbering
{
    // ---------- Types ----------
    public const string DocumentTypeOi = "OI";
    public const string DocumentTypeWi = "WI";

    /// <summary>รายการประเภทที่รองรับ (เรียงตามต้องการแสดงผล)</summary>
    public static IReadOnlyList<string> KnownTypes { get; } =
        new[] { DocumentTypeOi, DocumentTypeWi };

    /// <summary>ชุดสำหรับตรวจเร็ว (ไม่แคร์ตัวพิมพ์)</summary>
    private static readonly HashSet<string> KnownTypeSet =
        new(HashSetComparer);

    private static readonly IEqualityComparer<string> HashSetComparer =
        StringComparer.OrdinalIgnoreCase;

    static DocumentNumbering()
    {
        foreach (var t in KnownTypes)
            KnownTypeSet.Add(t);
    }

    // ---------- Normalization ----------
    /// <summary>แปลงให้เป็นตัวพิมพ์ใหญ่ ตัดช่องว่างหัวท้าย ถ้าค่าว่างคืน ""</summary>
    public static string NormalizeType(string? documentType)
        => string.IsNullOrWhiteSpace(documentType)
            ? string.Empty
            : documentType.Trim().ToUpperInvariant();

    /// <summary>คืนค่าแบบ normalize และถ้าไม่รู้จักให้เป็นค่าเริ่มต้น (OI)</summary>
    public static string NormalizeTypeOrDefault(string? documentType)
    {
        var n = NormalizeType(documentType);
        return IsKnownType(n) ? n : DocumentTypeOi;
    }

    public static bool IsKnownType(string? documentType)
        => !string.IsNullOrWhiteSpace(documentType) && KnownTypeSet.Contains(documentType!);

    public static bool IsOi(string? documentType)
        => string.Equals(NormalizeType(documentType), DocumentTypeOi, StringComparison.Ordinal);

    public static bool IsWi(string? documentType)
        => string.Equals(NormalizeType(documentType), DocumentTypeWi, StringComparison.Ordinal);

    // ---------- Code formatting ----------
    /// <summary>คืนรูปแบบรหัส: TYPE-0001 (ถ้าข้อมูลไม่ครบ คืน null)</summary>
    public static string? FormatCode(string? documentType, int? sequenceNumber)
    {
        var normalizedType = NormalizeType(documentType);
        if (!IsKnownType(normalizedType) || sequenceNumber is null || sequenceNumber <= 0)
            return null;

        return $"{normalizedType}-{sequenceNumber.Value:0000}";
    }

    // ---------- Parsing ----------
    private static readonly Regex CodeRegex =
        new(@"^(?<type>[A-Za-z]{2})-(?<seq>\d{4})$", RegexOptions.Compiled);

    /// <summary>พยายามอ่านรหัสรูปแบบ TYPE-0001</summary>
    public static bool TryParseCode(string? code, out string type, out int sequenceNumber)
    {
        type = string.Empty;
        sequenceNumber = 0;

        if (string.IsNullOrWhiteSpace(code)) return false;

        var m = CodeRegex.Match(code.Trim());
        if (!m.Success) return false;

        type = NormalizeType(m.Groups["type"].Value);
        if (!IsKnownType(type)) return false;

        if (!int.TryParse(m.Groups["seq"].Value, out sequenceNumber)) return false;

        return sequenceNumber > 0;
    }

    // ---------- Utilities ----------
    /// <summary>
    /// หาลำดับถัดไปจากรายการรหัสเดิม ๆ (เฉพาะชนิดที่ระบุ)
    /// </summary>
    public static int GetNextSequence(IEnumerable<string> existingCodes, string documentType)
    {
        var type = NormalizeTypeOrDefault(documentType);

        var max = existingCodes?
            .Select(c => TryParseCode(c, out var t, out var n) && string.Equals(t, type, StringComparison.OrdinalIgnoreCase) ? n : 0)
            .DefaultIfEmpty(0)
            .Max() ?? 0;

        return max + 1;
    }
}
