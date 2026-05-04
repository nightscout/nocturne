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
/// Unlike <see cref="SignalREntryEventSink"/>, this sink does not perform cache invalidation or
/// V4 decomposition — those responsibilities belong to <see cref="TreatmentService"/> and
/// <see cref="TreatmentReadService"/> respectively.
/// Broadcast failures are caught and logged without propagating.
/// </remarks>
/// <seealso cref="IDataEventSink{T}"/>
/// <seealso cref="ISignalRBroadcastService"/>
/// <seealso cref="TreatmentService"/>
public class SignalRTreatmentEventSink : IDataEventSink<Treatment>
{
    private readonly ISignalRBroadcastService _broadcast;
    private readonly ILogger<SignalRTreatmentEventSink> _logger;
    private const string Collection = "treatments";

    /// <summary>
    /// Initializes a new instance of <see cref="SignalRTreatmentEventSink"/>.
    /// </summary>
    /// <param name="broadcast">The SignalR broadcast service for real-time event delivery.</param>
    /// <param name="logger">The logger instance.</param>
    public SignalRTreatmentEventSink(
        ISignalRBroadcastService broadcast,
        ILogger<SignalRTreatmentEventSink> logger)
    {
        _broadcast = broadcast;
        _logger = logger;
    }

    public async Task OnCreatedAsync(Treatment treatment, CancellationToken ct)
    {
        try
        {
            await _broadcast.BroadcastStorageCreateAsync(
                Collection, new { colName = Collection, doc = treatment });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to broadcast create for treatment {Id}", treatment.Id);
        }
    }

    public async Task OnCreatedAsync(IReadOnlyList<Treatment> treatments, CancellationToken ct)
    {
        foreach (var treatment in treatments)
            await OnCreatedAsync(treatment, ct);
    }

    public async Task OnUpdatedAsync(Treatment treatment, CancellationToken ct)
    {
        try
        {
            await _broadcast.BroadcastStorageUpdateAsync(
                Collection, new { colName = Collection, doc = treatment });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to broadcast update for treatment {Id}", treatment.Id);
        }
    }

    public async Task OnDeletedAsync(Treatment? treatment, CancellationToken ct)
    {
        try
        {
            await _broadcast.BroadcastStorageDeleteAsync(
                Collection, new { colName = Collection, doc = treatment });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to broadcast delete for treatment {Id}", treatment?.Id);
        }
    }
}
