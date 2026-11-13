using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Health;

/// <summary>
/// The writes of a health series: each goes to <see cref="IHealthSeriesRepository{T}"/> and, once
/// committed, is broadcast as a storage event on <paramref name="collection"/>.
/// </summary>
/// <typeparam name="T">The domain record type.</typeparam>
internal sealed class HealthSeriesWriter<T>(
    IHealthSeriesRepository<T> repository,
    IDocumentProcessingService documentProcessingService,
    ISignalRBroadcastService signalRBroadcastService,
    string collection)
    where T : class, IProcessableDocument
{
    public async Task<IEnumerable<T>> CreateAsync(IEnumerable<T> records, CancellationToken ct)
    {
        var processed = documentProcessingService.ProcessDocuments(records.ToList()).ToList();
        var result = (await repository.UpsertAsync(processed, ct)).ToList();

        await signalRBroadcastService.BroadcastStorageCreateAsync(
            collection,
            new { collection, data = result, count = result.Count });
        return result;
    }

    public async Task<T?> UpdateAsync(string id, T record, CancellationToken ct)
    {
        var result = await repository.UpdateAsync(id, record, ct);
        if (result is null)
            return null;

        await signalRBroadcastService.BroadcastStorageUpdateAsync(
            collection,
            new { collection, data = result, id });
        return result;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct)
    {
        if (!await repository.DeleteAsync(id, ct))
            return false;

        await signalRBroadcastService.BroadcastStorageDeleteAsync(
            collection,
            new StorageDeleteEvent(collection, id));
        return true;
    }
}
