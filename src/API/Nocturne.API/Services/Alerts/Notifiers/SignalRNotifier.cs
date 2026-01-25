using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Alerts.Notifiers;

public class SignalRNotifier(ISignalRBroadcastService broadcastService) : INotifier
{
    public string Name => "signalr";

    public Task NotifyAsync(
        NotificationBase notification,
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        if (notification.Clear)
        {
            return broadcastService.BroadcastClearAlarmAsync(notification);
        }

        return notification.Level switch
        {
            2 => broadcastService.BroadcastUrgentAlarmAsync(notification),
            1 => broadcastService.BroadcastAlarmAsync(notification),
            _ => broadcastService.BroadcastNotificationAsync(notification),
        };
    }
}
