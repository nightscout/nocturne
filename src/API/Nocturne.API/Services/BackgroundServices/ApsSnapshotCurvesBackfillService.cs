using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Rewrites every tenant's stored APS snapshots into the shape the API now writes, through
/// <see cref="ApsSnapshotCurvesRewriter"/>, in small paced batches after startup.
/// </summary>
/// <remarks>
/// Idempotent and safe to run on every startup: the rewriter writes only rows whose rewrite
/// differs, so once the table is rewritten a run lists candidates and writes nothing. Each batch
/// commits on its own, so a restart keeps the progress made. Per tenant because
/// <c>aps_snapshots</c> is RLS-scoped and the policy is fail-closed.
/// </remarks>
internal sealed class ApsSnapshotCurvesBackfillService(
    IServiceProvider serviceProvider,
    ILogger<ApsSnapshotCurvesBackfillService> logger) : BackgroundService
{
    internal const int BatchSize = 500;

    /// <summary>The pause between batches, which leaves the database to live traffic.</summary>
    internal static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(250);

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

            foreach (var tenantId in tenantIds)
            {
                await using var db = await factory.CreateForTenantAsync(tenantId, stoppingToken);
                var candidates = await ApsSnapshotCurvesRewriter.ListCandidatesAsync(db, tenantId, stoppingToken);

                var changed = 0;
                foreach (var batch in candidates.Chunk(BatchSize))
                {
                    changed += await ApsSnapshotCurvesRewriter.RewriteAsync(db, batch, stoppingToken);
                    await Task.Delay(Pause, stoppingToken);
                }

                if (changed > 0)
                {
                    logger.LogInformation(
                        "Rewrote {Changed} of {Candidates} APS snapshot(s) for tenant {TenantId}",
                        changed,
                        candidates.Count,
                        tenantId);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error rewriting stored APS snapshot curves");
        }
    }
}
