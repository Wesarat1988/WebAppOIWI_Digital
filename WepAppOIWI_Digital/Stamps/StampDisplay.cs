using System;
using System.Globalization;

namespace WepAppOIWI_Digital.Stamps;

public static class StampDisplay
{
    private const string MasterControlLabel = "Master Control วันที่ปั้ม IE/PE DCFAN";
    private const string ValidUnitLabel = "VALID UNIT วันที่หมดอายุ TEMPORARY";

    public static string GetDisplayText(StampMode mode, DateOnly? date)
        => mode switch
        {
            StampMode.MasterControl => FormatWithDate(MasterControlLabel, date),
            StampMode.ValidUnitTemporary => FormatWithDate(ValidUnitLabel, date),
            _ => "-",
        };

    private static string FormatWithDate(string label, DateOnly? date)
    {
        if (date is null)
        {
            return label;
        }

        return $"{label}: {date.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}";
    }
}
