using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// PostgreSQL repository for SystemEvent operations
/// </summary>
public class SystemEventRepository : ISystemEventRepository
{
    private readonly NocturneDbContext _context;

    /// <summary>
    /// Initializes a new instance of the SystemEventRepository class
    /// </summary>
    /// <param name="context">The database context.</param>
    public SystemEventRepository(NocturneDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Get system events with optional filtering
    /// </summary>
    /// <param name="eventType">Optional event type filter.</param>
    /// <param name="category">Optional category filter.</param>
    /// <param name="from">Optional start timestamp (mills) filter.</param>
    /// <param name="to">Optional end timestamp (mills) filter.</param>
    /// <param name="source">Optional source identifier filter.</param>
    /// <param name="count">The maximum number of events to return.</param>
    /// <param name="skip">The number of events to skip.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A collection of matching system events.</returns>
    public virtual async Task<IEnumerable<SystemEvent>> GetSystemEventsAsync(
        SystemEventType? eventType = null,
        SystemEventCategory? category = null,
        long? from = null,
        long? to = null,
        string? source = null,
        int count = 100,
        int skip = 0,
        CancellationToken cancellationToken = default)
    {
        var query = _context.SystemEvents.AsQueryable();

        if (eventType.HasValue)
            query = query.Where(e => e.EventType == eventType.Value.ToString());

        if (category.HasValue)
            query = query.Where(e => e.Category == category.Value.ToString());

        if (!string.IsNullOrEmpty(source))
            query = query.Where(e => e.Source == source);

        if (from.HasValue)
            query = query.Where(e => e.Mills >= from.Value);

        if (to.HasValue)
            query = query.Where(e => e.Mills <= to.Value);

        var entities = await query
            .OrderByDescending(e => e.Mills)
            .Skip(skip)
            .Take(count)
            .ToListAsync(cancellationToken);

        return entities.Select(SystemEventMapper.ToDomainModel);
    }

    /// <summary>
    /// Get a specific system event by ID
    /// </summary>
    /// <param name="id">The unique identifier (GUID or legacy string ID).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The system event, or null if not found.</returns>
    public async Task<SystemEvent?> GetSystemEventByIdAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var entity = await _context.SystemEvents.FirstOrDefaultAsync(
            e => e.OriginalId == id,
            cancellationToken);

        if (entity == null && Guid.TryParse(id, out var guidId))
        {
            entity = await _context.SystemEvents.FirstOrDefaultAsync(
                e => e.Id == guidId,
                cancellationToken);
        }

        return entity != null ? SystemEventMapper.ToDomainModel(entity) : null;
    }

    /// <summary>
    /// Create or update a system event (upsert by originalId)
    /// </summary>
    /// <param name="systemEvent">The system event data to upsert.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The upserted system event.</returns>
    public async Task<SystemEvent> UpsertSystemEventAsync(
        SystemEvent systemEvent,
        CancellationToken cancellationToken = default)
        => (await UpsertBatchAsync([systemEvent], cancellationToken))[0];

    /// <summary>
    /// Bulk upsert system events (for connector imports)
    /// </summary>
    /// <param name="events">The collection of system events to upsert.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of events processed.</returns>
    public async Task<int> BulkUpsertAsync(
        IEnumerable<SystemEvent> events,
        CancellationToken cancellationToken = default)
        => (await UpsertBatchAsync(events.ToList(), cancellationToken)).Count;

    /// <summary>
    /// Upserts <paramref name="events"/> by <c>OriginalId</c> with one save, leaving the rows a save
    /// per event in input order would: a repeated <c>OriginalId</c> updates the row its first
    /// occurrence inserted. Every row it loaded or added is detached afterwards so a long connector
    /// sync does not pay change detection over every earlier batch; only those rows, since the scoped
    /// context may also track entities the caller still holds.
    /// </summary>
    private async Task<List<SystemEvent>> UpsertBatchAsync(
        IReadOnlyList<SystemEvent> events,
        CancellationToken cancellationToken)
    {
        if (events.Count == 0)
            return [];

        var originalIds = events
            .Select(e => e.OriginalId)
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct()
            .ToList();
        var existingRows = originalIds.Count == 0
            ? []
            : await _context.SystemEvents
                .Where(e => originalIds.Contains(e.OriginalId))
                .ToListAsync(cancellationToken);
        var loaded = new HashSet<SystemEventEntity>(existingRows, ReferenceEqualityComparer.Instance);
        var existingByOriginalId = existingRows
            .GroupBy(e => e.OriginalId!)
            .ToDictionary(g => g.Key, g => g.First());

        var written = new List<SystemEventEntity>(events.Count);
        foreach (var systemEvent in events)
        {
            var hasOriginalId = !string.IsNullOrEmpty(systemEvent.OriginalId);
            if (hasOriginalId && existingByOriginalId.TryGetValue(systemEvent.OriginalId!, out var entity))
            {
                SystemEventMapper.UpdateEntity(entity, systemEvent);
            }
            else
            {
                entity = SystemEventMapper.ToEntity(systemEvent);
                _context.SystemEvents.Add(entity);
                loaded.Add(entity);
                if (hasOriginalId)
                    existingByOriginalId[systemEvent.OriginalId!] = entity;
            }

            written.Add(entity);
        }

        await _context.SaveChangesAsync(cancellationToken);

        var results = written.Select(SystemEventMapper.ToDomainModel).ToList();
        foreach (var entity in loaded)
            _context.Entry(entity).State = EntityState.Detached;

        return results;
    }

    /// <summary>
    /// Delete a system event
    /// </summary>
    /// <param name="id">The unique identifier of the event to delete.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True if the event was deleted, otherwise false.</returns>
    public async Task<bool> DeleteSystemEventAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var entity = await _context.SystemEvents.FirstOrDefaultAsync(
            e => e.OriginalId == id,
            cancellationToken);

        if (entity == null && Guid.TryParse(id, out var guidId))
        {
            entity = await _context.SystemEvents.FirstOrDefaultAsync(
                e => e.Id == guidId,
                cancellationToken);
        }

        if (entity == null)
            return false;

        _context.SystemEvents.Remove(entity);
        var result = await _context.SaveChangesAsync(cancellationToken);
        return result > 0;
    }

    /// <summary>
    /// Delete all system events with the specified data source
    /// </summary>
    /// <param name="source">The source identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of deleted records.</returns>
    public async Task<long> DeleteBySourceAsync(
        string source,
        CancellationToken cancellationToken = default)
    {
        var deletedCount = await _context.SystemEvents
            .Where(e => e.Source == source)
            .ExecuteDeleteAsync(cancellationToken);
        return deletedCount;
    }
}
