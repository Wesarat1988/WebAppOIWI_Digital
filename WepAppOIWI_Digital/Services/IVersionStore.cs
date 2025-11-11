namespace WepAppOIWI_Digital.Services
{
    public interface IVersionStore
    {
        Task<DocumentVersion> SaveAsync(string normalizedPath, string fileName, Stream currentFileStream,
                                string savedBy, string? note, CancellationToken ct = default);

        Task<IReadOnlyList<DocumentVersion>> ListAsync(string normalizedPath, int take = 5, CancellationToken ct = default);

        Task<bool> RestoreAsync(DocumentVersion version, string livePhysicalPath, CancellationToken ct = default);
    }
}
