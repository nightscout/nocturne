using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Jobs;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Every <see cref="Interval"/>, merges an active tenant's duplicate canonical groups created since its
/// reconcile cursor (<see cref="IDeduplicationService.ReconcileNewLinksAsync(int, int, CancellationToken)"/>),
/// then runs <see cref="IDeduplicationService.RepairStateSpanPrimariesAsync(CancellationToken)"/> and
/// <see cref="IDeduplicationService.PruneSingletonLinksAsync(int, int, CancellationToken)"/>.
/// </summary>
/// <remarks>
/// Both cursors are kept in <c>dedup_reconcile_state</c>, not in the ledger, so the run's
/// <see cref="JobCursor"/> stays empty. A tenant whose merge lock is held has its merges left to the
/// next run, which is not a failure. An inactive tenant's row is parked for
/// <see cref="IScheduledJob.InactiveTenantRecheck"/>.
/// </remarks>
internal sealed class DeduplicationReconciliationJob(ILogger<DeduplicationReconciliationJob> logger) : IScheduledJob
{
    internal const string JobName = "dedup-reconciliation";

    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    private const int BatchSize = 5000;
    private const int MaxBatchesPerRun = 4;
    private const int PruneBatchSize = 10_000;
    private const int MaxPruneBatchesPerRun = 10;

    public string Name => JobName;

    public JobEnrolment Enrolment => JobEnrolment.PerTenant;

    public TimeSpan Spread => Interval;

    public async Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
    {
        if (!run.Tenant!.IsActive)
            return JobOutcome.Resume(null, run.StartedAt + IScheduledJob.InactiveTenantRecheck);

        var dedup = run.Services.GetRequiredService<IDeduplicationService>();
        var result = await dedup.ReconcileNewLinksAsync(BatchSize, MaxBatchesPerRun, ct);
        var repaired = await dedup.RepairStateSpanPrimariesAsync(ct);
        var pruned = await dedup.PruneSingletonLinksAsync(PruneBatchSize, MaxPruneBatchesPerRun, ct);

        logger.LogDebug(
            "Reconciled deduplication for tenant {TenantSlug}: {GroupsMerged} groups merged, {SpanPrimariesRepaired} state span primaries repaired, {LinksPruned} singleton links pruned",
            run.Tenant.Slug, result.GroupsMerged, repaired, pruned);

        return JobOutcome.Resume(null, run.StartedAt + Interval);
    }
}
