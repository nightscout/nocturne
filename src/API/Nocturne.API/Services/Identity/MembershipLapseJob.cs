using Nocturne.Core.Contracts.Jobs;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Services.Identity;

/// <summary>
/// Narrows, every minute, the memberships whose reason has ended (<see cref="IMembershipReasonLapses"/>).
/// Until then such a membership is absent to every access read that does not narrow it itself, so the
/// interval bounds how long a member can go without the access their other reasons still give.
/// One platform job: a membership is found across every tenant and narrowed on its own tenant.
/// </summary>
public class MembershipLapseJob(TimeProvider time, ILogger<MembershipLapseJob> logger) : IScheduledJob
{
    internal const string JobName = "membership-lapse";

    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    public string Name => JobName;

    public JobEnrolment Enrolment => JobEnrolment.Platform;

    public async Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
    {
        var sweep = await run.Services.GetRequiredService<IMembershipReasonLapses>()
            .NarrowLapsedAsync(time.GetUtcNow().UtcDateTime, ct);

        if (sweep.Recomputed > 0 || sweep.Failed > 0)
            logger.LogInformation(
                "Narrowed {Recomputed} memberships whose reason had ended; {Failed} could not be narrowed",
                sweep.Recomputed, sweep.Failed);

        return JobOutcome.Resume(null, run.StartedAt + Interval);
    }
}
