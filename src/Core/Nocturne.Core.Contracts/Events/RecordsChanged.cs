using Nocturne.Core.Contracts.V4;

namespace Nocturne.Core.Contracts.Events;

/// <summary>A committed write, as one change per record type it touched.</summary>
public sealed record RecordsChanged(IReadOnlyList<RecordTypeChange> Changes)
{
    /// <summary>
    /// A write of each of <paramref name="recordTypes"/> that names no record: a store that cannot
    /// materialise what it wrote. <see cref="WriteOrigin.Backfill"/> because the change carries no
    /// record and so is announced to no client.
    /// </summary>
    public static RecordsChanged Written(IEnumerable<Type> recordTypes) =>
        new([.. recordTypes.Select(type => new RecordTypeChange(type, WriteOrigin.Backfill, [], [], []))]);

    /// <summary>A write of one record type.</summary>
    public static RecordsChanged Of<TRecord>(
        WriteOrigin origin,
        IReadOnlyList<TRecord>? created = null,
        IReadOnlyList<TRecord>? updated = null,
        IReadOnlyList<TRecord>? deleted = null) where TRecord : class =>
        new([new RecordTypeChange(typeof(TRecord), origin, created ?? [], updated ?? [], deleted ?? [])]);
}

/// <summary>
/// The records of one <paramref name="RecordType"/> a committed write created, updated or deleted.
/// Empty lists mean only that the type was written. The write's records, if it announces any, are in
/// another change.
/// </summary>
/// <remarks>
/// The records are typed by <paramref name="RecordType"/>, not by the list: a V4 record
/// (<see cref="Nocturne.Core.Models.V4.IV4Record"/>) or a document of a legacy collection that has
/// none (food, settings), or that is announced whole (a profile, a device status).
/// </remarks>
public sealed record RecordTypeChange(
    Type RecordType,
    WriteOrigin Origin,
    IReadOnlyList<object> Created,
    IReadOnlyList<object> Updated,
    IReadOnlyList<object> Deleted)
{
    public bool HasRecords => Created.Count + Updated.Count + Deleted.Count > 0;
}
