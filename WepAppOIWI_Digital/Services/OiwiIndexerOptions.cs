namespace WepAppOIWI_Digital.Services;

public sealed class OiwiIndexerOptions
{
    public bool Enabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 300;
}
