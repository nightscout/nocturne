using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Background service that hard-deletes soft-deleted records past their retention period.
/// Runs every 24 hours, deleting in batches to avoid WAL bloat.
/// </summary>
public class SoftDeleteCleanupService(
    INocturneDbContextFactory contextFactory,
    IConfiguration configuration,
    ILogger<SoftDeleteCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private const int BatchSize = 10_000;

    /// <summary>
    /// Every table this service purges: the one behind each tenant-scoped soft-deletable entity.
    /// Restricted to tenant-scoped entities because <see cref="PurgeBatchedAsync"/> relies on
    /// row-level security to keep its raw DELETE inside one tenant.
    /// </summary>
    internal static IReadOnlyList<string> SoftDeletableTables(IModel model) =>
        [.. model.GetEntityTypes()
            .Where(t => typeof(ISoftDeletable).IsAssignableFrom(t.ClrType)
                        && typeof(ITenantScoped).IsAssignableFrom(t.ClrType))
            .Select(t => t.GetTableName())
            .OfType<string>()
            .Distinct()
            .Order()];

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
                logger.LogWarning(ex, "Soft-delete cleanup failed; will retry next cycle");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// Iterates all tenants and hard-deletes soft-deleted records past their retention period.
    /// </summary>
    internal async Task PurgeExpiredRecordsAsync(CancellationToken ct)
    {
        List<Guid> allTenantIds;
        IReadOnlyList<string> tables;
        await using (var platform = await contextFactory.CreatePlatformAsync(ct))
        {
            allTenantIds = await platform.Tenants
                .Select(t => t.Id)
                .ToListAsync(ct);
            tables = SoftDeletableTables(platform.Model);
        }

        foreach (var tenantId in allTenantIds)
        {
            try
            {
                var retentionDays = SoftDeleteRetentionPolicy.ResolveDays(
                    await ReadConfiguredDaysAsync(tenantId, ct), configuration);
                var minAge = TimeSpan.FromDays(retentionDays);

                var totalDeleted = 0;

                foreach (var table in tables)
                {
                    var tableDeleted = await PurgeBatchedAsync(tenantId, table, minAge, ct);
                    totalDeleted += tableDeleted;
                }

                var orphanedLinks = await CleanupOrphanedLinkedRecordsAsync(tenantId, ct);

                if (totalDeleted > 0 || orphanedLinks > 0)
                {
                    logger.LogInformation(
                        "Soft-delete cleanup for tenant {TenantId}: hard-deleted {Count} expired records "
                        + "and {OrphanedLinks} orphaned links (retention: {Days} days)",
                        tenantId, totalDeleted, orphanedLinks, retentionDays);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "Soft-delete cleanup failed for tenant {TenantId}; continuing with next tenant",
                    tenantId);
            }
        }
    }

    private async Task<int?> ReadConfiguredDaysAsync(Guid tenantId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateForTenantAsync(tenantId, ct);
        return await db.TenantDataRetentionConfig
            .Select(c => c.SoftDeleteRetentionDays)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Deletes records from the specified table whose deleted_at is older than the window.
    /// </summary>
    /// <returns>Total number of records deleted.</returns>
    private Task<int> PurgeBatchedAsync(
        Guid tenantId, string table, TimeSpan minAge, CancellationToken ct) =>
        contextFactory.PurgeOlderThanAsync(tenantId, table, AgeColumn, minAge, BatchSize, ct);

    /// <summary>Age column on every soft-deletable table.</summary>
    internal const string AgeColumn = "deleted_at";

    /// <summary>
    /// Removes linked_records that reference hard-deleted records, and links of a record type that
    /// no longer has a table behind it.
    /// </summary>
    /// <returns>The number of links deleted.</returns>
    private async Task<int> CleanupOrphanedLinkedRecordsAsync(Guid tenantId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateForTenantAsync(tenantId, ct);
        return await DeduplicationService.DeleteOrphanedLinksAsync(db, ct);
    }
}
