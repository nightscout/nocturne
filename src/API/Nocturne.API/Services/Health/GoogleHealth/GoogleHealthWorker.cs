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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessRequestsAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Google Health queue polling failed; durable requests will be retried");
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }
    }

    private async Task ProcessRequestsAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Parallel.ForEachAsync(coordinator.ReadRequestsAsync(stoppingToken),
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = stoppingToken },
                async (tenantId, ct) =>
            {
                await using var claim = await coordinator.ClaimWorkerAsync(tenantId, ct);
                if (claim is null) return;
                if (await coordinator.DisconnectRequestAsync(tenantId, ct) is { } disconnect)
                {
                    using var recovery = scopes.CreateScope();
                    recovery.ServiceProvider.GetRequiredService<ITenantAccessor>()
                        .SetTenant(new(tenantId, "", "", true, false));
                    await recovery.ServiceProvider.GetRequiredService<Nocturne.Core.Contracts.Health.IGoogleHealthService>()
                        .DisconnectAsync(disconnect.SubjectId, ct);
                    return;
                }
                if (!await coordinator.StartQueuedAsync(tenantId, ct)) return;
                var completed = false;
                try
                {
                    using var listing = scopes.CreateScope();
                    var tenant = await listing.ServiceProvider.GetRequiredService<ITenantService>()
                        .GetByIdAsync(tenantId, ct);
                    if (tenant is not { IsActive: true })
                    {
                        completed = true;
                        return;
                    }
                    completed = await SyncTenantAsync(tenant.Id, tenant.Slug, tenant.DisplayName, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    var error = ex as GoogleHealthException;
                    logger.LogError(ex,
                        "Queued Google Health sync failed for tenant {TenantId} with code {Code} at stage {Stage}. Exception: {Message}",
                        tenantId, error?.Message ?? "internal_sync", error?.Stage ?? "worker", ex.Message);
                }
                finally
                {
                    if (completed && !ct.IsCancellationRequested) await coordinator.CompleteAsync(tenantId);
                }
            });
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task<bool> SyncTenantAsync(Guid id, string slug, string displayName, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantAccessor>()
            .SetTenant(new(id, slug, displayName, true, false));
        var sync = scope.ServiceProvider.GetRequiredService<IConnectorSyncService>();
        SyncResult result;
        while (true)
        {
            if (!await coordinator.StartQueuedAsync(id, ct)) return false;
            result = await sync.TriggerSyncAsync(ConnectorId, new SyncRequest(), ct);
            if (!result.AlreadyRunning) break;
            logger.LogInformation(
                "Google Health worker for tenant {TenantId} ({Slug}) is waiting to retry after an active run", id, slug);
            await using (await coordinator.AcquireAsync(id, ct)) { }
            // The connector releases its operation lock just before the outer run guard.
            await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
        }
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
            return true;
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
            return true;
    }
}
