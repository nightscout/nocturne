using System.Net.Http.Json;
using System.Text.Json;
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
    /// <summary>The named client the webhook is called with.</summary>
    public const string HttpClientName = "account-link-webhook";

    /// <summary>How long one delivery may take before it counts as failed.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(config.Value.AccountLinkWebhookUrl) && baseDomain.Value.PublicOrigin is not null;

    /// <inheritdoc />
    public async Task<bool> DeliverAsync(AccountLinkMessage message, CancellationToken ct)
    {
        if (!IsConfigured)
            return false;

        try
        {
            using var response = await httpClientFactory.CreateClient(HttpClientName)
                .PostAsJsonAsync(config.Value.AccountLinkWebhookUrl, message, Json, ct);
            if (response.IsSuccessStatusCode)
                return true;

            logger.LogWarning(
                "The account link webhook refused a {Kind} message with status {StatusCode}",
                message.Kind, (int)response.StatusCode);
            return false;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("The account link webhook could not be reached for a {Kind} message: {Error}", message.Kind, ex.HttpRequestError);
            return false;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("The account link webhook timed out on a {Kind} message", message.Kind);
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or UriFormatException)
        {
            logger.LogWarning("The account link webhook URL cannot be called for a {Kind} message: {Error}", message.Kind, ex.GetType().Name);
            return false;
        }
    }
}
