using System.Net.Http.Json;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Providers;

/// <summary>
/// Delivers alert payloads to chat-platform users by forwarding a dispatch request
/// to the Nocturne bot service over HTTP.
/// </summary>
/// <remarks>
/// The bot endpoint is derived from <c>WEB_URL</c> when set (the internal web
/// endpoint wired by the AppHost), otherwise from the deployment's public base
/// URL (<see cref="ServiceNames.ConfigKeys.BaseUrl"/>) — which reaches the same
/// SvelteKit dispatch route through the gateway. Delivery is skipped with a
/// warning when neither is configured.
/// </remarks>
internal sealed class ChatBotProvider(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<ChatBotProvider> logger)
{
    /// <summary>
    /// The set of <see cref="ChannelType"/> values that this provider can deliver to.
    /// </summary>
    public static readonly HashSet<ChannelType> SupportedChannelTypes =
    [
        ChannelType.DiscordDm,
        ChannelType.DiscordChannel,
        ChannelType.SlackDm,
        ChannelType.SlackChannel,
        ChannelType.TelegramDm,
        ChannelType.TelegramGroup,
        ChannelType.WhatsAppDm,
        ChannelType.ResendEmail,
    ];

    /// <summary>
    /// Sends an alert payload to the specified chat destination.
    /// </summary>
    /// <param name="deliveryId">The unique delivery identifier for idempotency tracking.</param>
    /// <param name="channelType">The target channel type (e.g. Discord DM, Telegram group).</param>
    /// <param name="destination">Platform-specific destination identifier (user/channel ID).</param>
    /// <param name="payload">The <see cref="AlertPayload"/> to deliver.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task SendAsync(Guid deliveryId, ChannelType channelType, string destination, AlertPayload payload, CancellationToken ct)
    {
        var webUrl = configuration["WEB_URL"];
        if (string.IsNullOrEmpty(webUrl))
        {
            webUrl = configuration[ServiceNames.ConfigKeys.BaseUrl];
        }

        if (string.IsNullOrEmpty(webUrl))
        {
            logger.LogWarning("Neither WEB_URL nor BaseUrl configured, cannot dispatch to chat bot");
            return;
        }

        try
        {
            var client = httpClientFactory.CreateClient("ChatBot");
            var dispatchUrl = $"{webUrl.TrimEnd('/')}/api/v4/bot/dispatch";

            var response = await client.PostAsJsonAsync(dispatchUrl, new
            {
                DeliveryId = deliveryId,
                ChannelType = channelType,
                Destination = destination,
                Payload = payload,
            }, ct);

            response.EnsureSuccessStatusCode();

            logger.LogDebug(
                "Chat bot alert dispatched for delivery {DeliveryId} via {ChannelType}",
                deliveryId, channelType);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to dispatch chat bot alert for delivery {DeliveryId} via {ChannelType}",
                deliveryId, channelType);
            throw;
        }
    }
}
