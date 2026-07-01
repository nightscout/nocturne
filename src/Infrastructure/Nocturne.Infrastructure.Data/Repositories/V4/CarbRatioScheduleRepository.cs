using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing carbohydrate ratio schedules in the database. A LegacyId-only type (no
/// SyncId-upsert, no DeduplicationService participation), so it uses the shared
/// <see cref="V4RepositoryBase{TModel,TEntity}"/> behaviour unchanged plus the carb-ratio-specific
/// queries below.
/// </summary>
public class CarbRatioScheduleRepository : V4RepositoryBase<CarbRatioSchedule, CarbRatioScheduleEntity>, ICarbRatioScheduleRepository
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CarbRatioScheduleRepository"/> class.
    /// </summary>
    /// <param name="contextFactory">The tenant database context factory.</param>
    /// <param name="auditContext">The audit context for tracking mutations (used by the base soft-delete path).</param>
    /// <param name="logger">The logger instance.</param>
    // logger is unused for this LegacyId-only type but retained for DI + direct test construction.
    public CarbRatioScheduleRepository(ITenantDbContextFactory contextFactory, IAuditContext auditContext, ILogger<CarbRatioScheduleRepository> logger, IV4RecordBroadcaster<CarbRatioSchedule>? broadcaster = null)
        : base(contextFactory, auditContext, broadcaster)
    {
    }

    /// <inheritdoc />
    protected override CarbRatioScheduleEntity ToEntity(CarbRatioSchedule model) => CarbRatioScheduleMapper.ToEntity(model);

    /// <inheritdoc />
    protected override CarbRatioSchedule ToDomain(CarbRatioScheduleEntity entity) => CarbRatioScheduleMapper.ToDomainModel(entity);

    /// <inheritdoc />
    protected override void ApplyUpdate(CarbRatioScheduleEntity target, CarbRatioSchedule source) =>
        CarbRatioScheduleMapper.UpdateEntity(target, source);

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
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx
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
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entity = await ctx.CarbRatioSchedules
            .AsNoTracking()
            .Where(e => e.ProfileName == profileName && e.Timestamp <= timestamp)
            .OrderByDescending(e => e.Timestamp)
            .FirstOrDefaultAsync(ct);

        return entity is null ? null : CarbRatioScheduleMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Deletes carbohydrate ratio schedule records by legacy identifier prefix.
    /// </summary>
    /// <param name="prefix">The legacy identifier prefix.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The number of deleted records.</returns>
    public async Task<int> DeleteByLegacyIdPrefixAsync(string prefix, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return await ctx
            .CarbRatioSchedules.Where(e => e.LegacyId != null && e.LegacyId.StartsWith(prefix))
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.DeletedAt, DateTime.UtcNow), ct);
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
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx
            .CarbRatioSchedules.AsNoTracking()
            .Where(e => e.CorrelationId == correlationId)
            .ToListAsync(ct);
        return entities.Select(CarbRatioScheduleMapper.ToDomainModel);
    }
}
