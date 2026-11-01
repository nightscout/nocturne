using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.Core.Contracts.Jobs;

/// <summary>
/// A unit of recurring or one-shot work the job scheduler runs for each of its <c>job_ledger</c>
/// rows when the row comes due.
/// </summary>
/// <remarks>
/// A job is a singleton and holds no state between runs: what it needs to resume is in
/// <see cref="JobRun.Cursor"/>, and what it needs to run it resolves from <see cref="JobRun.Services"/>.
/// A run that throws keeps its cursor and is retried after a backoff; it is never skipped.
/// </remarks>
public interface IScheduledJob
{
    /// <summary>The longest a failing job waits between attempts, unless it sets a lower one.</summary>
    static readonly TimeSpan DefaultMaxBackoff = TimeSpan.FromHours(1);

    /// <summary>
    /// How long a per-tenant job parks an inactive tenant's row before it looks again, rather than
    /// taking a slot every interval: a reactivated tenant is picked up without any reactivation path
    /// having to reach the ledger.
    /// </summary>
    static readonly TimeSpan InactiveTenantRecheck = TimeSpan.FromHours(1);

    /// <summary>
    /// The job's row key in <c>job_ledger</c>. Only rows of registered names are claimed, so a row no
    /// deployed version registers stays due and is scanned past on every claim: the change that
    /// removes or renames a job deletes its rows (<c>DELETE FROM job_ledger WHERE job = '...'</c> in a
    /// migration).
    /// </summary>
    string Name { get; }

    /// <summary>How the job's rows come to exist.</summary>
    JobEnrolment Enrolment { get; }

    /// <summary>The longest wait between failed attempts; capped at <see cref="DefaultMaxBackoff"/>.</summary>
    TimeSpan MaxBackoff => DefaultMaxBackoff;

    /// <summary>
    /// The window a per-tenant job's rows are first due across when they are enrolled, each at its
    /// <see cref="JobPhase"/>; zero, the default, makes every row due at enrolment.
    /// </summary>
    TimeSpan Spread => TimeSpan.Zero;

    /// <summary>
    /// The most runs of this job's <see cref="ConcurrencyGroup"/> one process holds at once, so jobs
    /// whose runs are all stuck (an upstream outage holding each run to its timeout) leave the other
    /// slots to the other jobs. Every job of one group declares the same cap.
    /// </summary>
    int MaxConcurrency => int.MaxValue;

    /// <summary>
    /// The jobs whose runs count together against <see cref="MaxConcurrency"/>; by default the job alone.
    /// </summary>
    string ConcurrencyGroup => Name;

    /// <summary>Runs the job once for its row.</summary>
    Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct);
}

/// <summary>How an <see cref="IScheduledJob"/>'s rows come to exist.</summary>
public enum JobEnrolment
{
    /// <summary>One row per tenant, active or not, due at once when the scheduler first sees the tenant.</summary>
    PerTenant,

    /// <summary>One row with no tenant, due at once when the scheduler first sees the job.</summary>
    Platform,

    /// <summary>Rows come only from <see cref="IJobLedger.RequestAsync"/> or a migration.</summary>
    OnRequest,
}

/// <summary>One run of an <see cref="IScheduledJob"/> for one ledger row.</summary>
/// <param name="Tenant">The row's tenant, or null for a platform row.</param>
/// <param name="Cursor">Where the previous run stopped, or null.</param>
/// <param name="Attempts">How many runs in a row have failed before this one.</param>
/// <param name="Services">The run's scope, opened for <paramref name="Tenant"/> and reading as no one.</param>
/// <param name="StartedAt">When the scheduler claimed the row.</param>
public sealed record JobRun(
    TenantContext? Tenant,
    JobCursor? Cursor,
    int Attempts,
    IServiceProvider Services,
    DateTimeOffset StartedAt);
