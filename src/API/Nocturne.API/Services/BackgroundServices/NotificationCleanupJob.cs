using Nocturne.Core.Contracts.Jobs;
using Nocturne.Infrastructure.Data.Abstractions;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Purges a tenant's in-app notifications archived more than <see cref="RetentionPeriod"/> ago.
/// </summary>
/// <remarks>
/// Only archived notifications (those already dismissed or actioned by the user) are eligible
/// for deletion. Active or unread notifications are never touched. Each tenant's archived rows
/// older than the retention period are deleted, so a rule that a notice is sent only once is kept
/// in <see cref="Nocturne.API.Services.Notifications.IOwnerNoticeLedger"/>, not in archived
/// notifications.
/// </remarks>
public class NotificationCleanupJob(ILogger<NotificationCleanupJob> logger) : IScheduledJob
{
    internal const string JobName = "notification-cleanup";

    internal static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    internal static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(30);

    public string Name => JobName;

    public JobEnrolment Enrolment => JobEnrolment.PerTenant;

    public async Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
    {
        var cutoff = run.StartedAt.UtcDateTime - RetentionPeriod;
        var deleted = await run.Services.GetRequiredService<IInAppNotificationRepository>()
            .DeleteArchivedBeforeAsync(cutoff, ct);

        if (deleted > 0)
        {
            logger.LogInformation(
                "Cleaned up {Count} archived notifications of tenant {TenantId} older than {Cutoff}",
                deleted, run.Tenant!.TenantId, cutoff);
        }

        return JobOutcome.Resume(null, run.StartedAt + Interval);
    }
}
