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
    /// <remarks>
    /// A batch mints one per source record, never one per batch: the devicestatus projection,
    /// meal pairing and Linked Records all group by it. A batch result carries its first record's.
    /// </remarks>
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
    /// Records whose stored row already held what the decomposition produced, so nothing was
    /// written for them.
    /// </summary>
    public List<object> UnchangedRecords { get; } = [];

    /// <summary>Every record the decomposition leaves stored: created, updated or unchanged.</summary>
    public IEnumerable<object> StoredRecords => CreatedRecords.Concat(UpdatedRecords).Concat(UnchangedRecords);

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
