using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.API.Services.Realtime;

/// <summary>
/// Sends the documents of a <see cref="WriteOrigin.Live"/> write to their legacy storage collection, as
/// the NS v3 socket names them: creates, then updates, then deletes. It covers the collections whose
/// write is published as the legacy document itself: food and settings, which have no V4 record, and
/// profiles and device statuses, which are written as several V4 records and announced whole.
/// </summary>
/// <remarks>
/// A settings document under a reserved key (<see cref="SettingsKeys.IsReserved"/>) is Nocturne's own
/// configuration and outside the v3 collection, so it is never sent. A change that deletes more than
/// <see cref="AuditedBulkDeleteExtensions.BroadcastMaterializationCap"/> documents is sent as one
/// <see cref="StorageBulkDeleteEvent"/> rather than one event per document.
/// </remarks>
internal sealed class LegacyStorageRecordChangeSubscriber(ISignalRBroadcastService broadcast) : IRecordChangeSubscriber
{
    internal static readonly IReadOnlyDictionary<Type, string> Collections = new Dictionary<Type, string>
    {
        [typeof(Food)] = RealtimeCategories.Food,
        [typeof(Settings)] = RealtimeCategories.Settings,
        [typeof(Profile)] = RealtimeCategories.Profiles,
        [typeof(DeviceStatus)] = RealtimeCategories.DeviceStatus,
    };

    public RecordChangeStage Stage => RecordChangeStage.Announce;

    public async Task OnChangedAsync(RecordsChanged batch, CancellationToken ct)
    {
        foreach (var change in batch.Changes)
        {
            if (change.Origin != WriteOrigin.Live || !Collections.TryGetValue(change.RecordType, out var collection))
                continue;

            foreach (var document in change.Created.Where(InCollection))
                await broadcast.BroadcastStorageCreateAsync(collection, new StorageDocumentEvent(collection, document));

            foreach (var document in change.Updated.Where(InCollection))
                await broadcast.BroadcastStorageUpdateAsync(collection, new StorageDocumentEvent(collection, document));

            var deleted = change.Deleted.Where(InCollection).ToList();
            if (deleted.Count > AuditedBulkDeleteExtensions.BroadcastMaterializationCap)
            {
                await broadcast.BroadcastStorageDeleteAsync(collection, new StorageBulkDeleteEvent(collection, deleted.Count));
                continue;
            }

            foreach (var document in deleted)
                await broadcast.BroadcastStorageDeleteAsync(
                    collection, new StorageDeleteEvent(collection, (document as IProcessableDocument)?.Id, document));
        }
    }

    private static bool InCollection(object document) =>
        document is not Settings settings || !SettingsKeys.IsReserved(settings.Key);
}
