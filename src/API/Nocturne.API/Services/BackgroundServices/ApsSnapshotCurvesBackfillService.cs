using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Rewrites every tenant's stored APS snapshots into the shape the API now writes, through
/// <see cref="ApsSnapshotCurvesRewriter"/>, after startup.
/// </summary>
/// <remarks>
/// Safe to run on every startup: a rewritten table is read once and nothing is written. Per
/// tenant because <c>aps_snapshots</c> is RLS-scoped and the policy is fail-closed, and a tenant
/// that fails is logged and skipped so the rest are still rewritten; the next startup retries it.
/// </remarks>
internal sealed class ApsSnapshotCurvesBackfillService(
    IServiceProvider serviceProvider,
    ILogger<ApsSnapshotCurvesBackfillService> logger) : BackgroundService
{
    internal const int PageSize = 500;

    /// <summary>The pause after a page that wrote, which leaves the database to live traffic.</summary>
    internal static readonly TimeSpan PauseAfterWrite = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The pause after a page that wrote nothing, which keeps a rewritten table's read on each
    /// startup to a fraction of one backend.
    /// </summary>
    internal static readonly TimeSpan PauseAfterRead = TimeSpan.FromMilliseconds(50);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Detach from the sequential hosted-service startup chain before touching anything.
        await Task.Yield();

        try
        {
            using var scope = serviceProvider.CreateScope();
            var factory = scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>();

            // Tenants are not RLS-scoped, so this list read is safe without a tenant context.
            List<Guid> tenantIds;
            await using (var lookup = await factory.CreatePlatformAsync(stoppingToken))
            {
                tenantIds = await lookup.Tenants.AsNoTracking().Select(t => t.Id).ToListAsync(stoppingToken);
            }

            var changedTotal = 0;
            var failed = 0;
            foreach (var tenantId in tenantIds)
            {
                try
                {
                    await using var db = await factory.CreateForTenantAsync(tenantId, stoppingToken);
                    var changed = await ApsSnapshotCurvesRewriter.RewriteTenantAsync(
                        db, tenantId, PageSize, PauseAfterWrite, PauseAfterRead, stoppingToken);
                    changedTotal += changed;
                    if (changed > 0)
                    {
                        logger.LogInformation(
                            "Rewrote {Changed} APS snapshot(s) for tenant {TenantId}", changed, tenantId);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    logger.LogError(ex, "Error rewriting APS snapshot curves for tenant {TenantId}", tenantId);
                }
            }

            logger.LogInformation(
                "APS snapshot curve rewrite finished: {Changed} snapshot(s) rewritten across {Tenants} tenant(s), {Failed} failed",
                changedTotal,
                tenantIds.Count,
                failed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error rewriting stored APS snapshot curves");
        }
    }
}
