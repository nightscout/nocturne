using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Multitenancy;
using Nocturne.Connectors.GoogleHealth.Services;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Health.GoogleHealth;

/// <summary>
/// Keeps <see cref="IGoogleHealthGrantHolders"/> in the platform table
/// <c>google_health_grant_holders</c>, one row per tenant whose state has been recorded.
/// </summary>
/// <remarks>
/// A token stored before the table existed has no row. Before its first answer the process
/// therefore adopts the token of every tenant that has no row, reading it from that tenant's
/// secrets. A tenant with a row is never adopted: its row, even one
/// recording a release, is newer than anything adoption reads. A tenant whose secrets cannot be
/// read is skipped, since it cannot use its token either; only a failure to list the tenants
/// leaves adoption to the next question, and the answer meanwhile is
/// <see cref="GoogleHealthGrantSharing.Undetermined"/>.
/// </remarks>
public sealed class PostgresGoogleHealthGrantHolders(
    IServiceScopeFactory scopes,
    INocturneDbContextFactory contexts,
    ILogger<PostgresGoogleHealthGrantHolders> logger) : IGoogleHealthGrantHolders
{
    private const string AuditEndpoint = "service:google-health";
    private readonly SemaphoreSlim adoption = new(1, 1);
    private volatile bool adopted;

    public Task HoldAsync(Guid tenantId, string accountKey, CancellationToken ct) =>
        RecordAsync(tenantId, accountKey, ct);

    public Task ReleaseAsync(Guid tenantId, CancellationToken ct) => RecordAsync(tenantId, null, ct);

    public async Task<GoogleHealthGrantSharing> SharingAsync(Guid tenantId, string accountKey, CancellationToken ct)
    {
        try
        {
            await AdoptStoredTokensAsync(ct);
            await using var db = await ContextAsync(ct);
            return await db.GoogleHealthGrantHolders
                .AnyAsync(row => row.AccountKey == accountKey && row.TenantId != tenantId, ct)
                ? GoogleHealthGrantSharing.Shared
                : GoogleHealthGrantSharing.NotShared;
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or TimeoutException)
        {
            logger.LogWarning(ex,
                "Google Health could not read who else holds the account of tenant {TenantId}", tenantId);
            return GoogleHealthGrantSharing.Undetermined;
        }
    }

    private async Task RecordAsync(Guid tenantId, string? accountKey, CancellationToken ct)
    {
        await using var db = await ContextAsync(ct);
        if (await UpdateAsync(db, tenantId, accountKey, ct) > 0) return;
        if (!await TryInsertAsync(db, tenantId, accountKey, ct) &&
            await UpdateAsync(db, tenantId, accountKey, ct) == 0)
            throw new InvalidOperationException($"Google Health could not record the grant holder state of tenant {tenantId}");
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

    private async Task AdoptStoredTokensAsync(CancellationToken ct)
    {
        if (adopted) return;
        await adoption.WaitAsync(ct);
        try
        {
            if (adopted) return;
            List<Guid> unrecorded;
            await using (var db = await ContextAsync(ct))
                unrecorded = await db.Tenants.AsNoTracking()
                    .Where(tenant => !db.GoogleHealthGrantHolders.Any(row => row.TenantId == tenant.Id))
                    .Select(tenant => tenant.Id).ToListAsync(ct);
            foreach (var tenantId in unrecorded)
                await AdoptAsync(tenantId, ct);
            adopted = true;
        }
        finally
        {
            adoption.Release();
        }
    }

    private async Task AdoptAsync(Guid tenantId, CancellationToken ct)
    {
        try
        {
            await using var scope = TenantJobScope.Open(
                scopes, new TenantContext(tenantId, "", "", true, false), AuditEndpoint);
            var secrets = await scope.ServiceProvider.GetRequiredService<IConnectorConfigurationService>()
                .GetSecretsAsync(GoogleHealthConnectorService.ConnectorName, ct);
            if (GoogleHealthSessionStore.HeldAccount(secrets) is not { } account) return;
            await using var db = await ContextAsync(ct);
            await TryInsertAsync(db, tenantId, account, ct);
        }
        catch (Exception ex) when (ex is DbException or JsonException or InvalidOperationException)
        {
            logger.LogWarning(ex,
                "Google Health could not read the stored session of tenant {TenantId}; its token is not counted as held",
                tenantId);
        }
    }

    private ValueTask<NocturneDbContext> ContextAsync(CancellationToken ct) => contexts.CreatePlatformAsync(ct);
}
