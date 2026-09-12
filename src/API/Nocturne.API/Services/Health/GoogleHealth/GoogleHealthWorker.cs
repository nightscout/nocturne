using Nocturne.API.Services.Connectors;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.GoogleHealth.Models;
using Nocturne.Connectors.GoogleHealth.Services;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Services.Health.GoogleHealth;

public sealed class GoogleHealthWorker(
    IServiceScopeFactory scopes,
    GoogleHealthCoordinator coordinator,
    ILogger<GoogleHealthWorker> logger) : BackgroundService
{
    private const string ConnectorId = "googlehealth";

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        ProcessRequestsAsync(stoppingToken);

    private async Task ProcessRequestsAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var tenantId in coordinator.ReadRequestsAsync(stoppingToken))
            {
                if (!coordinator.StartQueued(tenantId)) continue;
                try
                {
                    using var listing = scopes.CreateScope();
                    var tenant = await listing.ServiceProvider.GetRequiredService<ITenantService>()
                        .GetByIdAsync(tenantId, stoppingToken);
                    if (tenant is not { IsActive: true }) continue;
                    await SyncTenantAsync(tenant.Id, tenant.Slug, tenant.DisplayName, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    var error = ex as GoogleHealthException;
                    logger.LogError(ex,
                        "Queued Google Health sync failed for tenant {TenantId} with code {Code} at stage {Stage}. Exception: {Message}",
                        tenantId, error?.Message ?? "internal_sync", error?.Stage ?? "worker", ex.Message);
                }
                finally { coordinator.Complete(tenantId); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task SyncTenantAsync(Guid id, string slug, string displayName, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantAccessor>()
            .SetTenant(new(id, slug, displayName, true, false));
        var result = await scope.ServiceProvider.GetRequiredService<IConnectorSyncService>()
            .TriggerSyncAsync(ConnectorId, new SyncRequest(), ct);
        var configurations = scope.ServiceProvider.GetRequiredService<IConnectorConfigurationService>();
        var now = DateTime.UtcNow;
        if (result.Success)
        {
            logger.LogInformation("Google Health worker sync succeeded for tenant {TenantId} ({Slug})", id, slug);
            await configurations.UpdateHealthStateAsync(
                "GoogleHealth",
                lastSyncAttempt: now,
                lastSuccessfulSync: now,
                lastErrorMessage: result.Message,
                lastErrorAt: string.IsNullOrWhiteSpace(result.Message) ? DateTime.MinValue : now,
                isHealthy: true,
                ct: ct);
            return;
        }

        logger.LogError(
            "Google Health worker sync failed for tenant {TenantId} ({Slug}) with result message: {Message}. Errors: {Errors}",
            id, slug, result.Message, string.Join("; ", result.Errors));

        await configurations.UpdateHealthStateAsync(
            "GoogleHealth",
            lastSyncAttempt: now,
            lastErrorMessage: result.Message,
            lastErrorAt: now,
            isHealthy: false,
            ct: ct);
    }
}
