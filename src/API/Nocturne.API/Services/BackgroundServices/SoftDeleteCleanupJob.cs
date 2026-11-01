using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Hard-deletes a tenant's soft-deleted records past its retention period, in batches to avoid WAL
/// bloat.
/// </summary>
public class SoftDeleteCleanupJob(
    IConfiguration configuration,
    ILogger<SoftDeleteCleanupJob> logger) : IScheduledJob
{
    internal const string JobName = "soft-delete-cleanup";

    internal static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private const int BatchSize = 10_000;

    public string Name => JobName;

    public JobEnrolment Enrolment => JobEnrolment.PerTenant;

    /// <summary>
    /// Every table this job purges: the one behind each tenant-scoped soft-deletable entity.
    /// Restricted to tenant-scoped entities because <see cref="RetentionPurgeExtensions.PurgeOlderThanAsync"/>
    /// relies on row-level security to keep its raw DELETE inside one tenant.
    /// </summary>
    internal static IReadOnlyList<PurgeTable> SoftDeletableTables(IModel model) =>
        [.. model.GetEntityTypes()
            .Where(t => typeof(ISoftDeletable).IsAssignableFrom(t.ClrType)
                        && typeof(ITenantScoped).IsAssignableFrom(t.ClrType)
                        && t.GetTableName() is not null)
            .Select(t => new PurgeTable(t.GetTableName()!, DeduplicationService.LinkedRecordTypeKeyOf(t.ClrType)))
            .Distinct()
            .OrderBy(t => t.Table, StringComparer.Ordinal)];

    /// <summary>
    /// A table the job purges, with the <see cref="Nocturne.Core.Models.RecordTypeKeys"/> key its
    /// rows are linked under when deduplication covers them.
    /// </summary>
    internal sealed record PurgeTable(string Table, string? LinkedRecordType);

    public async Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
    {
        var tenantId = run.Tenant!.TenantId;
        var contexts = run.Services.GetRequiredService<INocturneDbContextFactory>();

        IReadOnlyList<PurgeTable> tables;
        int? configuredDays;
        await using (var db = await contexts.CreateAsync(ct))
        {
            tables = SoftDeletableTables(db.Model);
            configuredDays = await db.TenantDataRetentionConfig
                .Select(c => c.SoftDeleteRetentionDays)
                .FirstOrDefaultAsync(ct);
        }

        var retentionDays = SoftDeleteRetentionPolicy.ResolveDays(configuredDays, configuration);
        var minAge = TimeSpan.FromDays(retentionDays);

        var totalDeleted = 0;
        foreach (var table in tables)
        {
            totalDeleted += await contexts.PurgeOlderThanAsync(
                tenantId, table.Table, AgeColumn, minAge, BatchSize, table.LinkedRecordType, ct);
        }

        if (totalDeleted > 0)
        {
            logger.LogInformation(
                "Soft-delete cleanup for tenant {TenantId}: hard-deleted {Count} expired records "
                + "(retention: {Days} days)",
                tenantId, totalDeleted, retentionDays);
        }

        return JobOutcome.Resume(null, run.StartedAt + Interval);
    }

    /// <summary>Age column on every soft-deletable table.</summary>
    internal const string AgeColumn = "deleted_at";
}
