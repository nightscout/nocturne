namespace Nocturne.Core.Models.V4;

/// <summary>
/// Result of decomposing a legacy Entry record into v4 granular models.
/// Tracks which records were created vs updated for idempotency reporting.
/// </summary>
public class DecompositionResult
{
    /// <summary>
    /// Correlation ID linking all records produced from the same legacy record
    /// </summary>
    public Guid? CorrelationId { get; set; }

    /// <summary>
    /// Records that were newly created during decomposition
    /// </summary>
    public List<object> CreatedRecords { get; } = [];

    /// <summary>
    /// Records that already existed and were updated during decomposition.
    /// Most records implement IV4Record, but StateSpan records are also included.
    /// </summary>
    public List<object> UpdatedRecords { get; } = [];
}
