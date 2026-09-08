using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nocturne.Connectors.GoogleHealth.Services;

public sealed class GoogleHealthOAuthClient(HttpClient http)
{
    public Task<JsonElement> ExchangeAuthorizationCodeAsync(
        Dictionary<string, string> form,
        CancellationToken cancellationToken) =>
        ExchangeAsync(form, "expired_signin", "authorization_code", cancellationToken);

    public Task<JsonElement> RefreshAccessTokenAsync(
        Dictionary<string, string> form,
        CancellationToken cancellationToken) =>
        ExchangeAsync(form, "reconnect_required", "token_refresh", cancellationToken);

    private async Task<JsonElement> ExchangeAsync(
        Dictionary<string, string> form,
        string invalidGrantCode,
        string stage,
        CancellationToken cancellationToken)
    {
        using var response = await http.PostAsync(
            "https://oauth2.googleapis.com/token",
            new FormUrlEncodedContent(form),
            cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw await OAuthErrorAsync(response, invalidGrantCode, stage, cancellationToken);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return json.RootElement.Clone();
    }

    public async Task<bool> RevokeAsync(string refreshToken, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsync(
            "https://oauth2.googleapis.com/revoke",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = refreshToken }),
            cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<string> AccountKeyAsync(string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://openidconnect.googleapis.com/v1/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new GoogleHealthException(
                "reconnect_required",
                stage: "account_identity",
                providerStatus: (int)response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!json.RootElement.TryGetProperty("sub", out var subjectValue) ||
            subjectValue.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(subjectValue.GetString()))
            throw new GoogleHealthException("invalid_token_response", stage: "account_identity");

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(subjectValue.GetString()!)));
    }

    private static async Task<GoogleHealthException> OAuthErrorAsync(
        HttpResponseMessage response,
        string invalidGrantCode,
        string stage,
        CancellationToken cancellationToken)
    {
        var code = response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
            ? "rate_limited"
            : "google_unavailable";
        string? providerReason = null;
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            string? rawReason = null;
            if (json.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.String)
                rawReason = error.GetString();

            providerReason = GoogleHealthHttpError.SafeProviderReason(rawReason);
            code = rawReason switch
            {
                "invalid_grant" => invalidGrantCode,
                "invalid_client" => "invalid_client_credentials",
                "redirect_uri_mismatch" => "invalid_callback",
                "invalid_scope" => "oauth_scope_configuration",
                "access_denied" => "permission_denied",
                "invalid_request" => "oauth_request_invalid",
                "temporarily_unavailable" or "server_error" => "google_unavailable",
                _ => code
            };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
        }

        return new GoogleHealthException(
            code,
            GoogleHealthHttpError.RetryAfter(response),
            stage,
            providerReason: providerReason,
            providerStatus: (int)response.StatusCode);
    }
}
