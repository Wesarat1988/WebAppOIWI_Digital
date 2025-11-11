namespace WepAppOIWI_Digital.Services
{
    public class DocumentVersion
    {
        public required string DocKey { get; init; }          // key ปลอดภัยจาก path (เช่น base64 หรือ slug)
        public required string VersionId { get; init; }       // yyyyMMddHHmmssfff
        public required string FileName { get; init; }        // ชื่อไฟล์เดิม เช่น vibration.pdf
        public required string SavedBy { get; init; }         // คนอัปเดต
        public DateTimeOffset SavedAt { get; init; }          // เวลาเซฟ
        public long Size { get; init; }                       // ไบต์
        public string? Note { get; init; }                    // คอมเมนต์
        public string PhysicalPath { get; init; } = "";       // ที่เก็บจริง
        public string PublicUrl { get; init; } = "";          // URL สำหรับพรีวิว/ดาวน์โหลด
    }
}
