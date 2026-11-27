using Nocturne.Core.Contracts.Events;

namespace Nocturne.API.Tests.TestDoubles;

/// <summary>Hands each batch to <paramref name="subscribers"/> in stage order, as the registered publisher does.</summary>
internal sealed class SubscribersPublisher(params IRecordChangeSubscriber[] subscribers) : IRecordChangePublisher
{
    public async Task PublishAsync(RecordsChanged batch, CancellationToken ct)
    {
        foreach (var subscriber in subscribers.OrderBy(s => s.Stage))
            await subscriber.OnChangedAsync(batch, ct);
    }
}
