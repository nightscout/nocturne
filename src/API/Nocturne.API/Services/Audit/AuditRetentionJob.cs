using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Audit;

/// <summary>
/// Purges a tenant's expired audit log records at the platform default retention
/// (<c>Audit:DefaultReadAuditRetentionDays</c> / <c>Audit:DefaultMutationRetentionDays</c>); the
/// tenant's own <see cref="TenantAuditConfigEntity"/> value overrides the default in either
/// direction. Deletes in batches to avoid WAL bloat.
/// </summary>
public class AuditRetentionJob(
    IConfiguration configuration,
    ILogger<AuditRetentionJob> logger) : IScheduledJob
{
    internal const string JobName = "audit-retention";

    internal static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private const int BatchSize = 10_000;

    public string Name => JobName;

    public JobEnrolment Enrolment => JobEnrolment.PerTenant;

    public async Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
    {
        var tenantId = run.Tenant!.TenantId;
        var contexts = run.Services.GetRequiredService<INocturneDbContextFactory>();

        int? configuredRead, configuredMutation;
        await using (var db = await contexts.CreateAsync(ct))
        {
            var config = await db.TenantAuditConfig
                .Where(c => c.TenantId == tenantId)
                .Select(c => new { c.ReadAuditRetentionDays, c.MutationAuditRetentionDays })
                .FirstOrDefaultAsync(ct);
            configuredRead = config?.ReadAuditRetentionDays;
            configuredMutation = config?.MutationAuditRetentionDays;
        }

        var readDeleted = 0;
        var mutationDeleted = 0;

        if (AuditRetentionPolicy.ResolveReadDays(configuredRead, configuration) is { } read)
        {
            readDeleted = await PurgeBatchedAsync(
                contexts, tenantId, "read_access_log", TimeSpan.FromDays(read), ct);
        }

        if (AuditRetentionPolicy.ResolveMutationDays(configuredMutation, configuration) is { } mutation)
        {
            mutationDeleted = await PurgeBatchedAsync(
                contexts, tenantId, "mutation_audit_log", TimeSpan.FromDays(mutation), ct);
        }

        if (readDeleted > 0 || mutationDeleted > 0)
        {
            logger.LogInformation(
                "Audit retention purge for tenant {TenantId}: {ReadDeleted} read, {MutationDeleted} mutation records deleted",
                tenantId, readDeleted, mutationDeleted);
        }

        return JobOutcome.Resume(null, run.StartedAt + Interval);
    }

    /// <summary>
    /// Deletes audit records from the specified table older than the retention window.
    /// </summary>
    /// <returns>Total number of records deleted.</returns>
    internal virtual Task<int> PurgeBatchedAsync(
        INocturneDbContextFactory contexts, Guid tenantId, string table, TimeSpan minAge, CancellationToken ct) =>
        contexts.PurgeOlderThanAsync(tenantId, table, AgeColumn, minAge, BatchSize, ct: ct);

    /// <summary>Age column on both audit tables.</summary>
    internal const string AgeColumn = "created_at";
}
