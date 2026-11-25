using Nocturne.Core.Contracts.Events;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <inheritdoc cref="IRecordChangePublisher"/>
internal sealed class RecordChangePublisher(IEnumerable<IRecordChangeSubscriber> subscribers) : IRecordChangePublisher
{
    private readonly IRecordChangeSubscriber[] _inStageOrder = [.. subscribers.OrderBy(s => s.Stage)];

    public async Task PublishAsync(RecordsChanged batch, CancellationToken ct)
    {
        foreach (var subscriber in _inStageOrder)
            await subscriber.OnChangedAsync(batch, ct);
    }
}
