using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Background service that hard-deletes soft-deleted records past their retention period.
/// Runs every 24 hours, deleting in batches to avoid WAL bloat.
/// </summary>
public class SoftDeleteCleanupService(
    IDbContextFactory<NocturneDbContext> contextFactory,
    IConfiguration configuration,
    ILogger<SoftDeleteCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private const int BatchSize = 10_000;

    /// <summary>
    /// All v4 tables with a deleted_at column.
    /// </summary>
    private static readonly string[] V4Tables =
    [
        "aps_snapshots", "basal_schedules", "bg_checks", "bolus_calculations",
        "boluses", "calibrations", "carb_intakes", "carb_ratio_schedules",
        "device_events", "device_status_extras",
        "devices", "meter_glucose", "notes", "patient_devices",
        "patient_insulins", "patient_records", "pump_snapshots",
        "sensitivity_schedules", "sensor_glucose", "target_range_schedules",
        "temp_basals", "therapy_settings", "uploader_snapshots"
    ];

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
        await using var configContext = await contextFactory.CreateDbContextAsync(ct);

        // Get per-tenant retention config
        var configs = await configContext.TenantDataRetentionConfig
            .IgnoreQueryFilters()
            .Select(c => new { c.TenantId, c.SoftDeleteRetentionDays })
            .ToListAsync(ct);

        // Also get all tenants that might have soft-deleted records but no config
        var allTenantIds = await configContext.Tenants
            .Select(t => t.Id)
            .ToListAsync(ct);

        var configMap = configs.ToDictionary(c => c.TenantId, c => c.SoftDeleteRetentionDays);

        foreach (var tenantId in allTenantIds)
        {
            try
            {
                var retentionDays = SoftDeleteRetentionPolicy.ResolveDays(
                    configMap.GetValueOrDefault(tenantId), configuration);
                var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

                var totalDeleted = 0;

                foreach (var table in V4Tables)
                {
                    var tableDeleted = await PurgeBatchedAsync(tenantId, table, cutoff, ct);
                    totalDeleted += tableDeleted;
                }

                // Clean up orphaned linked_records
                await CleanupOrphanedLinkedRecordsAsync(tenantId, ct);

                if (totalDeleted > 0)
                {
                    logger.LogInformation(
                        "Soft-delete cleanup for tenant {TenantId}: hard-deleted {Count} expired records (retention: {Days} days)",
                        tenantId, totalDeleted, retentionDays);
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

    /// <summary>
    /// Deletes records from the specified table with deleted_at before the cutoff, in batches
    /// of <see cref="BatchSize"/> to avoid WAL bloat and long-running transactions.
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
                $"DELETE FROM {table} WHERE ctid IN (SELECT ctid FROM {table} WHERE deleted_at < {{0}} LIMIT {BatchSize})",
                [cutoff], ct);
#pragma warning restore EF1002

            totalDeleted += batchDeleted;
        }
        while (batchDeleted >= BatchSize);

        return totalDeleted;
    }

    /// <summary>
    /// Removes linked_records that reference hard-deleted records. Best-effort: orphans don't
    /// cause incorrect behavior, just stale dedup metadata.
    /// </summary>
    private async Task CleanupOrphanedLinkedRecordsAsync(Guid tenantId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        await db.Database.ExecuteSqlRawAsync(
            "SELECT set_config('app.current_tenant_id', {0}, false)",
            [tenantId.ToString()], ct);

        // RecordType enum values are stored lowercase (e.g. "sensorglucose", "bolus")
        var dedupTypes = new Dictionary<string, string>
        {
            ["sensorglucose"] = "sensor_glucose",
            ["bolus"] = "boluses",
            ["carbintake"] = "carb_intakes",
            ["bgcheck"] = "bg_checks",
            ["tempbasal"] = "temp_basals"
        };

        foreach (var (recordType, sourceTable) in dedupTypes)
        {
#pragma warning disable EF1002
            await db.Database.ExecuteSqlRawAsync(
                $"DELETE FROM linked_records WHERE record_type = {{0}} AND record_id NOT IN (SELECT id FROM {sourceTable})",
                [recordType], ct);
#pragma warning restore EF1002
        }
    }
}
