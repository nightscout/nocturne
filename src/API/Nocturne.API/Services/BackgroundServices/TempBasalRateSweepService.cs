using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Caching;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Runs each <see cref="PendingTenantJobs.ProfileVersionsMoved"/> a migration left, once, after
/// startup: re-resolves the tenant's schedule-derived percent temp basal rates
/// (<see cref="ITempBasalRateReresolver.AfterProfilesChangedAsync"/>) and drops its cached reads, as a
/// profile write would have.
/// </summary>
/// <remarks>
/// A job's row is deleted once it has run, inactive tenant or not, so a restart does not run it
/// again. The re-resolution logs its own failure rather than throw, so a failed one is not retried.
/// </remarks>
internal sealed class TempBasalRateSweepService(
    IServiceProvider serviceProvider,
    ILogger<TempBasalRateSweepService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        try
        {
            var factory = serviceProvider.GetRequiredService<INocturneDbContextFactory>();
            List<Guid> pending;
            await using (var platform = await factory.CreatePlatformAsync(stoppingToken))
            {
                pending = await platform.PendingTenantJobs.AsNoTracking()
                    .Where(j => j.Job == PendingTenantJobs.ProfileVersionsMoved)
                    .Select(j => j.TenantId)
                    .ToListAsync(stoppingToken);
            }

            var tenants = (await serviceProvider.GetRequiredService<ITenantRepository>().GetManyAsync(pending, stoppingToken))
                .Select(t => (t.Id, t.Slug, t.DisplayName, t.IsActive))
                .ToList();

            foreach (var tenant in tenants)
            {
                await using (var job = TenantJobScope.Open(
                    serviceProvider,
                    new TenantContext(tenant.Id, tenant.Slug, tenant.DisplayName, tenant.IsActive, IsDemo: false),
                    TempBasalRateReresolver.AuditEndpoint))
                {
                    await job.ServiceProvider.GetRequiredService<ITempBasalRateReresolver>()
                        .AfterProfilesChangedAsync([], stoppingToken);
                    await job.ServiceProvider.GetRequiredService<ITenantCache>()
                        .RemoveTenantAsync(tenant.Id, stoppingToken);
                }

                await using var done = await factory.CreatePlatformAsync(stoppingToken);
                await done.PendingTenantJobs
                    .Where(j => j.TenantId == tenant.Id && j.Job == PendingTenantJobs.ProfileVersionsMoved)
                    .ExecuteDeleteAsync(stoppingToken);
                logger.LogInformation(
                    "Re-resolved the percent temp basal rates of tenant {TenantId}, whose profile versions a migration moved",
                    tenant.Id);
            }
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or TimeoutException)
        {
            logger.LogError(ex, "Could not run the pending re-resolution of moved profile versions; the next startup retries it");
        }
    }
}
