using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Tests.Shared.Mocks;

/// <summary>
/// The <typeparamref name="TDocument"/> documents of the <see cref="WriteOrigin.Live"/> changes a write
/// published: what the legacy storage subscriber announces for the collection.
/// </summary>
public sealed class RecordingDocumentChanges<TDocument> : RecordingRecordChanges
    where TDocument : class
{
    public IReadOnlyList<TDocument> Created => Live(change => change.Created);

    public IReadOnlyList<TDocument> Updated => Live(change => change.Updated);

    public IReadOnlyList<TDocument> Deleted => Live(change => change.Deleted);

    private IReadOnlyList<TDocument> Live(Func<RecordTypeChange, IReadOnlyList<object>> documents) =>
    [
        .. Changes
            .Where(change => change.Origin == WriteOrigin.Live && change.RecordType == typeof(TDocument))
            .SelectMany(documents)
            .Cast<TDocument>(),
    ];
}
