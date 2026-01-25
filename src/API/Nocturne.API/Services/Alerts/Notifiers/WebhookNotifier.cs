using System.Text.Json;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.API.Services.Alerts.Webhooks;

namespace Nocturne.API.Services.Alerts.Notifiers;

public class WebhookNotifier(
    NotificationPreferencesRepository preferencesRepository,
    WebhookRequestSender requestSender,
    ILogger<WebhookNotifier> logger)
    : INotifier
{
    public string Name => "webhook";

    public async Task NotifyAsync(
        NotificationBase notification,
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        var preferences = await preferencesRepository.GetPreferencesForUserAsync(
            userId,
            cancellationToken
        );

        if (preferences?.WebhookEnabled != true || string.IsNullOrWhiteSpace(preferences.WebhookUrls))
        {
            return;
        }

        var config = WebhookConfigurationParser.Parse(preferences.WebhookUrls, logger);
        if (config.Urls.Count == 0)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(
            new
            {
                userId,
                notification,
            }
        );

        await requestSender.SendAsync(config.Urls, payload, config.Secret, cancellationToken);
    }
}
