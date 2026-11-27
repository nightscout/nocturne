using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.V4;

namespace Nocturne.Tests.Shared.Mocks;

/// <summary>
/// The <typeparamref name="TModel"/> records of the <see cref="WriteOrigin.Live"/> changes a write
/// published: what the SignalR subscriber announces for the type.
/// </summary>
public sealed class RecordingV4RecordBroadcaster<TModel> : RecordingRecordChanges
    where TModel : class, IV4Record
{
    public IReadOnlyList<TModel> Created => Live(change => change.Created);

    public IReadOnlyList<TModel> Updated => Live(change => change.Updated);

    public IReadOnlyList<Guid> Deleted => [.. Live(change => change.Deleted).Select(record => record.Id)];

    private IReadOnlyList<TModel> Live(Func<RecordTypeChange, IReadOnlyList<object>> records) =>
    [
        .. Changes
            .Where(change => change.Origin == WriteOrigin.Live && change.RecordType == typeof(TModel))
            .SelectMany(records)
            .Cast<TModel>(),
    ];
}
