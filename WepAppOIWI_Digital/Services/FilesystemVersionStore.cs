using System.Text;
using System.Text.Json;

namespace WepAppOIWI_Digital.Services
{
    public sealed class FilesystemVersionStore : IVersionStore
    {
        private readonly IWebHostEnvironment _env;
        private readonly JsonSerializerOptions _jsonOpts = new(JsonSerializerDefaults.Web);

        public FilesystemVersionStore(IWebHostEnvironment env) => _env = env;

        private string GetDocKey(string normalizedPath)
        {
            // ทำ key ปลอดภัยจาก path: base64url แบบสั้น
            var bytes = Encoding.UTF8.GetBytes(normalizedPath);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private string VersionsRoot
            => Path.Combine(_env.WebRootPath, "documents", ".versions");

        public async Task<DocumentVersion> SaveAsync(string normalizedPath, string fileName, Stream currentFileStream,
                                                     string savedBy, string? note, CancellationToken ct = default)
        {
            Directory.CreateDirectory(VersionsRoot);
            var key = GetDocKey(normalizedPath);
            var docDir = Path.Combine(VersionsRoot, key);
            Directory.CreateDirectory(docDir);

            var versionId = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");
            var verDir = Path.Combine(docDir, versionId);
            Directory.CreateDirectory(verDir);

            var targetPath = Path.Combine(verDir, fileName);
            await using (var fs = File.Create(targetPath))
            {
                await currentFileStream.CopyToAsync(fs, ct);
            }

            var info = new FileInfo(targetPath);

            var ver = new DocumentVersion
            {
                DocKey = key,
                VersionId = versionId,
                FileName = fileName,
                SavedAt = DateTimeOffset.UtcNow,
                SavedBy = savedBy,
                Size = info.Length,
                Note = note,
                PhysicalPath = targetPath,
                PublicUrl = $"/documents/.versions/{key}/{versionId}/{Uri.EscapeDataString(fileName)}"
            };

            // เก็บ metadata เป็น JSON
            var metaPath = Path.Combine(verDir, "version.json");
            await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(ver, _jsonOpts), ct);

            return ver;
        }

        public async Task<IReadOnlyList<DocumentVersion>> ListAsync(string normalizedPath, int take = 5, CancellationToken ct = default)
        {
            var key = GetDocKey(normalizedPath);
            var docDir = Path.Combine(VersionsRoot, key);
            if (!Directory.Exists(docDir)) return Array.Empty<DocumentVersion>();

            var list = new List<DocumentVersion>();
            foreach (var verDir in Directory.EnumerateDirectories(docDir).OrderByDescending(p => p))
            {
                var meta = Path.Combine(verDir, "version.json");
                if (File.Exists(meta))
                {
                    var json = await File.ReadAllTextAsync(meta, ct);
                    var v = JsonSerializer.Deserialize<DocumentVersion>(json, _jsonOpts);
                    if (v != null) list.Add(v);
                }
                if (list.Count >= take) break;
            }
            return list;
        }

        public async Task<bool> RestoreAsync(DocumentVersion version, string livePhysicalPath, CancellationToken ct = default)
        {
            if (!File.Exists(version.PhysicalPath)) return false;
            var liveDir = Path.GetDirectoryName(livePhysicalPath)!;
            Directory.CreateDirectory(liveDir);

            await using var src = File.OpenRead(version.PhysicalPath);
            await using var dst = File.Create(livePhysicalPath);
            await src.CopyToAsync(dst, ct);
            return true;
        }
    }
}
