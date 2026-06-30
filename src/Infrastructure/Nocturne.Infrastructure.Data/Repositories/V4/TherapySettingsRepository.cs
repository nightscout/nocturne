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
/// Repository for managing therapy settings in the database. A LegacyId-only type (no SyncId-upsert,
/// no DeduplicationService participation), so it uses the shared
/// <see cref="V4RepositoryBase{TModel,TEntity}"/> behaviour unchanged plus the therapy-settings-specific
/// queries below.
/// </summary>
public class TherapySettingsRepository : V4RepositoryBase<TherapySettings, TherapySettingsEntity>, ITherapySettingsRepository
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TherapySettingsRepository"/> class.
    /// </summary>
    /// <param name="contextFactory">The tenant database context factory.</param>
    /// <param name="auditContext">The audit context for tracking mutations (used by the base soft-delete path).</param>
    /// <param name="logger">The logger instance.</param>
    // logger is unused for this LegacyId-only type but retained for DI + direct test construction.
    public TherapySettingsRepository(ITenantDbContextFactory contextFactory, IAuditContext auditContext, ILogger<TherapySettingsRepository> logger, IV4RecordBroadcaster<TherapySettings>? broadcaster = null)
        : base(contextFactory, auditContext, broadcaster)
    {
    }

    /// <inheritdoc />
    protected override TherapySettingsEntity ToEntity(TherapySettings model) => TherapySettingsMapper.ToEntity(model);

    /// <inheritdoc />
    protected override TherapySettings ToDomain(TherapySettingsEntity entity) => TherapySettingsMapper.ToDomainModel(entity);

    /// <inheritdoc />
    protected override void ApplyUpdate(TherapySettingsEntity target, TherapySettings source) =>
        TherapySettingsMapper.UpdateEntity(target, source);

    /// <summary>
    /// Gets therapy settings by profile name.
    /// </summary>
    /// <param name="profileName">The name of the profile.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of therapy settings.</returns>
    public async Task<IEnumerable<TherapySettings>> GetByProfileNameAsync(
        string profileName,
        CancellationToken ct = default
    )
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx
            .TherapySettings.AsNoTracking()
            .Where(e => e.ProfileName == profileName)
            .OrderByDescending(e => e.Timestamp)
            .ToListAsync(ct);
        return entities.Select(TherapySettingsMapper.ToDomainModel);
    }

    /// <summary>
    /// Gets the most recent therapy settings for a profile that was active at-or-before the given timestamp.
    /// </summary>
    /// <param name="profileName">The name of the profile.</param>
    /// <param name="timestamp">The point-in-time to query against.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The matching therapy settings, or null if none found.</returns>
    public async Task<TherapySettings?> GetActiveAtAsync(
        string profileName, DateTime timestamp, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entity = await ctx.TherapySettings
            .AsNoTracking()
            .Where(e => e.ProfileName == profileName && e.Timestamp <= timestamp)
            .OrderByDescending(e => e.Timestamp)
            .FirstOrDefaultAsync(ct);

        return entity is null ? null : TherapySettingsMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Deletes therapy settings by legacy identifier prefix.
    /// </summary>
    /// <param name="prefix">The legacy identifier prefix.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The number of deleted records.</returns>
    public async Task<int> DeleteByLegacyIdPrefixAsync(string prefix, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return await ctx
            .TherapySettings.Where(e => e.LegacyId != null && e.LegacyId.StartsWith(prefix))
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.DeletedAt, DateTime.UtcNow), ct);
    }

    /// <summary>
    /// Gets therapy settings by correlation identifier.
    /// </summary>
    /// <param name="correlationId">The correlation identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of therapy settings.</returns>
    public async Task<IEnumerable<TherapySettings>> GetByCorrelationIdAsync(
        Guid correlationId,
        CancellationToken ct = default
    )
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx
            .TherapySettings.AsNoTracking()
            .Where(e => e.CorrelationId == correlationId)
            .ToListAsync(ct);
        return entities.Select(TherapySettingsMapper.ToDomainModel);
    }
}
