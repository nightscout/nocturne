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
    /// Records not written because the user had deleted them. A re-import never brings those back.
    /// </summary>
    public int SkippedDeleted { get; set; }

    /// <summary>
    /// The stored rows that held the identity of the records counted in <see cref="SkippedDeleted"/>,
    /// where the refusing path read them.
    /// </summary>
    public List<RefusedRecord> RefusedRecords { get; } = [];

    /// <summary>
    /// Legacy records of a kind Nocturne does not store, such as an entry whose type is not a
    /// sensor reading, meter reading or calibration.
    /// </summary>
    public int SkippedUnsupported { get; set; }
}

/// <summary>A record not written because the stored row <paramref name="HeldBy"/> holds its identity.</summary>
/// <param name="RecordType">The V4 record type that was refused.</param>
/// <param name="HeldBy">The id of the stored row holding the identity.</param>
public sealed record RefusedRecord(Type RecordType, Guid HeldBy);
