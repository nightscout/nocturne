using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing target range schedules in the database.
/// </summary>
public class TargetRangeScheduleRepository : ITargetRangeScheduleRepository
{
    private readonly ITenantDbContextFactory _contextFactory;
    private readonly ILogger<TargetRangeScheduleRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TargetRangeScheduleRepository"/> class.
    /// </summary>
    /// <param name="contextFactory">The tenant database context factory.</param>
    /// <param name="logger">The logger instance.</param>
    public TargetRangeScheduleRepository(ITenantDbContextFactory contextFactory, ILogger<TargetRangeScheduleRepository> logger)
    {
        _contextFactory = contextFactory;
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
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var query = ctx.TargetRangeSchedules.AsNoTracking().AsQueryable();
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
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = await ctx.TargetRangeSchedules.FindAsync([id], ct);
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
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = await ctx.TargetRangeSchedules.FirstOrDefaultAsync(e => e.LegacyId == legacyId, ct);
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
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entities = await ctx
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
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = await ctx.TargetRangeSchedules
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
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = TargetRangeScheduleMapper.ToEntity(model);
        ctx.TargetRangeSchedules.Add(entity);
        await ctx.SaveChangesAsync(ct);
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
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity =
            await ctx.TargetRangeSchedules.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"TargetRangeSchedule {id} not found");
        TargetRangeScheduleMapper.UpdateEntity(entity, model);
        await ctx.SaveChangesAsync(ct);
        return TargetRangeScheduleMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Deletes a target range schedule by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity =
            await ctx.TargetRangeSchedules.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"TargetRangeSchedule {id} not found");
        entity.DeletedAt = DateTime.UtcNow;
        await ctx.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<TargetRangeSchedule> RestoreAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = await ctx.TargetRangeSchedules.IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId && e.Id == id && e.DeletedAt != null)
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException($"Soft-deleted TargetRangeSchedule {id} not found");
        entity.DeletedAt = null;
        await ctx.SaveChangesAsync(ct);
        return TargetRangeScheduleMapper.ToDomainModel(entity);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<TargetRangeSchedule>> BulkRestoreAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var idSet = ids.ToHashSet();
        var entities = await ctx.TargetRangeSchedules.IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId && idSet.Contains(e.Id) && e.DeletedAt != null)
            .ToListAsync(ct);
        foreach (var entity in entities)
            entity.DeletedAt = null;
        await ctx.SaveChangesAsync(ct);
        return entities.Select(TargetRangeScheduleMapper.ToDomainModel);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<TargetRangeSchedule>> GetDeletedAsync(int limit, int offset, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entities = await ctx.TargetRangeSchedules.IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId && e.DeletedAt != null)
            .OrderByDescending(e => e.DeletedAt)
            .Skip(offset).Take(limit)
            .AsNoTracking()
            .ToListAsync(ct);
        return entities.Select(TargetRangeScheduleMapper.ToDomainModel);
    }

    /// <inheritdoc />
    public async Task<int> CountDeletedAsync(CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        return await ctx.TargetRangeSchedules.IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId && e.DeletedAt != null)
            .CountAsync(ct);
    }

    /// <summary>
    /// Deletes target range schedules by legacy identifier.
    /// </summary>
    /// <param name="legacyId">The legacy identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The number of deleted records.</returns>
    public async Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        return await ctx.TargetRangeSchedules.Where(e => e.LegacyId == legacyId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.DeletedAt, DateTime.UtcNow), ct);
    }

    /// <summary>
    /// Deletes target range schedules by legacy identifier prefix.
    /// </summary>
    /// <param name="prefix">The legacy identifier prefix.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The number of deleted records.</returns>
    public async Task<int> DeleteByLegacyIdPrefixAsync(string prefix, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        return await ctx
            .TargetRangeSchedules.Where(e => e.LegacyId != null && e.LegacyId.StartsWith(prefix))
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.DeletedAt, DateTime.UtcNow), ct);
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
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var query = ctx.TargetRangeSchedules.AsNoTracking().AsQueryable();
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
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entities = await ctx
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

        await using var ctx = await _contextFactory.CreateAsync(ct);
        var strategy = ctx.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await ctx.Database.BeginTransactionAsync(ct);

            if (legacyIds.Count > 0)
            {
                var existingRecords = await ctx.TargetRangeSchedules.IgnoreQueryFilters().AsNoTracking()
                    .Where(e => e.TenantId == ctx.TenantId)
                    .Where(e => legacyIds.Contains(e.LegacyId!))
                    .Select(e => new { e.LegacyId, IsSoftDeleted = e.DeletedAt != null })
                    .ToListAsync(ct);

                var existingSet = existingRecords.Select(r => r.LegacyId).ToHashSet();
                var softDeletedCount = existingRecords.Count(r => r.IsSoftDeleted);

                if (softDeletedCount > 0)
                    _logger.LogInformation(
                        "Skipped {Count} previously-deleted {Type} records during import",
                        softDeletedCount, "TargetRangeSchedule");

                entities = entities
                    .Where(e => string.IsNullOrEmpty(e.LegacyId) || !existingSet.Contains(e.LegacyId))
                    .ToList();
            }

            if (entities.Count == 0)
            {
                await tx.CommitAsync(ct);
                return [];
            }

            const int batchSize = 500;
            foreach (var batch in entities.Chunk(batchSize))
            {
                ctx.TargetRangeSchedules.AddRange(batch);
                await ctx.SaveChangesAsync(ct);
                ctx.ChangeTracker.Clear();
            }

            await tx.CommitAsync(ct);
            return entities.Select(TargetRangeScheduleMapper.ToDomainModel);
        });
    }
}
