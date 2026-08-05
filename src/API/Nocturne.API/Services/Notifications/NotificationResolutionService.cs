using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.API.Services.Realtime;

namespace Nocturne.API.Services.Notifications;

/// <summary>
/// Background service that periodically evaluates pending in-app notifications and auto-resolves
/// those whose resolution conditions have been met (e.g. a suggested meal match was accepted
/// elsewhere). Runs every 30 seconds (<see cref="CheckInterval"/>).
/// </summary>
public class NotificationResolutionService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<NotificationResolutionService> _logger;

    /// <summary>
    /// Interval between resolution checks (30 seconds)
    /// </summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Initializes a new instance of the <see cref="NotificationResolutionService"/> class.
    /// </summary>
    /// <param name="serviceProvider">Service provider for creating scoped services</param>
    /// <param name="logger">Logger</param>
    public NotificationResolutionService(
        IServiceProvider serviceProvider,
        ILogger<NotificationResolutionService> logger
    )
    {
        _serviceProvider = serviceProvider;
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
                await EvaluateAllTenantsAsync(stoppingToken);
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
    /// Evaluate pending notifications across all active tenants
    /// </summary>
    private async Task EvaluateAllTenantsAsync(CancellationToken cancellationToken)
    {
        // Lookup active tenants using unfiltered context
        using var lookupScope = _serviceProvider.CreateScope();
        var factory = lookupScope.ServiceProvider.GetRequiredService<IDbContextFactory<NocturneDbContext>>();
        await using var lookupContext = await factory.CreateDbContextAsync(cancellationToken);
        var tenants = await lookupContext.Tenants.AsNoTracking()
            .Where(t => t.IsActive)
            .Select(t => new { t.Id, t.Slug, t.DisplayName })
            .ToListAsync(cancellationToken);

        foreach (var tenant in tenants)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var tenantAccessor = scope.ServiceProvider.GetRequiredService<ITenantAccessor>();
                tenantAccessor.SetTenant(new TenantContext(tenant.Id, tenant.Slug, tenant.DisplayName, true, IsDemo: false));

                await EvaluatePendingNotificationsAsync(scope.ServiceProvider, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during notification resolution for tenant {TenantSlug}", tenant.Slug);
            }
        }
    }

    /// <summary>
    /// Evaluate all pending notifications with resolution conditions for a single tenant
    /// </summary>
    private async Task EvaluatePendingNotificationsAsync(IServiceProvider scopedProvider, CancellationToken cancellationToken)
    {
        var repository = scopedProvider.GetRequiredService<IInAppNotificationRepository>();
        var broadcastService = scopedProvider.GetRequiredService<ISignalRBroadcastService>();

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
