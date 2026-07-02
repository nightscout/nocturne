using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing basal schedules in the database. A LegacyId-only type (no SyncId-upsert,
/// no DeduplicationService participation), so it uses the shared
/// <see cref="V4RepositoryBase{TModel,TEntity}"/> behaviour unchanged except its legacy-id deletes,
/// which route through the audited soft-delete helper, plus the basal-schedule-specific queries below.
/// </summary>
public class BasalScheduleRepository : V4RepositoryBase<BasalSchedule, BasalScheduleEntity>, IBasalScheduleRepository
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BasalScheduleRepository"/> class.
    /// </summary>
    /// <param name="contextFactory">The tenant database context factory.</param>
    /// <param name="auditContext">The audit context for tracking mutations.</param>
    /// <param name="logger">The logger instance.</param>
    // logger is unused for this LegacyId-only type but retained for DI + direct test construction.
    public BasalScheduleRepository(ITenantDbContextFactory contextFactory, IAuditContext auditContext, ILogger<BasalScheduleRepository> logger, IV4RecordBroadcaster<BasalSchedule>? broadcaster = null)
        : base(contextFactory, auditContext, broadcaster)
    {
    }

    /// <inheritdoc />
    protected override BasalScheduleEntity ToEntity(BasalSchedule model) => BasalScheduleMapper.ToEntity(model);

    /// <inheritdoc />
    protected override BasalSchedule ToDomain(BasalScheduleEntity entity) => BasalScheduleMapper.ToDomainModel(entity);

    /// <inheritdoc />
    protected override void ApplyUpdate(BasalScheduleEntity target, BasalSchedule source) =>
        BasalScheduleMapper.UpdateEntity(target, source);

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
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx
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
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entity = await ctx.BasalSchedules
            .AsNoTracking()
            .Where(e => e.ProfileName == profileName && e.Timestamp <= timestamp)
            .OrderByDescending(e => e.Timestamp)
            .FirstOrDefaultAsync(ct);

        return entity is null ? null : BasalScheduleMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Deletes basal schedule records by legacy identifier prefix.
    /// </summary>
    /// <param name="prefix">The legacy identifier prefix.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The number of deleted records.</returns>
    public async Task<int> DeleteByLegacyIdPrefixAsync(string prefix, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return await ctx.AuditedSoftDeleteAsync(
            ctx.BasalSchedules.Where(e => e.LegacyId != null && e.LegacyId.StartsWith(prefix)),
            AuditContext, ct);
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
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx
            .BasalSchedules.AsNoTracking()
            .Where(e => e.CorrelationId == correlationId)
            .ToListAsync(ct);
        return entities.Select(BasalScheduleMapper.ToDomainModel);
    }
}
