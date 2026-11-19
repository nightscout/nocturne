using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Health;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// <see cref="IGoogleHealthGrantHolderRepository"/> over the platform table
/// <c>google_health_grant_holders</c>, one platform context per call.
/// </summary>
public sealed class GoogleHealthGrantHolderRepository(INocturneDbContextFactory contexts)
    : IGoogleHealthGrantHolderRepository
{
    /// <inheritdoc />
    public async Task RecordAsync(Guid tenantId, string? accountKey, CancellationToken ct)
    {
        await using var db = await contexts.CreatePlatformAsync(ct);
        if (await UpdateAsync(db, tenantId, accountKey, ct) > 0) return;
        if (!await TryInsertAsync(db, tenantId, accountKey, ct) &&
            await UpdateAsync(db, tenantId, accountKey, ct) == 0)
            throw new InvalidOperationException($"Google Health could not record the grant holder state of tenant {tenantId}");
    }

    /// <inheritdoc />
    public async Task<bool> TryAddAsync(Guid tenantId, string accountKey, CancellationToken ct)
    {
        await using var db = await contexts.CreatePlatformAsync(ct);
        return await TryInsertAsync(db, tenantId, accountKey, ct);
    }

    /// <inheritdoc />
    public async Task<bool> IsHeldByAnotherTenantAsync(Guid tenantId, string accountKey, CancellationToken ct)
    {
        await using var db = await contexts.CreatePlatformAsync(ct);
        return await db.GoogleHealthGrantHolders
            .AnyAsync(row => row.AccountKey == accountKey && row.TenantId != tenantId, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<Guid>> GetRecordedTenantsAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreatePlatformAsync(ct);
        return (await db.GoogleHealthGrantHolders.AsNoTracking().Select(row => row.TenantId).ToListAsync(ct))
            .ToHashSet();
    }

    private static Task<int> UpdateAsync(NocturneDbContext db, Guid tenantId, string? accountKey, CancellationToken ct) =>
        db.GoogleHealthGrantHolders.Where(row => row.TenantId == tenantId)
            .ExecuteUpdateAsync(row => row.SetProperty(holder => holder.AccountKey, accountKey), ct);

    /// <returns>False when another writer inserted the tenant's row first, or the tenant is gone.</returns>
    private static async Task<bool> TryInsertAsync(NocturneDbContext db, Guid tenantId, string? accountKey, CancellationToken ct)
    {
        var row = db.GoogleHealthGrantHolders.Add(new GoogleHealthGrantHolderEntity { TenantId = tenantId, AccountKey = accountKey });
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            row.State = EntityState.Detached;
            return false;
        }
    }
}
