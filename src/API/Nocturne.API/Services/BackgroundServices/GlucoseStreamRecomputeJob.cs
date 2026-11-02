using Nocturne.Core.Contracts.Jobs;
using Nocturne.Core.Contracts.V4.Repositories;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Recomputes which stream every glucose bucket of a tenant's history shows
/// (<see cref="ISensorGlucoseRepository.RecomputeStreamsAsync"/>): requested when a CGM device's
/// place in the priority order moves, when readings are back-stamped to a device, and when a delete
/// or restore is too wide to recompute inline; and run daily, which repairs whatever a write path
/// left out of date.
/// </summary>
/// <remarks>
/// A run walks the whole history and keeps no cursor: a crashed run starts again, and the walk writes
/// only readings still out of date. A request made while a run holds the row brings the row due again
/// when it finishes, so a device moved mid-walk is walked again. A requested run cannot be told from
/// the daily one, so readings it changes are logged at Information. An inactive tenant's row is
/// parked for <see cref="IScheduledJob.InactiveTenantRecheck"/>.
/// </remarks>
internal sealed class GlucoseStreamRecomputeJob(ILogger<GlucoseStreamRecomputeJob> logger) : IScheduledJob
{
    internal static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    public string Name => ISensorGlucoseRepository.RecomputeStreamsJob;

    public JobEnrolment Enrolment => JobEnrolment.PerTenant;

    public TimeSpan Spread => Interval;

    public async Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
    {
        if (!run.Tenant!.IsActive)
            return JobOutcome.Resume(null, run.StartedAt + IScheduledJob.InactiveTenantRecheck);

        var changed = await run.Services.GetRequiredService<ISensorGlucoseRepository>().RecomputeStreamsAsync(ct);
        if (changed > 0)
            logger.LogInformation(
                "Recomputed the glucose streams of tenant {TenantSlug}: {Changed} readings changed",
                run.Tenant.Slug, changed);

        return JobOutcome.Resume(null, run.StartedAt + Interval);
    }
}
