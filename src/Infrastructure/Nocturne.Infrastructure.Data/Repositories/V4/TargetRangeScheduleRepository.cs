using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing target range schedules in the database.
/// </summary>
public class TargetRangeScheduleRepository : ITargetRangeScheduleRepository
{
    private readonly NocturneDbContext _context;
    private readonly ILogger<TargetRangeScheduleRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TargetRangeScheduleRepository"/> class.
    /// </summary>
    /// <param name="context">The database context.</param>
    /// <param name="logger">The logger instance.</param>
    public TargetRangeScheduleRepository(NocturneDbContext context, ILogger<TargetRangeScheduleRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Gets target range schedules based on filter criteria.
    /// </summary>
    /// <param name="from">Optional start timestamp filter.</param>
    /// <param name="to">Optional end timestamp filter.</param>
    /// <param name="device">Optional device filter.</param>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="limit">The maximum number of records to return.</param>
    /// <param name="offset">The number of records to skip.</param>
    /// <param name="descending">Whether to sort by timestamp in descending order.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of target range schedules.</returns>
    public async Task<IEnumerable<TargetRangeSchedule>> GetAsync(
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
        var query = _context.TargetRangeSchedules.AsNoTracking().AsQueryable();
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
        return entities.Select(TargetRangeScheduleMapper.ToDomainModel);
    }

    /// <summary>
    /// Gets a target range schedule by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The target range schedule, or null if not found.</returns>
    public async Task<TargetRangeSchedule?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.TargetRangeSchedules.FindAsync([id], ct);
        return entity is null ? null : TargetRangeScheduleMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Gets a target range schedule by its legacy (MongoDB) identifier.
    /// </summary>
    /// <param name="legacyId">The legacy identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The target range schedule, or null if not found.</returns>
    public async Task<TargetRangeSchedule?> GetByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        var entity = await _context.TargetRangeSchedules.FirstOrDefaultAsync(e => e.LegacyId == legacyId, ct);
        return entity is null ? null : TargetRangeScheduleMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Gets target range schedules by profile name.
    /// </summary>
    /// <param name="profileName">The name of the profile.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of target range schedules.</returns>
    public async Task<IEnumerable<TargetRangeSchedule>> GetByProfileNameAsync(
        string profileName,
        CancellationToken ct = default
    )
    {
        var entities = await _context
            .TargetRangeSchedules.AsNoTracking()
            .Where(e => e.ProfileName == profileName)
            .OrderByDescending(e => e.Timestamp)
            .ToListAsync(ct);
        return entities.Select(TargetRangeScheduleMapper.ToDomainModel);
    }

    /// <summary>
    /// Gets the most recent target range schedule for a profile that was active at-or-before the given timestamp.
    /// </summary>
    /// <param name="profileName">The name of the profile.</param>
    /// <param name="timestamp">The point-in-time to query against.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The matching target range schedule, or null if none found.</returns>
    public async Task<TargetRangeSchedule?> GetActiveAtAsync(
        string profileName, DateTime timestamp, CancellationToken ct = default)
    {
        var entity = await _context.TargetRangeSchedules
            .AsNoTracking()
            .Where(e => e.ProfileName == profileName && e.Timestamp <= timestamp)
            .OrderByDescending(e => e.Timestamp)
            .FirstOrDefaultAsync(ct);

        return entity is null ? null : TargetRangeScheduleMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Creates a new target range schedule.
    /// </summary>
    /// <param name="model">The target range schedule to create.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The created target range schedule.</returns>
    public async Task<TargetRangeSchedule> CreateAsync(TargetRangeSchedule model, CancellationToken ct = default)
    {
        var entity = TargetRangeScheduleMapper.ToEntity(model);
        _context.TargetRangeSchedules.Add(entity);
        await _context.SaveChangesAsync(ct);
        return TargetRangeScheduleMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Updates an existing target range schedule.
    /// </summary>
    /// <param name="id">The unique identifier of the schedule to update.</param>
    /// <param name="model">The updated schedule data.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The updated target range schedule.</returns>
    public async Task<TargetRangeSchedule> UpdateAsync(
        Guid id,
        TargetRangeSchedule model,
        CancellationToken ct = default
    )
    {
        var entity =
            await _context.TargetRangeSchedules.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"TargetRangeSchedule {id} not found");
        TargetRangeScheduleMapper.UpdateEntity(entity, model);
        await _context.SaveChangesAsync(ct);
        return TargetRangeScheduleMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Deletes a target range schedule by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity =
            await _context.TargetRangeSchedules.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"TargetRangeSchedule {id} not found");
        _context.TargetRangeSchedules.Remove(entity);
        await _context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Deletes target range schedules by legacy identifier.
    /// </summary>
    /// <param name="legacyId">The legacy identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The number of deleted records.</returns>
    public async Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        return await _context.TargetRangeSchedules.Where(e => e.LegacyId == legacyId).ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Deletes target range schedules by legacy identifier prefix.
    /// </summary>
    /// <param name="prefix">The legacy identifier prefix.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The number of deleted records.</returns>
    public async Task<int> DeleteByLegacyIdPrefixAsync(string prefix, CancellationToken ct = default)
    {
        return await _context
            .TargetRangeSchedules.Where(e => e.LegacyId != null && e.LegacyId.StartsWith(prefix))
            .ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Counts target range schedules within a timestamp range.
    /// </summary>
    /// <param name="from">Optional start timestamp filter.</param>
    /// <param name="to">Optional end timestamp filter.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The count of matching records.</returns>
    public async Task<int> CountAsync(DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        var query = _context.TargetRangeSchedules.AsNoTracking().AsQueryable();
        if (from.HasValue)
            query = query.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue)
            query = query.Where(e => e.Timestamp <= to.Value);
        return await query.CountAsync(ct);
    }

    /// <summary>
    /// Gets target range schedules by correlation identifier.
    /// </summary>
    /// <param name="correlationId">The correlation identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of target range schedules.</returns>
    public async Task<IEnumerable<TargetRangeSchedule>> GetByCorrelationIdAsync(
        Guid correlationId,
        CancellationToken ct = default
    )
    {
        var entities = await _context
            .TargetRangeSchedules.AsNoTracking()
            .Where(e => e.CorrelationId == correlationId)
            .ToListAsync(ct);
        return entities.Select(TargetRangeScheduleMapper.ToDomainModel);
    }

    /// <summary>
    /// Perfroms a bulk creation of target range schedules, handling deduplication.
    /// </summary>
    /// <param name="records">The collection of records to create.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of created schedules.</returns>
    public async Task<IEnumerable<TargetRangeSchedule>> BulkCreateAsync(
        IEnumerable<TargetRangeSchedule> records,
        CancellationToken ct = default
    )
    {
        var entities = records.Select(TargetRangeScheduleMapper.ToEntity).ToList();
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
                .TargetRangeSchedules.AsNoTracking()
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
            _context.TargetRangeSchedules.AddRange(batch);
            await _context.SaveChangesAsync(ct);
            _context.ChangeTracker.Clear();
        }

        return entities.Select(TargetRangeScheduleMapper.ToDomainModel);
    }
}
