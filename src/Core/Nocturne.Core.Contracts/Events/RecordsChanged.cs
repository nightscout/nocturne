using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.V4;

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
}

/// <summary>
/// The records of one <paramref name="RecordType"/> a committed write created, updated or deleted.
/// Empty lists mean only that the type was written. The write's records, if it announces any, are in
/// another change.
/// </summary>
public sealed record RecordTypeChange(
    Type RecordType,
    WriteOrigin Origin,
    IReadOnlyList<IV4Record> Created,
    IReadOnlyList<IV4Record> Updated,
    IReadOnlyList<IV4Record> Deleted)
{
    public bool HasRecords => Created.Count + Updated.Count + Deleted.Count > 0;
}
