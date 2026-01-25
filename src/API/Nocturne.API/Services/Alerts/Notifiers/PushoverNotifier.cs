using Nocturne.Core.Contracts;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Alerts.Notifiers;

public class PushoverNotifier(
    IPushoverService pushoverService,
    ILogger<PushoverNotifier> logger)
    : INotifier
{
    public string Name => "pushover";

    public async Task NotifyAsync(
        NotificationBase notification,
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        if (notification.Clear)
        {
            return;
        }

        var request = pushoverService.CreateAlarmNotification(
            notification.Level,
            notification.Group,
            notification.Title,
            notification.Message
        );

        var response = await pushoverService.SendNotificationAsync(request, cancellationToken);

        if (!response.Success)
        {
            logger.LogWarning(
                "Pushover notification failed for user {UserId}: {Error}",
                userId,
                response.Error
            );
        }
    }
}
