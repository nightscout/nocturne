using Nocturne.Core.Contracts.Jobs;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Rewrites each tenant's stored APS snapshots into the shape the API now writes, through
/// <see cref="ApsSnapshotCurvesRewriter"/>, <see cref="PagesPerRun"/> pages a run.
/// </summary>
/// <remarks>
/// The decomposer writes the new shape, so the job is done for a tenant once a run walks off the end
/// of its snapshots. Until then each run resumes from the last row the previous one walked.
/// </remarks>
internal sealed class ApsSnapshotCurvesJob(
    ILogger<ApsSnapshotCurvesJob> logger,
    Func<NocturneDbContext, Guid, (DateTime At, Guid Id)?, CancellationToken, Task<ApsSnapshotCurvesRewriter.Pass>>? rewrite = null)
    : IScheduledJob
{
    internal const string JobName = "aps-snapshot-curves";

    internal const int PagesPerRun = 20;

    private readonly Func<NocturneDbContext, Guid, (DateTime At, Guid Id)?, CancellationToken, Task<ApsSnapshotCurvesRewriter.Pass>> _rewrite =
        rewrite ?? ((db, tenantId, after, ct) => ApsSnapshotCurvesRewriter.RewriteTenantAsync(
            db, tenantId, after, PagesPerRun, TenantSweepPaging.PageSize, TenantSweepPaging.PauseAfterWrite, TenantSweepPaging.PauseAfterRead, ct));

    public string Name => JobName;

    public JobEnrolment Enrolment => JobEnrolment.PerTenant;

    public async Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
    {
        var tenantId = run.Tenant!.TenantId;
        var after = run.Cursor is { At: { } at, Id: { } id } ? (at.UtcDateTime, id) : ((DateTime, Guid)?)null;

        ApsSnapshotCurvesRewriter.Pass pass;
        await using (var db = await run.Services.GetRequiredService<INocturneDbContextFactory>().CreateAsync(ct))
        {
            pass = await _rewrite(db, tenantId, after, ct);
        }

        if (pass.Changed > 0)
            logger.LogInformation("Rewrote {Changed} APS snapshot(s) for tenant {TenantId}", pass.Changed, tenantId);

        return pass.Next is { } next
            ? JobOutcome.Resume(new JobCursor(At: new DateTimeOffset(next.At, TimeSpan.Zero), Id: next.Id), run.StartedAt)
            : JobOutcome.Done;
    }
}
