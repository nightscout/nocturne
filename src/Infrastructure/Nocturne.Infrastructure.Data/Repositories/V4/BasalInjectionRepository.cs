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
/// Repository for <see cref="BasalInjection"/> records (discrete long-acting basal insulin
/// injections, MDI). SyncId-upsert keyed; never cross-connector dedup-linked.
/// </summary>
public class BasalInjectionRepository : SyncUpsertRepositoryBase<BasalInjection, BasalInjectionEntity>, IBasalInjectionRepository
{
    /// <inheritdoc />
    public BasalInjectionRepository(
        ITenantDbContextFactory contextFactory,
        IAuditContext auditContext,
        IV4RecordBroadcaster<BasalInjection>? broadcaster = null)
        : base(contextFactory, auditContext, broadcaster)
    {
    }

    /// <inheritdoc />
    protected override BasalInjectionEntity ToEntity(BasalInjection model) => BasalInjectionMapper.ToEntity(model);

    /// <inheritdoc />
    protected override BasalInjection ToDomain(BasalInjectionEntity entity) => BasalInjectionMapper.ToDomainModel(entity);

    /// <inheritdoc />
    protected override void ApplyUpdate(BasalInjectionEntity target, BasalInjection source) => BasalInjectionMapper.UpdateEntity(target, source);

    /// <inheritdoc />
    public async Task<IReadOnlyList<BasalInjection>> GetUnattributedAsync(DateTime? from, DateTime? to, int limit, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx.GetUnattributedAsync<BasalInjectionEntity>(from, to, limit, ct);
        return entities.Select(BasalInjectionMapper.ToDomainModel).ToList();
    }

    /// <inheritdoc />
    public async Task<int> SetPatientDeviceIdsAsync(IReadOnlyDictionary<Guid, Guid> patientDeviceIdByRecordId, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return await ctx.SetPatientDeviceIdsAsync<BasalInjectionEntity>(patientDeviceIdByRecordId, ct);
    }
}
