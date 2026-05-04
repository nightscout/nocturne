using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.Audit;

/// <summary>
/// Background service that purges expired audit log records based on per-tenant retention
/// configuration. Runs every 24 hours, deleting in batches to avoid WAL bloat.
/// </summary>
public class AuditRetentionService(
    IDbContextFactory<NocturneDbContext> contextFactory,
    ILogger<AuditRetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private const int BatchSize = 10_000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(InitialDelay, stoppingToken);

        using var timer = new PeriodicTimer(Interval);

        do
        {
            try
            {
                await PurgeExpiredRecordsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Audit retention purge failed; will retry next cycle");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// Queries all tenants with configured retention and deletes expired records in batches.
    /// </summary>
    internal async Task PurgeExpiredRecordsAsync(CancellationToken ct)
    {
        await using var configContext = await contextFactory.CreateDbContextAsync(ct);

        var configs = await configContext.TenantAuditConfig
            .Where(c => c.ReadAuditRetentionDays != null || c.MutationAuditRetentionDays != null)
            .Select(c => new
            {
                c.TenantId,
                c.ReadAuditRetentionDays,
                c.MutationAuditRetentionDays
            })
            .ToListAsync(ct);

        if (configs.Count == 0)
        {
            logger.LogDebug("No tenants with configured audit retention; skipping purge");
            return;
        }

        foreach (var config in configs)
        {
            try
            {
                var readDeleted = 0;
                var mutationDeleted = 0;

                if (config.ReadAuditRetentionDays is { } readDays)
                {
                    var cutoff = DateTime.UtcNow.AddDays(-readDays);
                    readDeleted = await PurgeBatchedAsync(
                        config.TenantId, "read_access_log", cutoff, ct);
                }

                if (config.MutationAuditRetentionDays is { } mutationDays)
                {
                    var cutoff = DateTime.UtcNow.AddDays(-mutationDays);
                    mutationDeleted = await PurgeBatchedAsync(
                        config.TenantId, "mutation_audit_log", cutoff, ct);
                }

                if (readDeleted > 0 || mutationDeleted > 0)
                {
                    logger.LogInformation(
                        "Audit retention purge for tenant {TenantId}: {ReadDeleted} read, {MutationDeleted} mutation records deleted",
                        config.TenantId, readDeleted, mutationDeleted);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "Audit retention purge failed for tenant {TenantId}; continuing with next tenant",
                    config.TenantId);
            }
        }
    }

    /// <summary>
    /// Deletes records from the specified table older than the cutoff, in batches of
    /// <see cref="BatchSize"/> to avoid WAL bloat and long-running transactions.
    /// </summary>
    /// <returns>Total number of records deleted.</returns>
    private async Task<int> PurgeBatchedAsync(
        Guid tenantId, string table, DateTime cutoff, CancellationToken ct)
    {
        var totalDeleted = 0;
        int batchDeleted;

        do
        {
            await using var db = await contextFactory.CreateDbContextAsync(ct);

            // Set RLS context for the tenant-scoped table
            await db.Database.ExecuteSqlRawAsync(
                "SELECT set_config('app.current_tenant_id', {0}, false)",
                [tenantId.ToString()], ct);

            // Delete a batch using ctid for efficient sub-select.
            // Table name is from our code (not user input) so interpolation is safe.
#pragma warning disable EF1002
            batchDeleted = await db.Database.ExecuteSqlRawAsync(
                $"DELETE FROM {table} WHERE ctid IN (SELECT ctid FROM {table} WHERE created_at < {{0}} LIMIT {BatchSize})",
                [cutoff], ct);
#pragma warning restore EF1002

            totalDeleted += batchDeleted;
        }
        while (batchDeleted >= BatchSize);

        return totalDeleted;
    }
}
