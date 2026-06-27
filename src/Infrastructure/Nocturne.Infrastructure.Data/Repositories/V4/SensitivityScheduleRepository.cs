using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing insulin sensitivity schedules (ISF) in the database. A LegacyId-only type
/// (no SyncId-upsert, no DeduplicationService participation), so it uses the shared
/// <see cref="V4RepositoryBase{TModel,TEntity}"/> behaviour unchanged plus the sensitivity-specific
/// queries below.
/// </summary>
public class SensitivityScheduleRepository : V4RepositoryBase<SensitivitySchedule, SensitivityScheduleEntity>, ISensitivityScheduleRepository
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SensitivityScheduleRepository"/> class.
    /// </summary>
    /// <param name="contextFactory">The tenant database context factory.</param>
    /// <param name="auditContext">The audit context for tracking mutations (used by the base soft-delete path).</param>
    /// <param name="logger">The logger instance.</param>
    // logger is unused for this LegacyId-only type but retained for DI + direct test construction.
    public SensitivityScheduleRepository(ITenantDbContextFactory contextFactory, IAuditContext auditContext, ILogger<SensitivityScheduleRepository> logger)
        : base(contextFactory, auditContext)
    {
    }

    /// <inheritdoc />
    protected override SensitivityScheduleEntity ToEntity(SensitivitySchedule model) => SensitivityScheduleMapper.ToEntity(model);

    /// <inheritdoc />
    protected override SensitivitySchedule ToDomain(SensitivityScheduleEntity entity) => SensitivityScheduleMapper.ToDomainModel(entity);

    /// <inheritdoc />
    protected override void ApplyUpdate(SensitivityScheduleEntity target, SensitivitySchedule source) =>
        SensitivityScheduleMapper.UpdateEntity(target, source);

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
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx
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
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entity = await ctx.SensitivitySchedules
            .AsNoTracking()
            .Where(e => e.ProfileName == profileName && e.Timestamp <= timestamp)
            .OrderByDescending(e => e.Timestamp)
            .FirstOrDefaultAsync(ct);

        return entity is null ? null : SensitivityScheduleMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Deletes insulin sensitivity schedule records by legacy identifier prefix.
    /// </summary>
    /// <param name="prefix">The legacy identifier prefix.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The number of deleted records.</returns>
    public async Task<int> DeleteByLegacyIdPrefixAsync(string prefix, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return await ctx
            .SensitivitySchedules.Where(e => e.LegacyId != null && e.LegacyId.StartsWith(prefix))
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.DeletedAt, DateTime.UtcNow), ct);
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
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx
            .SensitivitySchedules.AsNoTracking()
            .Where(e => e.CorrelationId == correlationId)
            .ToListAsync(ct);
        return entities.Select(SensitivityScheduleMapper.ToDomainModel);
    }
}
