using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing device event records in the database.
/// Includes support for cross-connector deduplication.
/// </summary>
public class DeviceEventRepository : IDeviceEventRepository
{
    private readonly NocturneDbContext _context;
    private readonly IDeduplicationService _deduplicationService;
    private readonly IAuditContext _auditContext;
    private readonly ILogger<DeviceEventRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceEventRepository"/> class.
    /// </summary>
    /// <param name="context">The database context.</param>
    /// <param name="deduplicationService">The deduplication service.</param>
    /// <param name="auditContext">The audit context for tracking mutations.</param>
    /// <param name="logger">The logger instance.</param>
    public DeviceEventRepository(
        NocturneDbContext context,
        IDeduplicationService deduplicationService,
        IAuditContext auditContext,
        ILogger<DeviceEventRepository> logger)
    {
        _context = context;
        _deduplicationService = deduplicationService;
        _auditContext = auditContext;
        _logger = logger;
    }

    /// <summary>
    /// Gets device event records based on filter criteria.
    /// Deduplicates records using the <see cref="IDeduplicationService"/>.
    /// </summary>
    /// <param name="from">Optional start timestamp filter.</param>
    /// <param name="to">Optional end timestamp filter.</param>
    /// <param name="device">Optional device filter.</param>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="limit">The maximum number of records to return.</param>
    /// <param name="offset">The number of records to skip.</param>
    /// <param name="descending">Whether to sort by timestamp in descending order.</param>
    /// <param name="nativeOnly">Whether to return only native records.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of device events.</returns>
    public async Task<IEnumerable<DeviceEvent>> GetAsync(
        DateTime? from,
        DateTime? to,
        string? device,
        string? source,
        int limit = 100,
        int offset = 0,
        bool descending = true,
        bool nativeOnly = false,
        CancellationToken ct = default
    )
    {
        var query = _context.DeviceEvents.AsNoTracking().AsQueryable();
        if (from.HasValue)
            query = query.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue)
            query = query.Where(e => e.Timestamp <= to.Value);
        if (device != null)
            query = query.Where(e => e.Device == device);
        if (source != null)
            query = query.Where(e => e.DataSource == source);
        if (nativeOnly)
            query = query.Where(e => e.LegacyId == null);

        // Exclude non-primary duplicates from cross-connector deduplication
        var nonPrimaryIds = _context.LinkedRecords
            .Where(lr => lr.RecordType == "deviceevent" && !lr.IsPrimary)
            .Select(lr => lr.RecordId);
        query = query.Where(b => !nonPrimaryIds.Contains(b.Id));

