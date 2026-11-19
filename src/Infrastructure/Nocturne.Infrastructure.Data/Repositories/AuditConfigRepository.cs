using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <inheritdoc cref="IAuditConfigStore"/>
/// <remarks>
/// The tenant is the scope's, never a caller's argument. <c>tenant_audit_config</c> has no tenant
/// filter or policy, so <see cref="SaveAsync"/> matches and stamps the context's tenant itself;
/// <c>tenant_data_retention_config</c> is tenant-scoped, and its read follows the scope's tenant
/// through the filter and row level security.
/// </remarks>
public class AuditConfigRepository(INocturneDbContextFactory contexts, TimeProvider? time = null) : IAuditConfigStore
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<int?> GetSoftDeleteRetentionDaysAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateAsync(ct);
        return await db.TenantDataRetentionConfig
            .Select(c => c.SoftDeleteRetentionDays)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<TenantAuditConfig> SaveAsync(TenantAuditConfig config, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateAsync(ct);

        var tenantId = db.TenantId;
        var entity = await db.TenantAuditConfig.FirstOrDefaultAsync(c => c.TenantId == tenantId, ct);

        if (entity == null)
        {
            entity = new TenantAuditConfigEntity { Id = Guid.CreateVersion7(), TenantId = tenantId };
            db.TenantAuditConfig.Add(entity);
        }
        else
        {
            entity.SysUpdatedAt = _time.GetUtcNow().UtcDateTime;
        }

        entity.ReadAuditEnabled = config.ReadAuditEnabled;
        entity.ReadAuditRetentionDays = config.ReadAuditRetentionDays;
        entity.MutationAuditRetentionDays = config.MutationAuditRetentionDays;

        await db.SaveChangesAsync(ct);

        return new TenantAuditConfig(entity.ReadAuditEnabled, entity.ReadAuditRetentionDays, entity.MutationAuditRetentionDays);
    }
}
