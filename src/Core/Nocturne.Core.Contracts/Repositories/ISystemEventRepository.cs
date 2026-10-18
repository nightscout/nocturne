using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Repositories;

/// <summary>
/// Repository port for <see cref="SystemEvent"/> operations.
/// </summary>
/// <seealso cref="SystemEvent"/>
/// <seealso cref="SystemEventType"/>
/// <seealso cref="SystemEventCategory"/>
public interface ISystemEventRepository
{
    Task<IEnumerable<SystemEvent>> GetSystemEventsAsync(
        SystemEventType? eventType = null,
        SystemEventCategory? category = null,
        long? from = null,
        long? to = null,
        string? source = null,
        int count = 100,
        int skip = 0,
        CancellationToken cancellationToken = default);

    Task<SystemEvent?> GetSystemEventByIdAsync(
        string id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Create or update a system event (upsert by originalId).
    /// </summary>
    Task<SystemEvent> UpsertSystemEventAsync(
        SystemEvent systemEvent,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Bulk upsert system events (for connector imports).
    /// </summary>
    Task<int> BulkUpsertAsync(
        IEnumerable<SystemEvent> events,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteSystemEventAsync(
        string id,
        CancellationToken cancellationToken = default);

    Task<long> DeleteBySourceAsync(
        string source,
        CancellationToken cancellationToken = default);
}