        query = descending ? query.OrderByDescending(e => e.Timestamp) : query.OrderBy(e => e.Timestamp);
        var entities = await query.Skip(offset).Take(limit).ToListAsync(ct);
        return entities.Select(DeviceEventMapper.ToDomainModel);
    }

    /// <summary>
    /// Gets a device event record by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The device event, or null if not found.</returns>
    public async Task<DeviceEvent?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.DeviceEvents.FindAsync([id], ct);
        return entity is null ? null : DeviceEventMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Gets a device event record by its legacy (MongoDB) identifier.
    /// </summary>
    /// <param name="legacyId">The legacy identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The device event, or null if not found.</returns>
    public async Task<DeviceEvent?> GetByLegacyIdAsync(
        string legacyId,
        CancellationToken ct = default
    )
    {
        var entity = await _context.DeviceEvents.FirstOrDefaultAsync(
            e => e.LegacyId == legacyId,
            ct
        );
        return entity is null ? null : DeviceEventMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Creates a new device event record.
    /// </summary>
    /// <param name="model">The device event to create.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The created device event.</returns>
    public async Task<DeviceEvent> CreateAsync(DeviceEvent model, CancellationToken ct = default)
    {
        var entity = DeviceEventMapper.ToEntity(model);
        _context.DeviceEvents.Add(entity);
        await _context.SaveChangesAsync(ct);
        return DeviceEventMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Updates an existing device event record.
    /// </summary>
    /// <param name="id">The unique identifier of the record to update.</param>
    /// <param name="model">The updated record data.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The updated device event.</returns>
    public async Task<DeviceEvent> UpdateAsync(
        Guid id,
        DeviceEvent model,
        CancellationToken ct = default
    )
    {
        var entity =
            await _context.DeviceEvents.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"DeviceEvent {id} not found");
        DeviceEventMapper.UpdateEntity(entity, model);
        await _context.SaveChangesAsync(ct);
        return DeviceEventMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Deletes a device event record by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity =
            await _context.DeviceEvents.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"DeviceEvent {id} not found");
        _context.DeviceEvents.Remove(entity);
        await _context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Counts device event records within a timestamp range.
    /// </summary>
    /// <param name="from">Optional start timestamp filter.</param>
    /// <param name="to">Optional end timestamp filter.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The count of matching records.</returns>
    public async Task<int> CountAsync(DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        var query = _context.DeviceEvents.AsNoTracking().AsQueryable();
        if (from.HasValue)
            query = query.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue)
            query = query.Where(e => e.Timestamp <= to.Value);
        return await query.CountAsync(ct);
    }

    /// <summary>
    /// Gets device event records by correlation identifier.
    /// </summary>
    /// <param name="correlationId">The correlation identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of device events.</returns>
    public async Task<IEnumerable<DeviceEvent>> GetByCorrelationIdAsync(
        Guid correlationId,
        CancellationToken ct = default
    )
    {
        var entities = await _context
            .DeviceEvents.AsNoTracking()
            .Where(e => e.CorrelationId == correlationId)
            .ToListAsync(ct);
        return entities.Select(DeviceEventMapper.ToDomainModel);
    }

    /// <summary>
    /// Deletes a device event record by its legacy identifier.
    /// </summary>
    /// <param name="legacyId">The legacy identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The number of deleted records.</returns>
    public async Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        return await _context.AuditedExecuteDeleteAsync(
            _context.DeviceEvents.Where(e => e.LegacyId == legacyId), _auditContext, ct);
    }

    /// <summary>
    /// Deletes device event records matching the given data source and sync identifier.
    /// </summary>
    /// <param name="dataSource">The external data source name.</param>
    /// <param name="syncIdentifier">The external sync identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The number of deleted records.</returns>
    public async Task<int> DeleteBySyncIdentifierAsync(string dataSource, string syncIdentifier, CancellationToken ct = default)
    {
        return await _context.AuditedExecuteDeleteAsync(
            _context.DeviceEvents.Where(e => e.DataSource == dataSource && e.SyncIdentifier == syncIdentifier),
            _auditContext, ct);
    }

    /// <summary>
    /// Performs a bulk creation of device event records, handling deduplication.
    /// </summary>
    /// <param name="records">The collection of records to create.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of created records.</returns>
    public async Task<IEnumerable<DeviceEvent>> BulkCreateAsync(
        IEnumerable<DeviceEvent> records,
        CancellationToken ct = default
    )
    {
        var entities = records.Select(DeviceEventMapper.ToEntity).ToList();
        if (entities.Count == 0)
            return [];

        // Batch-level dedup: keep first occurrence per LegacyId
        entities = entities
            .GroupBy(e => e.LegacyId ?? e.Id.ToString())
            .Select(g => g.First())
            .ToList();

        // DB-level dedup: filter out records whose LegacyId already exists
        var legacyIds = entities
            .Where(e => !string.IsNullOrEmpty(e.LegacyId))
            .Select(e => e.LegacyId!)
            .ToHashSet();

        if (legacyIds.Count > 0)
        {
            var existingIds = await _context
                .DeviceEvents.AsNoTracking()
                .Where(e => legacyIds.Contains(e.LegacyId!))
                .Select(e => e.LegacyId)
                .ToListAsync(ct);

            var existingSet = existingIds.ToHashSet();
            entities = entities
                .Where(e => string.IsNullOrEmpty(e.LegacyId) || !existingSet.Contains(e.LegacyId))
                .ToList();
        }

        if (entities.Count == 0)
            return [];

        const int batchSize = 500;
        foreach (var batch in entities.Chunk(batchSize))
        {
            _context.DeviceEvents.AddRange(batch);
            await _context.SaveChangesAsync(ct);
            _context.ChangeTracker.Clear();
        }

        // Insert-time deduplication: link saved records to canonical groups
        foreach (var entity in entities)
        {
            try
            {
                var criteria = new MatchCriteria
                {
                    EventType = entity.EventType
                };

                var canonicalId = await _deduplicationService.GetOrCreateCanonicalIdAsync(
                    RecordType.DeviceEvent,
                    new DateTimeOffset(entity.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                    criteria,
                    ct);

                await _deduplicationService.LinkRecordAsync(
                    canonicalId,
                    RecordType.DeviceEvent,
                    entity.Id,
                    new DateTimeOffset(entity.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                    entity.DataSource ?? "unknown",
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deduplicate DeviceEvent {Id}", entity.Id);
            }
        }

        return entities.Select(DeviceEventMapper.ToDomainModel);
    }

    /// <summary>
    /// Gets the latest device event of a specific type.
    /// </summary>
    /// <param name="eventType">The type of device event.</param>
    /// <param name="asOf">Optional upper bound on event timestamp; <c>null</c> means latest.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The latest device event, or null if none found.</returns>
    public async Task<DeviceEvent?> GetLatestByEventTypeAsync(DeviceEventType eventType, DateTime? asOf, CancellationToken ct = default)
    {
        var eventTypeString = eventType.ToString();
        var query = _context.DeviceEvents
            .AsNoTracking()
            .Where(e => e.EventType == eventTypeString);
        if (asOf is { } cutoff)
            query = query.Where(e => e.Timestamp <= cutoff);

        var entity = await query
            .OrderByDescending(e => e.Timestamp)
            .FirstOrDefaultAsync(ct);

        return entity is null ? null : DeviceEventMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Gets the latest device event from a set of event types.
    /// </summary>
    /// <param name="eventTypes">The types of device events to search for.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The latest device event, or null if none found.</returns>
    public async Task<DeviceEvent?> GetLatestByEventTypesAsync(DeviceEventType[] eventTypes, CancellationToken ct = default)
    {
        var eventTypeStrings = eventTypes.Select(t => t.ToString()).ToList();
        var entity = await _context.DeviceEvents
            .AsNoTracking()
            .Where(e => eventTypeStrings.Contains(e.EventType))
            .OrderByDescending(e => e.Timestamp)
            .FirstOrDefaultAsync(ct);

        return entity is null ? null : DeviceEventMapper.ToDomainModel(entity);
    }
}
