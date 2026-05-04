namespace Nocturne.Core.Models.V4;

/// <summary>
/// Groups V4 records that were decomposed from the same source record.
/// </summary>
public class DecompositionBatch
{
    public Guid Id { get; set; }
    public string Source { get; set; } = string.Empty;
    public string? SourceRecordId { get; set; }
    public DateTime CreatedAt { get; set; }
}
