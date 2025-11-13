namespace Oiwi.Data.Entities;

public class Document
{
    public int Id { get; set; }
    public required string DocumentCode { get; set; }
    public required string DocumentType { get; set; }
    public required string Title { get; set; }
    public string? Line { get; set; }
    public string? Station { get; set; }
    public string? Model { get; set; }
    public string? MachineName { get; set; }
    public string? Comment { get; set; }
    public required string UploadedBy { get; set; }
    public DateTime UploadedAt { get; set; }
    public DateTime? LastUpdatedAt { get; set; }

    public List<DocumentFile> Files { get; set; } = new();
    public List<DocumentHistory> Histories { get; set; } = new();
    public StampInfo? StampInfo { get; set; }
}
