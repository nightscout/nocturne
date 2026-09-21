using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Nocturne.Connectors.Glooko.Xt;

/// <summary>
///     The two REST calls of the Glooko XT sign-in. The password step never returns a token: it
///     checks the password and emails the patient a one-time code, and only the code step yields
///     the JWT the connector then runs on. Both steps are interactive, so the connect controller
///     drives them; the sync never calls this class.
/// </summary>
public class GlookoXtLoginClient(HttpClient httpClient, ILogger<GlookoXtLoginClient> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>What a sign-in step returned: success, or a message fit to show the person who typed the credentials.</summary>
    public sealed record StepResult(bool Success, string? Error, string? Token = null);

    /// <summary>Step one: checks the password and has Glooko XT email a code to the account.</summary>
    public async Task<StepResult> RequestCodeAsync(string email, string password, CancellationToken ct = default)
    {
        using var request = NewRequest(GlookoXtConstants.Endpoints.PatientLogin,
            new GlookoXtPatientLoginRequest { Email = email, Password = password });

        using var response = await httpClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
        {
            logger.LogInformation("Glooko XT refused the password step with {StatusCode}", (int)response.StatusCode);
            return new StepResult(false, "Glooko XT did not accept that email and password.");
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Glooko XT password step failed with {StatusCode}", (int)response.StatusCode);
            return new StepResult(false, $"Glooko XT could not be reached (HTTP {(int)response.StatusCode}). Try again later.");
        }

        var parsed = TryParse<GlookoXtPatientLoginResponse>(body);
        if (parsed?.Error is { Length: > 0 } error)
        {
            logger.LogInformation("Glooko XT password step answered an error: {Error}", error);
            return new StepResult(false, "Glooko XT did not accept that email and password.");
        }

        return new StepResult(true, null);
    }

    /// <summary>Step two: trades the emailed code for the long-lived JWT.</summary>
    public async Task<StepResult> RedeemCodeAsync(string email, string code, CancellationToken ct = default)
    {
        using var request = NewRequest(GlookoXtConstants.Endpoints.CodeAuth,
            new GlookoXtCodeAuthRequest { Email = email, Code = code });

        using var response = await httpClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
        {
            logger.LogInformation("Glooko XT refused the code step with {StatusCode}", (int)response.StatusCode);
            return new StepResult(false, "Glooko XT did not accept that code. Codes expire quickly — request a new one if needed.");
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Glooko XT code step failed with {StatusCode}", (int)response.StatusCode);
            return new StepResult(false, $"Glooko XT could not be reached (HTTP {(int)response.StatusCode}). Try again later.");
        }

        var parsed = TryParse<GlookoXtCodeAuthResponse>(body);
        if (string.IsNullOrWhiteSpace(parsed?.Token))
        {
            logger.LogInformation("Glooko XT code step answered without a token: {Error}", parsed?.Error ?? "(no error field)");
            return new StepResult(false, "Glooko XT did not accept that code. Codes expire quickly — request a new one if needed.");
        }

        return new StepResult(true, null, parsed.Token);
    }

    private static HttpRequestMessage NewRequest<T>(string path, T body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, GlookoXtConstants.ServerUrl + path)
        {
            Content = JsonContent.Create(body, options: Json),
        };
        request.Headers.TryAddWithoutValidation(GlookoXtConstants.AbTokenHeader, string.Empty);
        return request;
    }

    private static T? TryParse<T>(string body) where T : class
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try { return JsonSerializer.Deserialize<T>(body, Json); }
        catch (JsonException) { return null; }
    }
}
