using System.Net.Http.Json;
using System.Text.Json;
using Nocturne.API.Authorization;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Providers;

/// <summary>
/// Delivers alert payloads to chat-platform users by forwarding a dispatch request
/// to the Nocturne bot service over HTTP.
/// </summary>
/// <remarks>
/// <para>
/// The bot endpoint is derived from <c>WEB_URL</c>, which names the web app's
/// deployment-internal address (the AppHost wires it from the web resource's
/// endpoint; the Compose bundles set <c>http://nocturne-web:&lt;port&gt;</c>).
/// The deployment's public base URL (<see cref="ServiceNames.ConfigKeys.BaseUrl"/>)
/// is deliberately not used as a fallback. It is a hairpin: an intra-cluster call
/// between two containers on the same network would leave the deployment, traverse
/// the CDN and the edge proxy, and come back in — carrying the instance-key service
/// credential across the public internet on every alert. It also makes delivery
/// depend on the edge forwarding those headers unchanged, which is not a property
/// the edge guarantees; header sanitisation there would silently 401 every dispatch.
/// </para>
/// <para>
/// The dispatch route is reachable from the internet through the gateway, so the
/// request carries the instance-key service credential
/// (<see cref="ServiceNames.Headers.InstanceKey"/> +
/// <see cref="ServiceNames.Headers.InstanceService"/>) and names the target tenant
/// in the body.
/// </para>
/// <para>
/// A missing address, instance key, or tenant slug throws rather than returning.
/// <see cref="AlertDeliveryService"/> creates the <c>alert_deliveries</c> row before
/// calling this provider and marks neither delivered nor failed for chat-bot
/// channels, so a silent return leaves that row <c>pending</c> forever with no error
/// text and no retry accounting. Throwing lands the reason in the row's error field
/// via the caller's <c>MarkFailedAsync</c>, matching how an HTTP failure is recorded.
/// </para>
/// </remarks>
internal sealed class ChatBotProvider(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ITenantAccessor tenantAccessor,
    ILogger<ChatBotProvider> logger)
{
    /// <summary>
    /// Matches the camelCase body the SvelteKit dispatch route parses. <c>PostAsJsonAsync</c>
    /// applies these defaults implicitly; they are explicit here because the request is
    /// built by hand to carry the service-auth headers.
    /// </summary>
    private static readonly JsonSerializerOptions DispatchJsonOptions = new(JsonSerializerDefaults.Web);

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
    /// <exception cref="InvalidOperationException">
    /// Thrown when <c>WEB_URL</c>, the instance key, or the tenant slug is unavailable. See the
    /// remarks on this type for why these are thrown rather than logged and skipped.
    /// </exception>
    public async Task SendAsync(Guid deliveryId, ChannelType channelType, string destination, AlertPayload payload, CancellationToken ct)
    {
        // Internal address only — see the remarks on this type for why the public
        // base URL is not an acceptable substitute.
        var webUrl = configuration["WEB_URL"];
        if (string.IsNullOrEmpty(webUrl))
        {
            throw new InvalidOperationException(
                "WEB_URL is not configured with the web app's internal address, cannot dispatch to chat bot");
        }

        var instanceKeyDigest = InstanceKeyDigest.Resolve(configuration);
        if (string.IsNullOrEmpty(instanceKeyDigest))
        {
            throw new InvalidOperationException(
                "No instance key configured, cannot authenticate the chat bot dispatch");
        }

        // The dispatch route resolves the tenant from this slug instead of a forwarded
        // host header, so a missing slug means the request cannot be scoped.
        var tenantSlug = tenantAccessor.Context?.Slug;
        if (string.IsNullOrEmpty(tenantSlug))
        {
            throw new InvalidOperationException(
                "No tenant resolved, cannot dispatch chat bot alert");
        }

        try
        {
            var client = httpClientFactory.CreateClient("ChatBot");
            var dispatchUrl = $"{webUrl.TrimEnd('/')}/api/v4/bot/dispatch";

            using var request = new HttpRequestMessage(HttpMethod.Post, dispatchUrl)
            {
                Content = JsonContent.Create(new
                {
                    DeliveryId = deliveryId,
                    ChannelType = channelType,
                    Destination = destination,
                    Payload = payload,
                    TenantSlug = tenantSlug,
                }, options: DispatchJsonOptions),
            };

            request.Headers.Add(ServiceNames.Headers.InstanceKey, instanceKeyDigest);
            request.Headers.Add(ServiceNames.Headers.InstanceService, ServiceNames.NocturneApi);

            var response = await client.SendAsync(request, ct);

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
