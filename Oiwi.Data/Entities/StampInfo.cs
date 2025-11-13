namespace Oiwi.Data.Entities;

public class StampInfo
{
    public int Id { get; set; }
    public int DocumentId { get; set; }
    public Document Document { get; set; } = null!;
    public required string Mode { get; set; }
    public DateTime StampDate { get; set; }
}
