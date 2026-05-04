using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing insulin sensitivity schedules (ISF) in the database.
/// </summary>
public class SensitivityScheduleRepository : ISensitivityScheduleRepository
{
    private readonly NocturneDbContext _context;
    private readonly ILogger<SensitivityScheduleRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SensitivityScheduleRepository"/> class.
    /// </summary>
    /// <param name="context">The database context.</param>
    /// <param name="logger">The logger instance.</param>
    public SensitivityScheduleRepository(NocturneDbContext context, ILogger<SensitivityScheduleRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Gets insulin sensitivity schedules based on filter criteria.
    /// </summary>
    /// <param name="from">Optional start timestamp filter.</param>
    /// <param name="to">Optional end timestamp filter.</param>
    /// <param name="device">Optional device filter.</param>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="limit">The maximum number of records to return.</param>
    /// <param name="offset">The number of records to skip.</param>
    /// <param name="descending">Whether to sort by timestamp in descending order.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of insulin sensitivity schedules.</returns>
    public async Task<IEnumerable<SensitivitySchedule>> GetAsync(
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
        var query = _context.SensitivitySchedules.AsNoTracking().AsQueryable();
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
        return entities.Select(SensitivityScheduleMapper.ToDomainModel);
    }

    /// <summary>
    /// Gets an insulin sensitivity schedule record by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The insulin sensitivity schedule, or null if not found.</returns>
    public async Task<SensitivitySchedule?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.SensitivitySchedules.FindAsync([id], ct);
        return entity is null ? null : SensitivityScheduleMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Gets an insulin sensitivity schedule record by its legacy identifier.
    /// </summary>
    /// <param name="legacyId">The legacy identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The insulin sensitivity schedule, or null if not found.</returns>
    public async Task<SensitivitySchedule?> GetByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        var entity = await _context.SensitivitySchedules.FirstOrDefaultAsync(e => e.LegacyId == legacyId, ct);
        return entity is null ? null : SensitivityScheduleMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Gets insulin sensitivity schedule records by profile name.
    /// </summary>
    /// <param name="profileName">The name of the profile.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of matching schedules.</returns>
    public async Task<IEnumerable<SensitivitySchedule>> GetByProfileNameAsync(
        string profileName,
        CancellationToken ct = default
    )
    {
        var entities = await _context
            .SensitivitySchedules.AsNoTracking()
            .Where(e => e.ProfileName == profileName)
            .OrderByDescending(e => e.Timestamp)
            .ToListAsync(ct);
        return entities.Select(SensitivityScheduleMapper.ToDomainModel);
    }

    /// <summary>
    /// Gets the most recent sensitivity schedule for a profile that was active at-or-before the given timestamp.
    /// </summary>
    /// <param name="profileName">The name of the profile.</param>
    /// <param name="timestamp">The point-in-time to query against.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The matching sensitivity schedule, or null if none found.</returns>
    public async Task<SensitivitySchedule?> GetActiveAtAsync(
        string profileName, DateTime timestamp, CancellationToken ct = default)
    {
        var entity = await _context.SensitivitySchedules
            .AsNoTracking()
            .Where(e => e.ProfileName == profileName && e.Timestamp <= timestamp)
            .OrderByDescending(e => e.Timestamp)
            .FirstOrDefaultAsync(ct);

        return entity is null ? null : SensitivityScheduleMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Creates a new insulin sensitivity schedule record.
    /// </summary>
    /// <param name="model">The insulin sensitivity schedule to create.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The created record.</returns>
    public async Task<SensitivitySchedule> CreateAsync(SensitivitySchedule model, CancellationToken ct = default)
    {
        var entity = SensitivityScheduleMapper.ToEntity(model);
        _context.SensitivitySchedules.Add(entity);
        await _context.SaveChangesAsync(ct);
        return SensitivityScheduleMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Updates an existing insulin sensitivity schedule record.
    /// </summary>
    /// <param name="id">The unique identifier of the record to update.</param>
    /// <param name="model">The updated record data.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The updated record.</returns>
    public async Task<SensitivitySchedule> UpdateAsync(
        Guid id,
        SensitivitySchedule model,
        CancellationToken ct = default
    )
    {
        var entity =
            await _context.SensitivitySchedules.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"SensitivitySchedule {id} not found");
        SensitivityScheduleMapper.UpdateEntity(entity, model);
        await _context.SaveChangesAsync(ct);
        return SensitivityScheduleMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Deletes an insulin sensitivity schedule record by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity =
            await _context.SensitivitySchedules.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"SensitivitySchedule {id} not found");
        _context.SensitivitySchedules.Remove(entity);
        await _context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Deletes an insulin sensitivity schedule record by its legacy identifier.
    /// </summary>
    /// <param name="legacyId">The legacy identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The number of deleted records.</returns>
    public async Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        return await _context.SensitivitySchedules.Where(e => e.LegacyId == legacyId).ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Deletes insulin sensitivity schedule records by legacy identifier prefix.
    /// </summary>
    /// <param name="prefix">The legacy identifier prefix.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The number of deleted records.</returns>
    public async Task<int> DeleteByLegacyIdPrefixAsync(string prefix, CancellationToken ct = default)
    {
        return await _context
            .SensitivitySchedules.Where(e => e.LegacyId != null && e.LegacyId.StartsWith(prefix))
            .ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Counts insulin sensitivity schedule records within a timestamp range.
    /// </summary>
    /// <param name="from">Optional start timestamp filter.</param>
    /// <param name="to">Optional end timestamp filter.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The count of matching records.</returns>
    public async Task<int> CountAsync(DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        var query = _context.SensitivitySchedules.AsNoTracking().AsQueryable();
        if (from.HasValue)
            query = query.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue)
            query = query.Where(e => e.Timestamp <= to.Value);
        return await query.CountAsync(ct);
    }

    /// <summary>
    /// Gets insulin sensitivity schedule records by correlation identifier.
    /// </summary>
    /// <param name="correlationId">The correlation identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of matching schedules.</returns>
    public async Task<IEnumerable<SensitivitySchedule>> GetByCorrelationIdAsync(
        Guid correlationId,
        CancellationToken ct = default
    )
    {
        var entities = await _context
            .SensitivitySchedules.AsNoTracking()
            .Where(e => e.CorrelationId == correlationId)
            .ToListAsync(ct);
        return entities.Select(SensitivityScheduleMapper.ToDomainModel);
    }

    /// <summary>
    /// Performs a bulk creation of insulin sensitivity schedule records, handling deduplication.
    /// </summary>
    /// <param name="records">The collection of records to create.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of created schedules.</returns>
    public async Task<IEnumerable<SensitivitySchedule>> BulkCreateAsync(
        IEnumerable<SensitivitySchedule> records,
        CancellationToken ct = default
    )
    {
        var entities = records.Select(SensitivityScheduleMapper.ToEntity).ToList();
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
                .SensitivitySchedules.AsNoTracking()
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
            _context.SensitivitySchedules.AddRange(batch);
            await _context.SaveChangesAsync(ct);
            _context.ChangeTracker.Clear();
        }

        return entities.Select(SensitivityScheduleMapper.ToDomainModel);
    }
}
