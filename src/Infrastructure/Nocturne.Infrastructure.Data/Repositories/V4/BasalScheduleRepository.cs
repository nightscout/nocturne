using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing basal schedules in the database.
/// </summary>
public class BasalScheduleRepository : IBasalScheduleRepository
{
    private readonly NocturneDbContext _context;
    private readonly IAuditContext _auditContext;
    private readonly ILogger<BasalScheduleRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BasalScheduleRepository"/> class.
    /// </summary>
    /// <param name="context">The database context.</param>
    /// <param name="auditContext">The audit context for tracking mutations.</param>
    /// <param name="logger">The logger instance.</param>
    public BasalScheduleRepository(NocturneDbContext context, IAuditContext auditContext, ILogger<BasalScheduleRepository> logger)
    {
        _context = context;
        _auditContext = auditContext;
        _logger = logger;
    }

    /// <summary>
    /// Gets basal schedules based on filter criteria.
    /// </summary>
    /// <param name="from">Optional start timestamp filter.</param>
    /// <param name="to">Optional end timestamp filter.</param>
    /// <param name="device">Optional device filter.</param>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="limit">The maximum number of records to return.</param>
    /// <param name="offset">The number of records to skip.</param>
    /// <param name="descending">Whether to sort by timestamp in descending order.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of basal schedules.</returns>
    public async Task<IEnumerable<BasalSchedule>> GetAsync(
        DateTime? from,
        DateTime? to,
        string? device,
        string? source,
        int limit = 100,
        int offset = 0,
        bool descending = true,
        CancellationToken ct = default
    )
    {
        var query = _context.BasalSchedules.AsNoTracking().AsQueryable();
        if (from.HasValue)
            query = query.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue)
            query = query.Where(e => e.Timestamp <= to.Value);
        if (device != null)
            query = query.Where(e => e.Device == device);
        if (source != null)
            query = query.Where(e => e.DataSource == source);
        query = descending ? query.OrderByDescending(e => e.Timestamp) : query.OrderBy(e => e.Timestamp);
        var entities = await query.Skip(offset).Take(limit).ToListAsync(ct);
        return entities.Select(BasalScheduleMapper.ToDomainModel);
    }

    /// <summary>
    /// Gets a basal schedule by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The basal schedule, or null if not found.</returns>
    public async Task<BasalSchedule?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.BasalSchedules.FindAsync([id], ct);
        return entity is null ? null : BasalScheduleMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Gets a basal schedule by its legacy (MongoDB) identifier.
    /// </summary>
    /// <param name="legacyId">The legacy identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The basal schedule, or null if not found.</returns>
    public async Task<BasalSchedule?> GetByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        var entity = await _context.BasalSchedules.FirstOrDefaultAsync(e => e.LegacyId == legacyId, ct);
        return entity is null ? null : BasalScheduleMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Gets basal schedules by profile name.
    /// </summary>
    /// <param name="profileName">The name of the profile.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of basal schedules.</returns>
    public async Task<IEnumerable<BasalSchedule>> GetByProfileNameAsync(
        string profileName,
        CancellationToken ct = default
    )
    {
        var entities = await _context
            .BasalSchedules.AsNoTracking()
            .Where(e => e.ProfileName == profileName)
            .OrderByDescending(e => e.Timestamp)
            .ToListAsync(ct);
        return entities.Select(BasalScheduleMapper.ToDomainModel);
    }

    /// <summary>
    /// Gets the most recent basal schedule for a profile that was active at-or-before the given timestamp.
    /// </summary>
    /// <param name="profileName">The name of the profile.</param>
    /// <param name="timestamp">The point-in-time to query against.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The matching basal schedule, or null if none found.</returns>
    public async Task<BasalSchedule?> GetActiveAtAsync(
        string profileName, DateTime timestamp, CancellationToken ct = default)
    {
        var entity = await _context.BasalSchedules
            .AsNoTracking()
            .Where(e => e.ProfileName == profileName && e.Timestamp <= timestamp)
            .OrderByDescending(e => e.Timestamp)
            .FirstOrDefaultAsync(ct);

        return entity is null ? null : BasalScheduleMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Creates a new basal schedule record.
    /// </summary>
    /// <param name="model">The basal schedule to create.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The created basal schedule.</returns>
    public async Task<BasalSchedule> CreateAsync(BasalSchedule model, CancellationToken ct = default)
    {
        var entity = BasalScheduleMapper.ToEntity(model);
        _context.BasalSchedules.Add(entity);
        await _context.SaveChangesAsync(ct);
        return BasalScheduleMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Updates an existing basal schedule record.
    /// </summary>
    /// <param name="id">The unique identifier of the schedule to update.</param>
    /// <param name="model">The updated schedule data.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The updated basal schedule.</returns>
    public async Task<BasalSchedule> UpdateAsync(Guid id, BasalSchedule model, CancellationToken ct = default)
    {
        var entity =
            await _context.BasalSchedules.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"BasalSchedule {id} not found");
        BasalScheduleMapper.UpdateEntity(entity, model);
        await _context.SaveChangesAsync(ct);
        return BasalScheduleMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Deletes a basal schedule record by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity =
            await _context.BasalSchedules.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"BasalSchedule {id} not found");
        _context.BasalSchedules.Remove(entity);
        await _context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Deletes a basal schedule record by its legacy identifier.
    /// </summary>
    /// <param name="legacyId">The legacy identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The number of deleted records.</returns>
    public async Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        return await _context.AuditedExecuteDeleteAsync(
            _context.BasalSchedules.Where(e => e.LegacyId == legacyId), _auditContext, ct);
    }

    /// <summary>
    /// Deletes basal schedule records by legacy identifier prefix.
    /// </summary>
    /// <param name="prefix">The legacy identifier prefix.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The number of deleted records.</returns>
    public async Task<int> DeleteByLegacyIdPrefixAsync(string prefix, CancellationToken ct = default)
    {
        return await _context.AuditedExecuteDeleteAsync(
            _context.BasalSchedules.Where(e => e.LegacyId != null && e.LegacyId.StartsWith(prefix)),
            _auditContext, ct);
    }

    /// <summary>
    /// Counts basal schedule records within a timestamp range.
    /// </summary>
    /// <param name="from">Optional start timestamp filter.</param>
    /// <param name="to">Optional end timestamp filter.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The count of matching records.</returns>
    public async Task<int> CountAsync(DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        var query = _context.BasalSchedules.AsNoTracking().AsQueryable();
        if (from.HasValue)
            query = query.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue)
            query = query.Where(e => e.Timestamp <= to.Value);
        return await query.CountAsync(ct);
    }

    /// <summary>
    /// Gets basal schedule records by correlation identifier.
    /// </summary>
    /// <param name="correlationId">The correlation identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of basal schedules.</returns>
    public async Task<IEnumerable<BasalSchedule>> GetByCorrelationIdAsync(
        Guid correlationId,
        CancellationToken ct = default
    )
    {
        var entities = await _context
            .BasalSchedules.AsNoTracking()
            .Where(e => e.CorrelationId == correlationId)
            .ToListAsync(ct);
        return entities.Select(BasalScheduleMapper.ToDomainModel);
    }

    /// <summary>
    /// Performs a bulk creation of basal schedule records, handling deduplication.
    /// </summary>
    /// <param name="records">The collection of records to create.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of created schedules.</returns>
    public async Task<IEnumerable<BasalSchedule>> BulkCreateAsync(
        IEnumerable<BasalSchedule> records,
        CancellationToken ct = default
    )
    {
        var entities = records.Select(BasalScheduleMapper.ToEntity).ToList();
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
                .BasalSchedules.AsNoTracking()
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
            _context.BasalSchedules.AddRange(batch);
            await _context.SaveChangesAsync(ct);
            _context.ChangeTracker.Clear();
        }

        return entities.Select(BasalScheduleMapper.ToDomainModel);
    }
}
