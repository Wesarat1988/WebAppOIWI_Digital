namespace Oiwi.Data.Entities;

public class DocumentFile
{
    public int Id { get; set; }
    public int DocumentId { get; set; }
    public Document Document { get; set; } = null!;
    public int VersionNo { get; set; }
    public required string FileName { get; set; }
    public required string RelativePath { get; set; }
    public long SizeBytes { get; set; }
    public DateTime EffectiveDate { get; set; }
    public bool IsActive { get; set; }
}
