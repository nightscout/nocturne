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

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for <see cref="ProfileActivation"/> records: legacy-id keyed for the activations
/// decomposed from treatments, sync-key upserted for a connector's.
/// </summary>
public class ProfileActivationRepository
    : SyncUpsertRepositoryBase<ProfileActivation, ProfileActivationEntity>, IProfileActivationRepository
{
    public ProfileActivationRepository(
        INocturneDbContextFactory contextFactory,
        IAuditContext auditContext,
        ILogger<ProfileActivationRepository> logger,
        IV4RecordBroadcaster<ProfileActivation>? broadcaster = null)
        : base(contextFactory, auditContext, logger, broadcaster)
    {
    }

    protected override ProfileActivationEntity ToEntity(ProfileActivation model) => ProfileActivationMapper.ToEntity(model);

    protected override ProfileActivation ToDomain(ProfileActivationEntity entity) => ProfileActivationMapper.ToDomainModel(entity);

    protected override void ApplyUpdate(ProfileActivationEntity target, ProfileActivation source) =>
        ProfileActivationMapper.UpdateEntity(target, source);

    public async Task<IReadOnlyList<ProfileActivation>> GetOverlappingAsync(
        DateTime from, DateTime to, int limit, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx.ProfileActivations.AsNoTracking()
            .Where(e => e.Timestamp <= to && (e.EndTimestamp == null || e.EndTimestamp > from))
            .OrderByDescending(e => e.Timestamp)
            .ThenByDescending(e => e.Id)
            .Take(limit)
            .ToListAsync(ct);
        return entities.Select(ProfileActivationMapper.ToDomainModel).Reverse().ToList();
    }

    public async Task<IReadOnlyList<ProfileActivation>> GetStartingAfterAsync(
        DateTime after, int limit, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx.ProfileActivations.AsNoTracking()
            .Where(e => e.Timestamp > after)
            .OrderBy(e => e.Timestamp)
            .ThenBy(e => e.Id)
            .Take(limit)
            .ToListAsync(ct);
        return entities.Select(ProfileActivationMapper.ToDomainModel).ToList();
    }

    public async Task<IReadOnlyList<ProfileActivation>> GetRequestedAtOrBeforeAsync(
        DateTime at, int limit, CancellationToken ct = default)
    {
        var requested = nameof(ProfileActivationPhase.Requested);
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx.ProfileActivations.AsNoTracking()
            .Where(e => e.Phase == requested && e.Timestamp <= at)
            .OrderByDescending(e => e.Timestamp)
            .ThenByDescending(e => e.Id)
            .Take(limit)
            .ToListAsync(ct);
        return entities.Select(ProfileActivationMapper.ToDomainModel).ToList();
    }

    public async Task<IReadOnlyList<(string? DataSource, string? App, string? EnteredBy, DateTime FirstStart)>> GetEffectiveSourcesAsync(
        CancellationToken ct = default)
    {
        var effective = nameof(ProfileActivationPhase.Effective);
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var rows = await ctx.ProfileActivations.AsNoTracking()
            .Where(e => e.Phase == effective)
            .GroupBy(e => new { e.DataSource, e.App, e.EnteredBy })
            .Select(g => new { g.Key.DataSource, g.Key.App, g.Key.EnteredBy, FirstStart = g.Min(e => e.Timestamp) })
            .ToListAsync(ct);
        return rows.Select(r => (r.DataSource, r.App, r.EnteredBy, r.FirstStart)).ToList();
    }

    public async Task<IReadOnlyList<ProfileActivation>> GetIncludingDeletedAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0)
            return [];

        var wanted = ids.ToList();
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx.ProfileActivations.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.TenantId == ctx.TenantId && wanted.Contains(e.Id))
            .ToListAsync(ct);
        return entities.Select(ProfileActivationMapper.ToDomainModel).ToList();
    }

    public async Task<IReadOnlyList<ProfileActivation>> GetSettledByAsync(
        IReadOnlyCollection<DateTime> starts, IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        var ends = starts.Select(s => (DateTime?)s).ToList();
        var requestedIds = ids.Select(i => (Guid?)i).ToList();
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx.ProfileActivations.AsNoTracking()
            .Where(e => ends.Contains(e.EndTimestamp) || requestedIds.Contains(e.RequestedActivationId))
            .OrderBy(e => e.Timestamp)
            .ThenBy(e => e.Id)
            .ToListAsync(ct);
        return entities.Select(ProfileActivationMapper.ToDomainModel).ToList();
    }

    public async Task<int> SetEndsAsync(IReadOnlyDictionary<Guid, DateTime?> ends, CancellationToken ct = default)
    {
        if (ends.Count == 0)
            return 0;

        var ids = ends.Keys.ToList();
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx.ProfileActivations.Where(e => ids.Contains(e.Id)).ToListAsync(ct);
        foreach (var entity in entities)
            entity.EndTimestamp = ends[entity.Id];
        return entities.Count == 0 ? 0 : await SaveChangedAsync(ctx, entities, ct);
    }

    public async Task<int> SetRequestedActivationAsync(
        IReadOnlyCollection<Guid> effectiveIds, Guid? requestedId, CancellationToken ct = default)
    {
        if (effectiveIds.Count == 0)
            return 0;

        var ids = effectiveIds.ToList();
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx.ProfileActivations.Where(e => ids.Contains(e.Id)).ToListAsync(ct);
        foreach (var entity in entities)
            entity.RequestedActivationId = requestedId;
        return entities.Count == 0 ? 0 : await SaveChangedAsync(ctx, entities, ct);
    }

    public async Task<IReadOnlyList<ProfileActivation>> GetUnattributedAsync(
        DateTime? from, DateTime? to, int limit, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx.GetUnattributedAsync<ProfileActivationEntity>(from, to, limit, ct);
        return entities.Select(ProfileActivationMapper.ToDomainModel).ToList();
    }

    public async Task<int> SetPatientDeviceIdsAsync(
        IReadOnlyDictionary<Guid, Guid> patientDeviceIdByRecordId, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return await ctx.SetPatientDeviceIdsAsync<ProfileActivationEntity>(patientDeviceIdByRecordId, ct);
    }

    /// <summary>Saves only the rows whose values moved, so an unchanged end writes no row version.</summary>
    private static async Task<int> SaveChangedAsync(
        NocturneDbContext ctx, List<ProfileActivationEntity> entities, CancellationToken ct)
    {
        var changed = entities.Count(e => ctx.Entry(e).State == EntityState.Modified);
        if (changed > 0)
            await ctx.SaveChangesAsync(ct);
        return changed;
    }
}
