using System.Net.Http.Json;
using System.Text.Json;

namespace Nocturne.API.Services.Auth;

/// <summary>How a call to an operator webhook ended.</summary>
public enum OperatorWebhookOutcome
{
    /// <summary>The webhook answered with a success status and, when one was asked for, a readable body.</summary>
    Answered,

    /// <summary>The webhook answered with a status that is not a success.</summary>
    Refused,

    /// <summary>The request did not reach the webhook, or the connection broke.</summary>
    Unreachable,

    /// <summary>The webhook did not answer within <see cref="OperatorWebhook.Timeout"/>.</summary>
    TimedOut,

    /// <summary>The webhook answered with a success status and a body that is not the expected JSON.</summary>
    Unreadable,

    /// <summary>The configured URL cannot be called at all.</summary>
    Uncallable,
}

/// <summary>
/// The end of a call to an operator webhook. <see cref="Detail"/> names a failure by its type or
/// transport error only: an exception message can carry the request URL or body.
/// </summary>
public record OperatorWebhookResult(OperatorWebhookOutcome Outcome, int? StatusCode = null, string? Detail = null);

/// <summary>An <see cref="OperatorWebhookResult"/> with the body the webhook answered with.</summary>
public sealed record OperatorWebhookResult<TResponse>(
    OperatorWebhookOutcome Outcome, int? StatusCode = null, string? Detail = null, TResponse? Value = default)
    : OperatorWebhookResult(Outcome, StatusCode, Detail);

/// <summary>
/// Posts JSON to a URL the operator configured. A webhook that is slow, down or wrong is a result
/// the caller decides on; the caller's own cancellation is the one thing that is thrown.
/// </summary>
public static class OperatorWebhook
{
    /// <summary>The named client account links are posted with.</summary>
    public const string AccountLinkClient = "account-link-webhook";

    /// <summary>The named client the slug-validation webhook is called with.</summary>
    public const string SlugValidationClient = "slug-validation";

    /// <summary>The named client the username-validation webhook is called with.</summary>
    public const string UsernameValidationClient = "username-validation";

    /// <summary>How long one call may take before it counts as <see cref="OperatorWebhookOutcome.TimedOut"/>.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Registers a named client for an operator webhook, with <see cref="Timeout"/> and no
    /// automatic redirects: a redirect the operator did not configure must not receive the body.
    /// </summary>
    public static IHttpClientBuilder AddOperatorWebhookClient(this IServiceCollection services, string name) =>
        services.AddHttpClient(name, client => client.Timeout = Timeout)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

    /// <summary>Posts <paramref name="body"/> and reads no answer beyond the status.</summary>
    public static async Task<OperatorWebhookResult> PostAsync(
        HttpClient client, string url, object body, CancellationToken ct) =>
        await SendAsync(client, url, body, (_, _) => Task.FromResult(true), ct);

    /// <summary>Posts <paramref name="body"/> and reads the answer as <typeparamref name="TResponse"/>.</summary>
    public static Task<OperatorWebhookResult<TResponse>> PostAsync<TResponse>(
        HttpClient client, string url, object body, CancellationToken ct) =>
        SendAsync(
            client, url, body,
            async (response, token) => await response.Content.ReadFromJsonAsync<TResponse>(Json, token),
            ct);

    private static async Task<OperatorWebhookResult<TResponse>> SendAsync<TResponse>(
        HttpClient client,
        string url,
        object body,
        Func<HttpResponseMessage, CancellationToken, Task<TResponse?>> read,
        CancellationToken ct)
    {
        try
        {
            using var response = await client.PostAsJsonAsync(url, body, Json, ct);
            if (!response.IsSuccessStatusCode)
                return new(OperatorWebhookOutcome.Refused, (int)response.StatusCode);

            try
            {
                return new(OperatorWebhookOutcome.Answered, (int)response.StatusCode, Value: await read(response, ct));
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
            {
                return new(OperatorWebhookOutcome.Unreadable, (int)response.StatusCode, ex.GetType().Name);
            }
        }
        catch (HttpRequestException ex)
        {
            return new(OperatorWebhookOutcome.Unreachable, Detail: ex.HttpRequestError.ToString());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(OperatorWebhookOutcome.TimedOut);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or UriFormatException)
        {
            return new(OperatorWebhookOutcome.Uncallable, Detail: ex.GetType().Name);
        }
    }
}
