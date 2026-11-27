using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Realtime;

/// <summary>
/// Sends the records of a <see cref="WriteOrigin.Live"/> write to their V4 category group with a
/// <c>recordType</c> discriminator: creates, then updates, then deleted ids. A type absent from
/// <see cref="V4BroadcastMap"/> (the dormant <c>therapy</c> family) sends nothing.
/// </summary>
/// <remarks>
/// <c>doc</c> is typed <see cref="object"/> so the hub serialises the record by its runtime type: a
/// property typed <see cref="Nocturne.Core.Models.V4.IV4Record"/> would serialise only the interface's
/// members.
/// </remarks>
internal sealed class SignalRRecordChangeSubscriber(ISignalRBroadcastService broadcast) : IRecordChangeSubscriber
{
    public RecordChangeStage Stage => RecordChangeStage.Announce;

    public async Task OnChangedAsync(RecordsChanged batch, CancellationToken ct)
    {
        foreach (var change in batch.Changes)
        {
            if (change.Origin != WriteOrigin.Live
                || !change.HasRecords
                || !V4BroadcastMap.TryGet(change.RecordType, out var category, out var recordType))
                continue;

            foreach (var record in change.Created)
                await broadcast.BroadcastStorageCreateAsync(category, new { colName = category, recordType, doc = (object)record });

            foreach (var record in change.Updated)
                await broadcast.BroadcastStorageUpdateAsync(category, new { colName = category, recordType, doc = (object)record });

            foreach (var record in change.Deleted.Cast<IV4Record>())
                await broadcast.BroadcastStorageDeleteAsync(category, new { colName = category, recordType, id = record.Id });
        }
    }
}
