using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Health;

namespace Nocturne.API.Services.Health.GoogleHealth;

public sealed class GoogleHealthWorker(
    IServiceScopeFactory scopes,
    GoogleHealthCoordinator coordinator,
    ILogger<GoogleHealthWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(ProcessRequestsAsync(stoppingToken), ProcessScheduleAsync(stoppingToken));

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
                    await SyncTenantAsync(tenant.Id, tenant.Slug, tenant.DisplayName, true, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception) { logger.LogWarning("Queued Google Health sync failed; details are not logged to protect health data and credentials"); }
                finally { coordinator.Complete(tenantId); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task ProcessScheduleAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var listing = scopes.CreateScope();
                    var tenants = await listing.ServiceProvider.GetRequiredService<ITenantService>().GetAllAsync(stoppingToken);
                    foreach (var tenant in tenants.Where(t => t.IsActive))
                    {
                        if (!coordinator.StartScheduled(tenant.Id)) continue;
                        try
                        {
                            await SyncTenantAsync(tenant.Id, tenant.Slug, tenant.DisplayName, false, stoppingToken);
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                        catch (Exception) { logger.LogWarning("Google Health sync failed; details are not logged to protect health data and credentials"); }
                        finally { coordinator.Complete(tenant.Id); }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception) { logger.LogWarning("Google Health scheduler could not enumerate tenants"); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task SyncTenantAsync(Guid id, string slug, string displayName, bool force, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantAccessor>()
            .SetTenant(new(id, slug, displayName, true, false));
        await scope.ServiceProvider.GetRequiredService<IGoogleHealthService>()
            .SyncAsync(force, ct);
    }
}
