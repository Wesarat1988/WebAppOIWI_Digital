namespace Oiwi.Data.Entities;

public class DocumentHistory
{
    public int Id { get; set; }
    public int DocumentId { get; set; }
    public Document Document { get; set; } = null!;
    public required string Action { get; set; }
    public required string ChangedBy { get; set; }
    public DateTime ChangedAt { get; set; }
    public string? Reason { get; set; }
    public string? OldDocumentCode { get; set; }
    public int? OldFileVersion { get; set; }
}
