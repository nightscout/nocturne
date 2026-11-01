using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Services.Notifications;

/// <summary>
/// Every <see cref="Interval"/>, archives an active tenant's pending in-app notifications whose
/// resolution conditions are met, and broadcasts each archive. An inactive tenant's row is parked
/// for <see cref="IScheduledJob.InactiveTenantRecheck"/>.
/// </summary>
internal sealed class NotificationResolutionJob(ILogger<NotificationResolutionJob> logger) : IScheduledJob
{
    internal const string JobName = "notification-resolution";

    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>A failing run is retried at the job's cadence, so a run of failures does not leave expired notifications standing.</summary>
    internal static readonly TimeSpan ResolutionMaxBackoff = Interval;

    public string Name => JobName;

    public JobEnrolment Enrolment => JobEnrolment.PerTenant;

    public TimeSpan MaxBackoff => ResolutionMaxBackoff;

    public TimeSpan Spread => Interval;

    public async Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
    {
        if (!run.Tenant!.IsActive)
            return JobOutcome.Resume(null, run.StartedAt + IScheduledJob.InactiveTenantRecheck);

        await ResolveAsync(run.Services, run.StartedAt.UtcDateTime, ct);

        return JobOutcome.Resume(null, run.StartedAt + Interval);
    }

    private async Task ResolveAsync(IServiceProvider services, DateTime now, CancellationToken ct)
    {
        var repository = services.GetRequiredService<IInAppNotificationRepository>();
        var notifications = await repository.GetPendingResolutionAsync(ct);
        if (notifications.Count == 0)
            return;

        var broadcastService = services.GetRequiredService<ISignalRBroadcastService>();
        foreach (var notification in notifications)
        {
            try
            {
                var conditions = InAppNotificationRepository.DeserializeConditions(notification.ResolutionConditionsJson);
                if (conditions?.ExpiresAt is not { } expiresAt || expiresAt > now)
                    continue;

                logger.LogInformation(
                    "Auto-resolving notification {NotificationId} due to expiry at {ExpiresAt}",
                    notification.Id, expiresAt);

                var archived = await repository.ArchiveAsync(notification.Id, NotificationArchiveReason.Expired, ct);
                if (archived is null)
                    continue;

                try
                {
                    await broadcastService.BroadcastNotificationArchivedAsync(
                        archived.UserId, InAppNotificationRepository.ToDto(archived), NotificationArchiveReason.Expired);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to broadcast notification archived event for {NotificationId}", notification.Id);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error evaluating notification {NotificationId}", notification.Id);
            }
        }
    }
}
