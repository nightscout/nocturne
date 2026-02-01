using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Alerts;

public interface INotifierDispatcher
{
    Task DispatchAsync(
        NotificationBase notification,
        string userId,
        CancellationToken cancellationToken = default
    );
}
