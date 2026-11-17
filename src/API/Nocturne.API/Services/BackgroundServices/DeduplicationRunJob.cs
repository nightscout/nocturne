using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Jobs;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Runs a tenant's requested full deduplication run (<see cref="IDeduplicationAdmin.RunAsync"/>) a slice
/// at a time: a slice that stops short is due again at once and resumes from where it stopped.
/// </summary>
/// <remarks>
/// The run stores its own status, failure included, so a failed run ends rather than being retried.
/// One run at a time per process: a run reads and links a tenant's whole history.
/// </remarks>
internal sealed class DeduplicationRunJob : IScheduledJob
{
    public string Name => IDeduplicationAdmin.RunJob;

    public JobEnrolment Enrolment => JobEnrolment.OnRequest;

    public int MaxConcurrency => 1;

    public async Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct) =>
        await run.Services.GetRequiredService<IDeduplicationAdmin>().RunAsync(run.Cursor, ct) is { } next
            ? JobOutcome.Resume(next, run.StartedAt)
            : JobOutcome.Done;
}
