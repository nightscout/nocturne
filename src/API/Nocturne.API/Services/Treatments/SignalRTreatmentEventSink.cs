using Nocturne.API.Services.Entries;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Models;
using Nocturne.API.Services.Realtime;

namespace Nocturne.API.Services.Treatments;

/// <summary>
/// <see cref="IDataEventSink{T}"/> implementation for <see cref="Treatment"/> that forwards
/// write lifecycle events (created, updated, deleted) to <see cref="ISignalRBroadcastService"/>
/// as real-time storage events in the <c>treatments</c> collection group.
/// </summary>
/// <remarks>
/// This sink does not perform cache invalidation or V4 decomposition: the repositories clear the
/// legacy read caches (<see cref="Nocturne.Core.Contracts.V4.ILegacyReadCacheInvalidation"/>), and
/// <see cref="TreatmentReadService"/> decomposes.
/// </remarks>
/// <seealso cref="IDataEventSink{T}"/>
/// <seealso cref="ISignalRBroadcastService"/>
/// <seealso cref="TreatmentService"/>
internal class SignalRTreatmentEventSink : IDataEventSink<Treatment>
{
    private readonly ISignalRBroadcastService _broadcast;
    private const string Collection = "treatments";

    /// <summary>
    /// Initializes a new instance of <see cref="SignalRTreatmentEventSink"/>.
    /// </summary>
    /// <param name="broadcast">The SignalR broadcast service for real-time event delivery.</param>
    public SignalRTreatmentEventSink(ISignalRBroadcastService broadcast)
    {
        _broadcast = broadcast;
    }

    public async Task OnCreatedAsync(Treatment treatment, CancellationToken ct)
    {
        await _broadcast.BroadcastStorageCreateAsync(
            Collection, new StorageDocumentEvent(Collection, treatment));
    }

    public async Task OnCreatedAsync(IReadOnlyList<Treatment> treatments, CancellationToken ct)
    {
        foreach (var treatment in treatments)
            await OnCreatedAsync(treatment, ct);
    }

    public async Task OnUpdatedAsync(Treatment treatment, CancellationToken ct)
    {
        await _broadcast.BroadcastStorageUpdateAsync(
            Collection, new StorageDocumentEvent(Collection, treatment));
    }

    public async Task OnDeletedAsync(Treatment? treatment, CancellationToken ct)
    {
        await _broadcast.BroadcastStorageDeleteAsync(
            Collection, new StorageDeleteEvent(Collection, treatment?.Id, treatment));
    }
}
