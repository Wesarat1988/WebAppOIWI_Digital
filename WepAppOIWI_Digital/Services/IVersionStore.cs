using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WepAppOIWI_Digital.Services;

public interface IVersionStore
{
    Task<IReadOnlyList<VersionDescriptor>> ListAsync(string normalizedPath, int take = 5, CancellationToken ct = default);

    Task<VersionSnapshotHandle?> TryGetAsync(string normalizedPath, string versionId, CancellationToken ct = default);

    Task<bool> SnapshotAsync(
        string normalizedPath,
        string physicalPath,
        string? actor,
        string? comment,
        CancellationToken ct = default);

    Task<bool> RestoreAsync(
        string normalizedPath,
        string versionId,
        string physicalPath,
        string? actor,
        string? comment,
        CancellationToken ct = default);
}
