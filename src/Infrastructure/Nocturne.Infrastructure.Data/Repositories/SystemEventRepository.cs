using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// PostgreSQL repository for SystemEvent operations
/// </summary>
public class SystemEventRepository : ISystemEventRepository
{
    private const int MaxUpsertAttempts = 3;

    private readonly INocturneDbContextFactory _contexts;

    /// <summary>
    /// Initializes a new instance of the SystemEventRepository class
    /// </summary>
    public SystemEventRepository(INocturneDbContextFactory contexts)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
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
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var query = db.SystemEvents.AsQueryable();

        if (eventType.HasValue)
            query = query.Where(e => e.EventType == eventType.Value.ToString());

        if (category.HasValue)
            query = query.Where(e => e.Category == category.Value.ToString());

        if (!string.IsNullOrEmpty(source))
            query = query.Where(e => e.Source == source);

        query = query.InWindow(e => e.Mills, from, to);

        var entities = await query
            .OrderByDescending(e => e.Mills)
            .Skip(skip)
            .Take(count)
            .ToListAsync(cancellationToken);

        return entities.Select(SystemEventMapper.ToDomainModel);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SystemEvent>> GetInRangeAsync(long from, long to, int limit, CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var entities = await db.SystemEvents.AsNoTracking()
            .Where(e => e.Mills >= from && e.Mills < to)
            .OrderBy(e => e.Mills).ThenBy(e => e.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
        return [.. entities.Select(SystemEventMapper.ToDomainModel)];
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
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var entity = await db.SystemEvents.FirstOrDefaultAsync(
            e => e.OriginalId == id,
            cancellationToken);

        if (entity == null && Guid.TryParse(id, out var guidId))
        {
            entity = await db.SystemEvents.FirstOrDefaultAsync(
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
    /// occurrence inserted.
    /// </summary>
    private async Task<List<SystemEvent>> UpsertBatchAsync(
        IReadOnlyList<SystemEvent> events,
        CancellationToken cancellationToken)
    {
        if (events.Count == 0)
            return [];

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await SaveBatchAsync(events, cancellationToken);
            }
            catch (DbUpdateException ex) when (attempt < MaxUpsertAttempts && ex.IsUniqueViolation())
            {
                // A concurrent upsert committed one of these keys between the read and the save, and the
                // save wrote nothing. The next attempt reads its row and updates it.
            }
        }
    }

    private async Task<List<SystemEvent>> SaveBatchAsync(
        IReadOnlyList<SystemEvent> events,
        CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);

        var originalIds = events
            .Select(e => e.OriginalId)
            .OfType<string>()
            .Where(id => id.Length > 0)
            .Distinct()
            .ToList();
        var existingRows = originalIds.Count == 0
            ? []
            : await db.SystemEvents
                .Where(e => originalIds.Contains(e.OriginalId!))
                .ToListAsync(cancellationToken);
        var hidden = await HiddenRowsAsync(db, originalIds, existingRows, cancellationToken);
        existingRows.AddRange(hidden);
        var existingByOriginalId = existingRows
            .ToDictionary(e => e.OriginalId!);

        var written = new List<(SystemEventEntity Row, SystemEvent Sent, bool Hidden)>(events.Count);
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
                db.SystemEvents.Add(entity);
                if (hasOriginalId)
                    existingByOriginalId[systemEvent.OriginalId!] = entity;
            }

            written.Add((entity, systemEvent, hidden.Contains(entity)));
        }

        if (hidden.Count == 0)
            await db.SaveChangesAsync(cancellationToken);
        else
            await db.WriteWithHistoryClampLiftedAsync(async token => await db.SaveChangesAsync(token), cancellationToken);

        return written.Select(w => w.Hidden
            ? SystemEventMapper.ToDomainModel(w.Row, createdAt: w.Sent.CreatedAt)
            : SystemEventMapper.ToDomainModel(w.Row)).ToList();
    }

    /// <summary>
    /// The rows of <paramref name="originalIds"/> the unique <c>(tenant, original_id)</c> index holds that
    /// a history-clamped writer cannot see, tracked for the update the writer's event makes of them. The
    /// index spans the whole tenant, so an insert beside one fails with 23505 however the clamp reads. The
    /// event overwrites every field the answer carries, so the answer is the writer's own event
    /// (<see cref="SystemEventMapper.ToDomainModel(SystemEventEntity, DateTime?)"/>) and reveals nothing the
    /// clamp hides.
    /// </summary>
    private static async Task<List<SystemEventEntity>> HiddenRowsAsync(
        NocturneDbContext db,
        List<string> originalIds,
        List<SystemEventEntity> visible,
        CancellationToken cancellationToken)
    {
        if (!db.HistoryClamped)
            return [];

        var unseen = originalIds.Except(visible.Select(e => e.OriginalId!)).ToList();
        if (unseen.Count == 0)
            return [];

        return await db.WriteWithHistoryClampLiftedAsync(
            token => db.SystemEvents.Where(e => unseen.Contains(e.OriginalId!)).ToListAsync(token),
            cancellationToken);
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
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var entity = await db.SystemEvents.FirstOrDefaultAsync(
            e => e.OriginalId == id,
            cancellationToken);

        if (entity == null && Guid.TryParse(id, out var guidId))
        {
            entity = await db.SystemEvents.FirstOrDefaultAsync(
                e => e.Id == guidId,
                cancellationToken);
        }

        if (entity == null)
            return false;

        db.SystemEvents.Remove(entity);
        var result = await db.SaveChangesAsync(cancellationToken);
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
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var deletedCount = await db.SystemEvents
            .Where(e => e.Source == source)
            .ExecuteDeleteAsync(cancellationToken);
        return deletedCount;
    }
}
