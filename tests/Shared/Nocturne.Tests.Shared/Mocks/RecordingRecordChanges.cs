using Nocturne.Core.Contracts.Events;

namespace Nocturne.Tests.Shared.Mocks;

/// <summary>Records every batch a write published, in publish order.</summary>
public class RecordingRecordChanges : IRecordChangePublisher
{
    private readonly List<RecordsChanged> _batches = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<RecordsChanged> Batches
    {
        get
        {
            lock (_gate)
                return [.. _batches];
        }
    }

    public IReadOnlyList<RecordTypeChange> Changes => [.. Batches.SelectMany(batch => batch.Changes)];

    public Task PublishAsync(RecordsChanged batch, CancellationToken ct)
    {
        lock (_gate)
            _batches.Add(batch);
        return Task.CompletedTask;
    }

    public void Clear()
    {
        lock (_gate)
            _batches.Clear();
    }
}
