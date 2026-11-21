using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Events;

/// <summary>A committed write, as one change per record type it touched.</summary>
public sealed record RecordsChanged(IReadOnlyList<RecordTypeChange> Changes);

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
