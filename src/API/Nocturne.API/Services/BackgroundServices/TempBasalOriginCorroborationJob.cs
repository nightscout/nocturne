using Nocturne.API.Services.Caching;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Runs <see cref="TempBasalOriginCorroborator"/> for a tenant every <see cref="Interval"/>, on the temp
/// basals written at least <see cref="SettlingDelay"/> ago, first reopening those that snapshots written
/// since the last run now lie near (<see cref="TempBasalOriginCorroborator.ReopenNearNewSnapshotsAsync"/>).
/// </summary>
/// <remarks>
/// The cursor's <see cref="JobCursor.At"/> is the latest snapshot write a run has reopened temp basals
/// for. A tenant whose temp basals changed has its cached treatment and statistics reads dropped.
/// </remarks>
internal sealed class TempBasalOriginCorroborationJob(ILogger<TempBasalOriginCorroborationJob> logger) : IScheduledJob
{
    internal const string JobName = "temp-basal-origin-corroboration";

    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long a temp basal is left after it is written, for the devicestatus uploaded around it
    /// to arrive: an uploader sends the two separately, and a phone that was offline sends both late.
    /// </summary>
    internal static readonly TimeSpan SettlingDelay = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How far back a tenant's first run looks for snapshot writes. A snapshot written longer before
    /// that, beside a temp that had already settled, is not looked at again.
    /// </summary>
    internal static readonly TimeSpan FirstLookback = TimeSpan.FromDays(7);

    public string Name => JobName;

    public JobEnrolment Enrolment => JobEnrolment.PerTenant;

    public async Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
    {
        var tenantId = run.Tenant!.TenantId;
        var now = run.StartedAt.UtcDateTime;
        var snapshotsWrittenAfter = run.Cursor?.At is { } through
            ? through.UtcDateTime - TempBasalOriginCorroborator.CommitMargin
            : now - FirstLookback;

        int written;
        DateTime? snapshotsWrittenThrough;
        await using (var db = await run.Services.GetRequiredService<INocturneDbContextFactory>().CreateAsync(ct))
        {
            (_, snapshotsWrittenThrough) = await TempBasalOriginCorroborator.ReopenNearNewSnapshotsAsync(
                db, tenantId, snapshotsWrittenAfter, TenantSweepPaging.PageSize, ct);
            written = await TempBasalOriginCorroborator.CorroborateTenantAsync(
                db,
                tenantId,
                now - SettlingDelay,
                TenantSweepPaging.PageSize,
                TenantSweepPaging.PauseAfterWrite,
                TenantSweepPaging.PauseAfterRead,
                ct);
        }

        if (written > 0)
        {
            await run.Services.GetRequiredService<ILegacyReadCacheInvalidation>().WrittenAsync([typeof(TempBasal)]);
            await run.Services.GetRequiredService<ITenantCache>().RemoveFamilyAsync(TenantCacheFamily.Statistics, ct);
            logger.LogInformation(
                "Temp basal origin corroboration judged {Written} temp basal(s) of tenant {TenantId}", written, tenantId);
        }

        var cursor = snapshotsWrittenThrough is { } latest
            ? new JobCursor(At: new DateTimeOffset(DateTime.SpecifyKind(latest, DateTimeKind.Utc)))
            : run.Cursor;
        return JobOutcome.Resume(cursor, run.StartedAt + Interval);
    }
}
