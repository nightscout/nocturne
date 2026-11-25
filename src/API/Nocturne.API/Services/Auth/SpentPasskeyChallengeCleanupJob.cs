using Nocturne.Core.Contracts.Jobs;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Removes spent passkey challenges whose tokens have expired. An expired token is refused before
/// its spend is looked at, so this only keeps the table from growing.
/// The table is not tenant-scoped (see <see cref="Nocturne.Infrastructure.Data.Entities.SpentPasskeyChallengeEntity"/>), so this is one platform job.
/// </summary>
/// <remarks>
/// A row is kept for <see cref="OAuthCodeCleanupJob.RetentionBuffer"/> past its expiry, so an
/// instance whose clock runs behind the one that minted the token still finds the spend.
/// </remarks>
public class SpentPasskeyChallengeCleanupJob(ILogger<SpentPasskeyChallengeCleanupJob> logger) : IScheduledJob
{
    internal const string JobName = "spent-passkey-challenge-cleanup";

    internal const int BatchSize = 10_000;

    public string Name => JobName;

    public JobEnrolment Enrolment => JobEnrolment.Platform;

    public async Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
    {
        var cutoff = run.StartedAt.UtcDateTime - OAuthCodeCleanupJob.RetentionBuffer;
        var challenges = run.Services.GetRequiredService<ISpentPasskeyChallengeRepository>();

        var deleted = 0;
        int batch;
        do
        {
            batch = await challenges.DeleteExpiredAsync(cutoff, BatchSize, ct);
            deleted += batch;
        }
        while (batch == BatchSize);

        if (deleted > 0)
            logger.LogInformation("Removed {SpentPasskeyChallenges} expired spent passkey challenges", deleted);

        return JobOutcome.Resume(null, run.StartedAt + OAuthCodeCleanupJob.Interval);
    }
}
