using System;

namespace WepAppOIWI_Digital.Services;

public sealed class SetupStateCache
{
    private string? cachedJson;

    public string? CachedJson => cachedJson;

    public void Save(string? json)
    {
        cachedJson = json;
    }

    public void Clear()
    {
        cachedJson = null;
    }
}
