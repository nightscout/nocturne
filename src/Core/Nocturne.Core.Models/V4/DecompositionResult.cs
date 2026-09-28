namespace Nocturne.Core.Models.V4;

/// <summary>
/// Result of decomposing a single legacy record (e.g., <see cref="Treatment"/>, <see cref="Entry"/>,
/// <see cref="DeviceStatus"/>) into one or more V4 granular models.
/// Tracks which records were created versus updated for idempotency reporting.
/// </summary>
/// <seealso cref="BatchDecompositionResult"/>
/// <seealso cref="IV4Record"/>
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

    /// <summary>
    /// Records not written because the user had deleted them. A re-import never brings those back.
    /// </summary>
    public int SkippedDeleted { get; set; }

    /// <summary>
    /// Legacy records of a kind Nocturne does not store, such as an entry whose type is not a
    /// sensor reading, meter reading or calibration.
    /// </summary>
    public int SkippedUnsupported { get; set; }
}
