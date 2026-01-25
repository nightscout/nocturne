using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Services.Alerts;

public class NotifierDispatcher : INotifierDispatcher
{
    private readonly NotificationPreferencesRepository _preferencesRepository;
    private readonly IReadOnlyDictionary<string, INotifier> _notifiers;
    private readonly ILogger<NotifierDispatcher> _logger;

    public NotifierDispatcher(
        NotificationPreferencesRepository preferencesRepository,
        IEnumerable<INotifier> notifiers,
        ILogger<NotifierDispatcher> logger
    )
    {
        _preferencesRepository = preferencesRepository;
        _logger = logger;
        _notifiers = notifiers.ToDictionary(
            notifier => notifier.Name,
            notifier => notifier,
            StringComparer.OrdinalIgnoreCase
        );
    }

    public async Task DispatchAsync(
        NotificationBase? notification,
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        if (notification == null)
        {
            return;
        }

        var preferences = await _preferencesRepository.GetPreferencesForUserAsync(
            userId,
            cancellationToken
        );

        var notifierNames = new List<string> { "signalr" };

        if (preferences?.PushoverEnabled == true)
        {
            notifierNames.Add("pushover");
        }

        if (preferences?.WebhookEnabled == true)
        {
            notifierNames.Add("webhook");
        }

        foreach (var notifierName in notifierNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!_notifiers.TryGetValue(notifierName, out var notifier))
            {
                _logger.LogDebug("Notifier {Notifier} not registered", notifierName);
                continue;
            }

            try
            {
                await notifier.NotifyAsync(notification, userId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Notifier {Notifier} failed to dispatch notification for user {UserId}",
                    notifierName,
                    userId
                );
            }
        }
    }
}
