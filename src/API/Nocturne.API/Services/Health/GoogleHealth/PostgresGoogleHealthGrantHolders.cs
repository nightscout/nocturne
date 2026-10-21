using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Multitenancy;
using Nocturne.Connectors.GoogleHealth.Services;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Health.GoogleHealth;

/// <summary>
/// Keeps <see cref="IGoogleHealthGrantHolders"/> in the platform table
/// <c>google_health_grant_holders</c>.
/// </summary>
/// <remarks>
/// A token stored before the table existed, or by a replica on an older image, has no row. Before
/// its first answer each process therefore adds a row for every tenant whose secrets hold a token.
/// While that pass fails, every answer is that another tenant may hold the account, and the next
/// question retries it.
/// </remarks>
public sealed class PostgresGoogleHealthGrantHolders(
    IServiceScopeFactory scopes,
    ILogger<PostgresGoogleHealthGrantHolders> logger) : IGoogleHealthGrantHolders
{
    private const string ConnectorName = "GoogleHealth";
    private const string AuditEndpoint = "service:google-health";
    private readonly SemaphoreSlim adoption = new(1, 1);
    private volatile bool adopted;

    public Task HoldAsync(Guid tenantId, string accountKey, CancellationToken ct) =>
        ExecuteAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO google_health_grant_holders (tenant_id, account_key) VALUES ({tenantId}, {accountKey})
            ON CONFLICT (tenant_id) DO UPDATE SET account_key = EXCLUDED.account_key
            """, ct), ct);

    public Task ReleaseAsync(Guid tenantId, CancellationToken ct) =>
        ExecuteAsync(db => db.GoogleHealthGrantHolders.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync(ct), ct);

    public async Task<bool> MayBeHeldByAnotherTenantAsync(Guid tenantId, string accountKey, CancellationToken ct)
    {
        try
        {
            await AdoptStoredTokensAsync(ct);
            return await ExecuteAsync(db => db.GoogleHealthGrantHolders
                .AnyAsync(row => row.AccountKey == accountKey && row.TenantId != tenantId, ct), ct);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or TimeoutException
                                       or CryptographicException or JsonException)
        {
            logger.LogWarning(ex,
                "Google Health could not tell whether another tenant holds the account of tenant {TenantId}; keeping its grant",
                tenantId);
            return true;
        }
    }

    private async Task AdoptStoredTokensAsync(CancellationToken ct)
    {
        if (adopted) return;
        await adoption.WaitAsync(ct);
        try
        {
            if (adopted) return;
            var tenants = await ExecuteAsync(db => db.Tenants.AsNoTracking().Select(tenant => tenant.Id).ToListAsync(ct), ct);
            foreach (var tenantId in tenants)
            {
                await using var scope = TenantJobScope.Open(
                    scopes, new TenantContext(tenantId, "", "", true, false), AuditEndpoint);
                var secrets = await scope.ServiceProvider.GetRequiredService<IConnectorConfigurationService>()
                    .GetSecretsAsync(ConnectorName, ct);
                if (GoogleHealthSessionStore.HeldAccount(secrets) is not { } account) continue;
                // A row written since the secrets were read is newer than they are.
                await ExecuteAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO google_health_grant_holders (tenant_id, account_key) VALUES ({tenantId}, {account})
                    ON CONFLICT (tenant_id) DO NOTHING
                    """, ct), ct);
            }
            adopted = true;
        }
        finally
        {
            adoption.Release();
        }
    }

    private async Task<T> ExecuteAsync<T>(Func<NocturneDbContext, Task<T>> operation, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreatePlatformAsync(ct);
        return await operation(db);
    }
}
