using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Services;

/// <summary>
/// Background service that evaluates pending notifications and auto-resolves them based on their conditions.
/// </summary>
public class NotificationResolutionService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NotificationResolutionService> _logger;

    /// <summary>
    /// Interval between resolution checks (30 seconds)
    /// </summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Initializes a new instance of the <see cref="NotificationResolutionService"/> class.
    /// </summary>
    /// <param name="scopeFactory">Service scope factory for creating scoped services</param>
    /// <param name="logger">Logger</param>
    public NotificationResolutionService(
        IServiceScopeFactory scopeFactory,
        ILogger<NotificationResolutionService> logger
    )
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Execute the background service
    /// </summary>
    /// <param name="stoppingToken">Stopping token</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Notification Resolution Service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EvaluatePendingNotificationsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during notification resolution evaluation");
            }

            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Notification Resolution Service stopped");
    }

    /// <summary>
    /// Evaluate all pending notifications with resolution conditions
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    private async Task EvaluatePendingNotificationsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<InAppNotificationRepository>();
        var broadcastService = scope.ServiceProvider.GetRequiredService<ISignalRBroadcastService>();

        // Get all notifications with pending resolution conditions
        var notifications = await repository.GetPendingResolutionAsync(cancellationToken);

        if (notifications.Count == 0)
        {
            return;
        }

        _logger.LogDebug("Evaluating {Count} notifications with resolution conditions", notifications.Count);

        var now = DateTime.UtcNow;

        foreach (var notification in notifications)
        {
            try
            {
                var conditions = InAppNotificationRepository.DeserializeConditions(notification.ResolutionConditionsJson);

                if (conditions == null)
                {
                    continue;
                }

                // Check time-based expiry
                if (conditions.ExpiresAt.HasValue && conditions.ExpiresAt.Value <= now)
                {
                    _logger.LogInformation(
                        "Auto-resolving notification {NotificationId} due to expiry at {ExpiresAt}",
                        notification.Id,
                        conditions.ExpiresAt
                    );

                    var archived = await repository.ArchiveAsync(
                        notification.Id,
                        NotificationArchiveReason.Expired,
                        cancellationToken
                    );

                    if (archived != null)
                    {
                        // Broadcast the archived notification
                        var dto = InAppNotificationRepository.ToDto(archived);
                        try
                        {
                            await broadcastService.BroadcastNotificationArchivedAsync(
                                archived.UserId,
                                dto,
                                NotificationArchiveReason.Expired
                            );
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(
                                ex,
                                "Failed to broadcast notification archived event for {NotificationId}",
                                notification.Id
                            );
                        }
                    }
                }

                // Future: Add additional resolution condition checks here
                // - SourceDeletedType: Check if source entity has been deleted
                // - GlucoseCondition: Check if glucose has returned to target range
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error evaluating notification {NotificationId}",
                    notification.Id
                );
            }
        }
    }

    /// <summary>
    /// Stop the background service
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Notification Resolution Service is stopping");
        await base.StopAsync(cancellationToken);
    }
}
