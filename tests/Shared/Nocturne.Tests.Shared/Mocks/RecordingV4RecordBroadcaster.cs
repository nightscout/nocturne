using Nocturne.Core.Contracts.Events;

namespace Nocturne.Tests.Shared.Mocks;

public sealed class RecordingV4RecordBroadcaster<TModel> : IV4RecordBroadcaster<TModel>
    where TModel : class
{
    public List<TModel> Created { get; } = [];
    public List<TModel> Updated { get; } = [];
    public List<Guid> Deleted { get; } = [];

    public Task BroadcastCreatedAsync(IReadOnlyList<TModel> items, CancellationToken ct = default)
    {
        Created.AddRange(items);
        return Task.CompletedTask;
    }

    public Task BroadcastUpdatedAsync(IReadOnlyList<TModel> items, CancellationToken ct = default)
    {
        Updated.AddRange(items);
        return Task.CompletedTask;
    }

    public Task BroadcastDeletedAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default)
    {
        Deleted.AddRange(ids);
        return Task.CompletedTask;
    }
}
