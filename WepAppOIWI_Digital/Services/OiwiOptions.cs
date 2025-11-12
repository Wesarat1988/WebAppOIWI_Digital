using System;
using System.Collections.Generic;

namespace WepAppOIWI_Digital.Services;

public sealed class OiwiOptions
{
    public string SharePath { get; set; } = string.Empty;
    public int ScanIntervalSeconds { get; set; } = 180;
    public int BatchSize { get; set; } = 500;
    public int MaxParallelDirs { get; set; } = 4;
    public IReadOnlyList<string> IncludeExtensions { get; set; } = Array.Empty<string>();
}
