using System.Data.Common;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Re-resolves every tenant's schedule-derived percent temp basal rates once after startup, through
/// <see cref="ITempBasalRateReresolver.AfterProfilesChangedAsync"/>.
/// </summary>
/// <remarks>
/// It brings the rates up to schedule versions no write re-resolved for: those a migration moved in
/// SQL (<c>RepairEpochProfileVersions</c>), and those whose re-resolution after a write failed, which
/// never fails the write. A tenant with no schedule-derived rate costs one indexed probe, and a rate
/// already right is not written, so it is safe on every startup.
/// </remarks>
internal sealed class TempBasalRateSweepService(
    IServiceProvider serviceProvider,
    ActiveTenantSnapshot activeTenants,
    ILogger<TempBasalRateSweepService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        IReadOnlyList<ActiveTenant> tenants;
        try
        {
            tenants = await activeTenants.GetAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or TimeoutException)
        {
            logger.LogError(ex, "Could not list the tenants whose percent temp basal rates to re-resolve");
            return;
        }

        foreach (var tenant in tenants)
        {
            await using var job = TenantJobScope.Open(
                serviceProvider,
                new TenantContext(tenant.Id, tenant.Slug, tenant.DisplayName, true, IsDemo: false),
                TempBasalRateReresolver.AuditEndpoint);
            await job.ServiceProvider.GetRequiredService<ITempBasalRateReresolver>()
                .AfterProfilesChangedAsync([], stoppingToken);
        }
    }
}
