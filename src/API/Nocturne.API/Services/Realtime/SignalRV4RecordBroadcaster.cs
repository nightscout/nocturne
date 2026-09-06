using Nocturne.Core.Contracts.Events;

namespace Nocturne.API.Services.Realtime;

/// <summary>
/// SignalR adapter for <see cref="IV4RecordBroadcaster{TModel}"/>: broadcasts native V4 record shapes to
/// the V4 category group with a <c>recordType</c> discriminator. Registered as an open generic so every
/// V4 model type resolves; types absent from <see cref="V4BroadcastMap"/> (the dormant <c>therapy</c>
/// family) broadcast nothing.
/// </summary>
/// <typeparam name="TModel">The V4 domain model type being broadcast.</typeparam>
public sealed class SignalRV4RecordBroadcaster<TModel>(ISignalRBroadcastService broadcast)
    : IV4RecordBroadcaster<TModel>
    where TModel : class
{
    public async Task BroadcastCreatedAsync(IReadOnlyList<TModel> items, CancellationToken ct = default)
    {
        if (items.Count == 0 || !V4BroadcastMap.TryGet(typeof(TModel), out var category, out var recordType))
            return;

        foreach (var item in items)
            await broadcast.BroadcastStorageCreateAsync(category, new { recordType, doc = item });
    }

    public async Task BroadcastUpdatedAsync(IReadOnlyList<TModel> items, CancellationToken ct = default)
    {
        if (items.Count == 0 || !V4BroadcastMap.TryGet(typeof(TModel), out var category, out var recordType))
            return;

        foreach (var item in items)
            await broadcast.BroadcastStorageUpdateAsync(category, new { recordType, doc = item });
    }

    public async Task BroadcastDeletedAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0 || !V4BroadcastMap.TryGet(typeof(TModel), out var category, out var recordType))
            return;

        foreach (var id in ids)
            await broadcast.BroadcastStorageDeleteAsync(category, new { recordType, id });
    }
}
