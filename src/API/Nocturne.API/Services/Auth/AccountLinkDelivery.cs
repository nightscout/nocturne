using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Nocturne.API.Configuration;
using Nocturne.API.Multitenancy;

namespace Nocturne.API.Services.Auth;

/// <summary>What an account link lets its holder do.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AccountLinkKind>))]
public enum AccountLinkKind
{
    /// <summary>Start a recovery session, which enrols one way to sign in.</summary>
    [JsonStringEnumMemberName("recovery")]
    Recovery,

    /// <summary>Confirm the address the link was sent to.</summary>
    [JsonStringEnumMemberName("email_confirmation")]
    EmailConfirmation,
}

/// <summary>
/// One account link on its way to an inbox. It holds what the message needs and nothing that
/// identifies the account inside Nocturne; the only secret is the code inside <see cref="Link"/>.
/// </summary>
public sealed record AccountLinkMessage(
    AccountLinkKind Kind,
    string Email,
    string Link,
    DateTime ExpiresAt,
    string SiteSlug,
    string SiteDisplayName,
    string AccountDisplayName);

/// <summary>
/// Delivers account links to an address. Nocturne mints and verifies every link; a delivery only
/// carries it. While <see cref="IAccountLinkRoute.IsConfigured"/> is false the flows that send links do not exist.
/// </summary>
public interface IAccountLinkDelivery : IAccountLinkRoute
{
    /// <summary>
    /// Delivers <paramref name="message"/> and returns whether the route accepted it. A failure is
    /// reported, never thrown, and logged without the address or the link.
    /// </summary>
    Task<bool> DeliverAsync(AccountLinkMessage message, CancellationToken ct);
}

/// <summary>
/// Delivers account links by POSTing each message to <see cref="OperatorConfiguration.AccountLinkWebhookUrl"/>,
/// in the shape that property documents.
/// </summary>
public sealed class OperatorWebhookLinkDelivery(
    IHttpClientFactory httpClientFactory,
    IOptions<OperatorConfiguration> config,
    IOptions<BaseDomainOptions> baseDomain,
    ILogger<OperatorWebhookLinkDelivery> logger) : IAccountLinkDelivery
{
    /// <inheritdoc />
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(config.Value.AccountLinkWebhookUrl) && baseDomain.Value.PublicOrigin is not null;

    /// <inheritdoc />
    public async Task<bool> DeliverAsync(AccountLinkMessage message, CancellationToken ct)
    {
        if (!IsConfigured)
            return false;

        var result = await OperatorWebhook.PostAsync(
            httpClientFactory.CreateClient(OperatorWebhook.AccountLinkClient),
            config.Value.AccountLinkWebhookUrl!, message, ct);

        switch (result.Outcome)
        {
            case OperatorWebhookOutcome.Answered:
                return true;
            case OperatorWebhookOutcome.Refused:
                logger.LogWarning(
                    "The account link webhook refused a {Kind} message with status {StatusCode}",
                    message.Kind, result.StatusCode);
                break;
            case OperatorWebhookOutcome.Unreachable:
                logger.LogWarning("The account link webhook could not be reached for a {Kind} message: {Error}", message.Kind, result.Detail);
                break;
            case OperatorWebhookOutcome.TimedOut:
                logger.LogWarning("The account link webhook timed out on a {Kind} message", message.Kind);
                break;
            default:
                logger.LogWarning("The account link webhook URL cannot be called for a {Kind} message: {Error}", message.Kind, result.Detail);
                break;
        }

        return false;
    }
}
