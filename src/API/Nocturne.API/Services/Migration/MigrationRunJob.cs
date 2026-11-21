using Nocturne.Core.Contracts.Jobs;

namespace Nocturne.API.Services.Migration;

/// <summary>
/// Runs a tenant's live migration job (<see cref="IMigrationJobService.RunSliceAsync"/>) a slice at
/// a time: a slice that stops short is due again at once and goes on from the job's checkpoint.
/// </summary>
/// <remarks>
/// The job stores its own status and position, failure included, so the ledger row carries no
/// cursor and a failed job ends rather than being retried. A slice the host stops throws, which
/// leaves the row due for whichever process claims it next, and so does one that finds its record
/// written by another holder, which is retried from that record. Each tenant's slices are due at
/// the time the last one started, so tenants waiting on <see cref="MaxConcurrency"/> take turns.
/// </remarks>
internal sealed class MigrationRunJob(IMigrationJobService migrations) : IScheduledJob
{
    public string Name => MigrationJobService.RunJobName;

    public JobEnrolment Enrolment => JobEnrolment.OnRequest;

    public int MaxConcurrency => 3;

    public async Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct) =>
        run.Tenant is { } tenant && await migrations.RunSliceAsync(tenant, ct)
            ? JobOutcome.Resume(cursor: null, run.StartedAt)
            : JobOutcome.Done;
}
