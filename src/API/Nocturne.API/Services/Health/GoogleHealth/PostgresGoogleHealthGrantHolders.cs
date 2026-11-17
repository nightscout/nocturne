using System.Data.Common;
using System.Text.Json;
using Nocturne.API.Multitenancy;
using Nocturne.Connectors.GoogleHealth.Services;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Health.GoogleHealth;

/// <summary>
/// Keeps <see cref="IGoogleHealthGrantHolders"/> in <see cref="IGoogleHealthGrantHolderRepository"/>,
/// one row per tenant whose state has been recorded.
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
    IGoogleHealthGrantHolderRepository holders,
    ITenantRepository tenants,
    ILogger<PostgresGoogleHealthGrantHolders> logger) : IGoogleHealthGrantHolders
{
    private const string AuditEndpoint = "service:google-health";
    private readonly SemaphoreSlim adoption = new(1, 1);
    private volatile bool adopted;

    public Task HoldAsync(Guid tenantId, string accountKey, CancellationToken ct) =>
        holders.RecordAsync(tenantId, accountKey, ct);

    public Task ReleaseAsync(Guid tenantId, CancellationToken ct) => holders.RecordAsync(tenantId, null, ct);

    public async Task<GoogleHealthGrantSharing> SharingAsync(Guid tenantId, string accountKey, CancellationToken ct)
    {
        try
        {
            await AdoptStoredTokensAsync(ct);
            return await holders.IsHeldByAnotherTenantAsync(tenantId, accountKey, ct)
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

    private async Task AdoptStoredTokensAsync(CancellationToken ct)
    {
        if (adopted) return;
        await adoption.WaitAsync(ct);
        try
        {
            if (adopted) return;
            var recorded = await holders.GetRecordedTenantsAsync(ct);
            var unrecorded = (await tenants.ListIdsAsync(activeOnly: false, ct)).Where(id => !recorded.Contains(id)).ToList();
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
            await holders.TryAddAsync(tenantId, account, ct);
        }
        catch (Exception ex) when (ex is DbException or JsonException or InvalidOperationException)
        {
            logger.LogWarning(ex,
                "Google Health could not read the stored session of tenant {TenantId}; its token is not counted as held",
                tenantId);
        }
    }
}
