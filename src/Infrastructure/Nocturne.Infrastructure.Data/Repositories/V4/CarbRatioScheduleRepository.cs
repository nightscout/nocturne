using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing carbohydrate ratio schedules in the database.
/// </summary>
public class CarbRatioScheduleRepository : ICarbRatioScheduleRepository
{
    private readonly NocturneDbContext _context;
    private readonly ILogger<CarbRatioScheduleRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CarbRatioScheduleRepository"/> class.
    /// </summary>
    /// <param name="context">The database context.</param>
    /// <param name="logger">The logger instance.</param>
    public CarbRatioScheduleRepository(NocturneDbContext context, ILogger<CarbRatioScheduleRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Gets carbohydrate ratio schedules based on filter criteria.
    /// </summary>
    /// <param name="from">Optional start timestamp filter.</param>
    /// <param name="to">Optional end timestamp filter.</param>
    /// <param name="device">Optional device filter.</param>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="limit">The maximum number of records to return.</param>
    /// <param name="offset">The number of records to skip.</param>
    /// <param name="descending">Whether to sort by timestamp in descending order.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of carbohydrate ratio schedules.</returns>
    public async Task<IEnumerable<CarbRatioSchedule>> GetAsync(
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
        var query = _context.CarbRatioSchedules.AsNoTracking().AsQueryable();
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
        return entities.Select(CarbRatioScheduleMapper.ToDomainModel);
    }

    /// <summary>
    /// Gets a carbohydrate ratio schedule by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The carbohydrate ratio schedule, or null if not found.</returns>
    public async Task<CarbRatioSchedule?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.CarbRatioSchedules.FindAsync([id], ct);
        return entity is null ? null : CarbRatioScheduleMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Gets a carbohydrate ratio schedule by its legacy (MongoDB) identifier.
    /// </summary>
    /// <param name="legacyId">The legacy identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The carbohydrate ratio schedule, or null if not found.</returns>
    public async Task<CarbRatioSchedule?> GetByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        var entity = await _context.CarbRatioSchedules.FirstOrDefaultAsync(e => e.LegacyId == legacyId, ct);
        return entity is null ? null : CarbRatioScheduleMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Gets carbohydrate ratio schedules by profile name.
    /// </summary>
    /// <param name="profileName">The name of the profile.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of carbohydrate ratio schedules.</returns>
    public async Task<IEnumerable<CarbRatioSchedule>> GetByProfileNameAsync(
        string profileName,
        CancellationToken ct = default
    )
    {
        var entities = await _context
            .CarbRatioSchedules.AsNoTracking()
            .Where(e => e.ProfileName == profileName)
            .OrderByDescending(e => e.Timestamp)
            .ToListAsync(ct);
        return entities.Select(CarbRatioScheduleMapper.ToDomainModel);
    }

    /// <summary>
    /// Gets the most recent carb ratio schedule for a profile that was active at-or-before the given timestamp.
    /// </summary>
    /// <param name="profileName">The name of the profile.</param>
    /// <param name="timestamp">The point-in-time to query against.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The matching carb ratio schedule, or null if none found.</returns>
    public async Task<CarbRatioSchedule?> GetActiveAtAsync(
        string profileName, DateTime timestamp, CancellationToken ct = default)
    {
        var entity = await _context.CarbRatioSchedules
            .AsNoTracking()
            .Where(e => e.ProfileName == profileName && e.Timestamp <= timestamp)
            .OrderByDescending(e => e.Timestamp)
            .FirstOrDefaultAsync(ct);

        return entity is null ? null : CarbRatioScheduleMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Creates a new carbohydrate ratio schedule record.
    /// </summary>
    /// <param name="model">The carbohydrate ratio schedule to create.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The created carbohydrate ratio schedule.</returns>
    public async Task<CarbRatioSchedule> CreateAsync(CarbRatioSchedule model, CancellationToken ct = default)
    {
        var entity = CarbRatioScheduleMapper.ToEntity(model);
        _context.CarbRatioSchedules.Add(entity);
        await _context.SaveChangesAsync(ct);
        return CarbRatioScheduleMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Updates an existing carbohydrate ratio schedule record.
    /// </summary>
    /// <param name="id">The unique identifier of the schedule to update.</param>
    /// <param name="model">The updated schedule data.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The updated carbohydrate ratio schedule.</returns>
    public async Task<CarbRatioSchedule> UpdateAsync(Guid id, CarbRatioSchedule model, CancellationToken ct = default)
    {
        var entity =
            await _context.CarbRatioSchedules.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"CarbRatioSchedule {id} not found");
        CarbRatioScheduleMapper.UpdateEntity(entity, model);
        await _context.SaveChangesAsync(ct);
        return CarbRatioScheduleMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Deletes a carbohydrate ratio schedule record by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity =
            await _context.CarbRatioSchedules.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"CarbRatioSchedule {id} not found");
        _context.CarbRatioSchedules.Remove(entity);
        await _context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Deletes a carbohydrate ratio schedule record by its legacy identifier.
    /// </summary>
    /// <param name="legacyId">The legacy identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The number of deleted records.</returns>
    public async Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        return await _context.CarbRatioSchedules.Where(e => e.LegacyId == legacyId).ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Deletes carbohydrate ratio schedule records by legacy identifier prefix.
    /// </summary>
    /// <param name="prefix">The legacy identifier prefix.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The number of deleted records.</returns>
    public async Task<int> DeleteByLegacyIdPrefixAsync(string prefix, CancellationToken ct = default)
    {
        return await _context
            .CarbRatioSchedules.Where(e => e.LegacyId != null && e.LegacyId.StartsWith(prefix))
            .ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Counts carbohydrate ratio schedule records within a timestamp range.
    /// </summary>
    /// <param name="from">Optional start timestamp filter.</param>
    /// <param name="to">Optional end timestamp filter.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The count of matching records.</returns>
    public async Task<int> CountAsync(DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        var query = _context.CarbRatioSchedules.AsNoTracking().AsQueryable();
        if (from.HasValue)
            query = query.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue)
            query = query.Where(e => e.Timestamp <= to.Value);
        return await query.CountAsync(ct);
    }

    /// <summary>
    /// Gets carbohydrate ratio schedule records by correlation identifier.
    /// </summary>
    /// <param name="correlationId">The correlation identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of carbohydrate ratio schedules.</returns>
    public async Task<IEnumerable<CarbRatioSchedule>> GetByCorrelationIdAsync(
        Guid correlationId,
        CancellationToken ct = default
    )
    {
        var entities = await _context
            .CarbRatioSchedules.AsNoTracking()
            .Where(e => e.CorrelationId == correlationId)
            .ToListAsync(ct);
        return entities.Select(CarbRatioScheduleMapper.ToDomainModel);
    }

    /// <summary>
    /// Performs a bulk creation of carbohydrate ratio schedule records, handling deduplication.
    /// </summary>
    /// <param name="records">The collection of records to create.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of created schedules.</returns>
    public async Task<IEnumerable<CarbRatioSchedule>> BulkCreateAsync(
        IEnumerable<CarbRatioSchedule> records,
        CancellationToken ct = default
    )
    {
        var entities = records.Select(CarbRatioScheduleMapper.ToEntity).ToList();
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
                .CarbRatioSchedules.AsNoTracking()
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
            _context.CarbRatioSchedules.AddRange(batch);
            await _context.SaveChangesAsync(ct);
            _context.ChangeTracker.Clear();
        }

        return entities.Select(CarbRatioScheduleMapper.ToDomainModel);
    }
}
