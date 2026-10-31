using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Rewrites every tenant's stored APS snapshots into the shape the API now writes, through
/// <see cref="ApsSnapshotCurvesRewriter"/>, after startup.
/// </summary>
/// <remarks>
/// The decomposer writes the new shape, so once a tenant's pass has walked every page the tenant
/// is recorded in <c>backfill_completions</c> and later startups do not walk it again. Per
/// tenant because <c>aps_snapshots</c> is RLS-scoped and the policy is fail-closed, and a tenant
/// that fails is logged and left unrecorded so the rest are still rewritten; the next startup
/// retries it.
/// </remarks>
internal sealed class ApsSnapshotCurvesBackfillService(
    IServiceProvider serviceProvider,
    ILogger<ApsSnapshotCurvesBackfillService> logger,
    TimeProvider timeProvider,
    Func<NocturneDbContext, Guid, CancellationToken, Task<int>>? rewrite = null) : BackgroundService
{
    internal const string BackfillName = "aps-snapshot-curves";

    private readonly Func<NocturneDbContext, Guid, CancellationToken, Task<int>> _rewrite = rewrite
        ?? ((db, tenantId, ct) => ApsSnapshotCurvesRewriter.RewriteTenantAsync(
            db, tenantId, TenantSweepPaging.PageSize, TenantSweepPaging.PauseAfterWrite, TenantSweepPaging.PauseAfterRead, ct));

    /// <summary>
    /// Records the tenant as complete. A second instance that finishes the same tenant first has
    /// already written the row, so a unique violation is the row existing, not a failure.
    /// </summary>
    internal static async Task RecordCompletionAsync(
        NocturneDbContext db, Guid tenantId, TimeProvider timeProvider, CancellationToken ct)
    {
        db.BackfillCompletions.Add(new BackfillCompletionEntity
        {
            Backfill = BackfillName,
            TenantId = tenantId,
            CompletedAt = timeProvider.GetUtcNow().UtcDateTime,
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Detach from the sequential hosted-service startup chain before touching anything.
        await Task.Yield();

        try
        {
            using var scope = serviceProvider.CreateScope();
            var factory = scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>();

            // The tenant list and the completion rows are read with no tenant pinned.
            List<Guid> tenantIds;
            HashSet<Guid> completed;
            await using (var lookup = await factory.CreatePlatformAsync(stoppingToken))
            {
                tenantIds = [.. await scope.ServiceProvider.GetRequiredService<ITenantRepository>().ListIdsAsync(activeOnly: false, stoppingToken)];
                completed = (await lookup.BackfillCompletions.AsNoTracking()
                    .Where(c => c.Backfill == BackfillName)
                    .Select(c => c.TenantId)
                    .ToListAsync(stoppingToken)).ToHashSet();
            }

            var changedTotal = 0;
            var failed = 0;
            var skipped = 0;
            foreach (var tenantId in tenantIds)
            {
                if (completed.Contains(tenantId))
                {
                    skipped++;
                    continue;
                }

                try
                {
                    int changed;
                    await using (var db = await factory.CreateForTenantAsync(tenantId, stoppingToken))
                    {
                        changed = await _rewrite(db, tenantId, stoppingToken);
                    }

                    changedTotal += changed;

                    await using (var platform = await factory.CreatePlatformAsync(stoppingToken))
                    {
                        await RecordCompletionAsync(platform, tenantId, timeProvider, stoppingToken);
                    }

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
                "APS snapshot curve rewrite finished: {Changed} snapshot(s) rewritten across {Tenants} tenant(s), {Skipped} already complete, {Failed} failed",
                changedTotal,
                tenantIds.Count,
                skipped,
                failed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error rewriting stored APS snapshot curves");
        }
    }
}
