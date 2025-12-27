using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// PostgreSQL repository for SystemEvent operations
/// </summary>
public class SystemEventRepository
{
    private readonly NocturneDbContext _context;

    /// <summary>
    /// Initializes a new instance of the SystemEventRepository class
    /// </summary>
    public SystemEventRepository(NocturneDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Get system events with optional filtering
    /// </summary>
    public async Task<IEnumerable<SystemEvent>> GetSystemEventsAsync(
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
    public async Task<SystemEvent> UpsertSystemEventAsync(
        SystemEvent systemEvent,
        CancellationToken cancellationToken = default)
    {
        SystemEventEntity? entity = null;

        // Check for existing by originalId
        if (!string.IsNullOrEmpty(systemEvent.OriginalId))
        {
            entity = await _context.SystemEvents.FirstOrDefaultAsync(
                e => e.OriginalId == systemEvent.OriginalId,
                cancellationToken);
        }

        if (entity != null)
        {
            // Update existing
            entity.EventType = systemEvent.EventType.ToString();
            entity.Category = systemEvent.Category.ToString();
            entity.Code = systemEvent.Code;
            entity.Description = systemEvent.Description;
            entity.Mills = systemEvent.Mills;
            entity.Source = systemEvent.Source;
            entity.MetadataJson = systemEvent.Metadata != null
                ? System.Text.Json.JsonSerializer.Serialize(systemEvent.Metadata)
                : null;
        }
        else
        {
            entity = SystemEventMapper.ToEntity(systemEvent);
            _context.SystemEvents.Add(entity);
        }

        await _context.SaveChangesAsync(cancellationToken);
        return SystemEventMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Bulk upsert system events (for connector imports)
    /// </summary>
    public async Task<int> BulkUpsertAsync(
        IEnumerable<SystemEvent> events,
        CancellationToken cancellationToken = default)
    {
        var count = 0;
        foreach (var evt in events)
        {
            await UpsertSystemEventAsync(evt, cancellationToken);
            count++;
        }
        return count;
    }

    /// <summary>
    /// Delete a system event
    /// </summary>
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
