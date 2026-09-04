using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.API.Services.Audit;

/// <summary>
/// Background service that purges expired audit log records. Every tenant is purged at the
/// platform default retention (<c>Audit:DefaultReadAuditRetentionDays</c> /
/// <c>Audit:DefaultMutationRetentionDays</c>); a tenant's own <see cref="TenantAuditConfigEntity"/>
/// value overrides the default in either direction. Runs every 24 hours, deleting in batches to
/// avoid WAL bloat.
/// </summary>
public class AuditRetentionService(
    IDbContextFactory<NocturneDbContext> contextFactory,
    IConfiguration configuration,
    ILogger<AuditRetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private const int BatchSize = 10_000;

    /// <summary>
    /// Platform retention applied to tenants that have not set their own. Audit rows are an
    /// operational trail, not clinical data, and unbounded retention grew mutation_audit_log to
    /// 24GB in production while almost no tenant had opted into a retention window.
    /// </summary>
    private const int FallbackRetentionDays = 90;

    private int? DefaultReadRetentionDays => ResolveDefault("Audit:DefaultReadAuditRetentionDays");

    private int? DefaultMutationRetentionDays => ResolveDefault("Audit:DefaultMutationRetentionDays");

    /// <summary>
    /// Reads a platform retention default. An unset key falls back to
    /// <see cref="FallbackRetentionDays"/>; a configured value of zero or less disables the
    /// default, leaving only explicitly configured tenants purged.
    /// </summary>
    private int? ResolveDefault(string key)
    {
        var days = configuration.GetValue<int?>(key) ?? FallbackRetentionDays;
        return days > 0 ? days : null;
    }

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
    /// Resolves each tenant's effective retention (own config, else the platform default) and
    /// deletes expired records in batches.
    /// </summary>
    internal async Task PurgeExpiredRecordsAsync(CancellationToken ct)
    {
        var defaultReadDays = DefaultReadRetentionDays;
        var defaultMutationDays = DefaultMutationRetentionDays;

        await using var configContext = await contextFactory.CreateDbContextAsync(ct);

        var configs = await configContext.TenantAuditConfig
            .Where(c => c.ReadAuditRetentionDays != null || c.MutationAuditRetentionDays != null)
            .Select(c => new
            {
                c.TenantId,
                c.ReadAuditRetentionDays,
                c.MutationAuditRetentionDays
            })
            .ToDictionaryAsync(c => c.TenantId, ct);

        // With a platform default in force every tenant is a purge candidate, not only the ones
        // carrying a config row. Configured tenants are unioned in so a config row for a tenant
        // missing from the table is still honoured.
        var tenantIds = new HashSet<Guid>(configs.Keys);
        if (defaultReadDays is not null || defaultMutationDays is not null)
        {
            var allTenantIds = await configContext.Tenants
                .AsNoTracking()
                .Select(t => t.Id)
                .ToListAsync(ct);
            tenantIds.UnionWith(allTenantIds);
        }

        if (tenantIds.Count == 0)
        {
            logger.LogDebug("No tenants with effective audit retention; skipping purge");
            return;
        }

        var cycleReadDeleted = 0;
        var cycleMutationDeleted = 0;

        foreach (var tenantId in tenantIds)
        {
            var config = configs.GetValueOrDefault(tenantId);
            var readDays = config?.ReadAuditRetentionDays ?? defaultReadDays;
            var mutationDays = config?.MutationAuditRetentionDays ?? defaultMutationDays;

            try
            {
                var readDeleted = 0;
                var mutationDeleted = 0;

                if (readDays is { } read)
                {
                    var cutoff = DateTime.UtcNow.AddDays(-read);
                    readDeleted = await PurgeBatchedAsync(
                        tenantId, "read_access_log", cutoff, ct);
                }

                if (mutationDays is { } mutation)
                {
                    var cutoff = DateTime.UtcNow.AddDays(-mutation);
                    mutationDeleted = await PurgeBatchedAsync(
                        tenantId, "mutation_audit_log", cutoff, ct);
                }

                cycleReadDeleted += readDeleted;
                cycleMutationDeleted += mutationDeleted;

                if (readDeleted > 0 || mutationDeleted > 0)
                {
                    logger.LogInformation(
                        "Audit retention purge for tenant {TenantId}: {ReadDeleted} read, {MutationDeleted} mutation records deleted",
                        tenantId, readDeleted, mutationDeleted);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "Audit retention purge failed for tenant {TenantId}; continuing with next tenant",
                    tenantId);
            }
        }

        // Unconditional: a zero-deletion cycle is the signature of a purge that cannot see its
        // rows, which a per-tenant log gated on a non-zero count cannot distinguish from idleness.
        logger.LogInformation(
            "Audit retention purge swept {TenantCount} tenants: {ReadDeleted} read, {MutationDeleted} mutation records deleted",
            tenantIds.Count, cycleReadDeleted, cycleMutationDeleted);
    }

    /// <summary>
    /// Deletes audit records from the specified table older than the cutoff.
    /// </summary>
    /// <returns>Total number of records deleted.</returns>
    internal virtual Task<int> PurgeBatchedAsync(
        Guid tenantId, string table, DateTime cutoff, CancellationToken ct) =>
        contextFactory.PurgeOlderThanAsync(tenantId, table, "created_at", cutoff, BatchSize, ct);
}
