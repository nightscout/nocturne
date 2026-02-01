using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Alerts;

public interface INotifier
{
    string Name { get; }

    Task NotifyAsync(
        NotificationBase notification,
        string userId,
        CancellationToken cancellationToken = default
    );
}
