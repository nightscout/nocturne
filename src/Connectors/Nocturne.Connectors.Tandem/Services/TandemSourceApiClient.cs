using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Tandem.Configurations;
using Nocturne.Connectors.Tandem.Models;

namespace Nocturne.Connectors.Tandem.Services;

/// <summary>
/// Thin client over the Tandem Source "BFF" reports API. Endpoints and payload shapes are ported
/// from <c>tconnectsync</c> v3's <c>api/tandemsource.py</c> (the pre-BFF <c>reportsfacade</c>
/// endpoints were retired by Tandem in June 2026). Non-success responses throw
/// <see cref="HttpRequestException"/> (carrying the status code) so the caller's retry/
/// re-authentication loop can react to 401/5xx.
/// </summary>
public sealed class TandemSourceApiClient(HttpClient httpClient, ILogger logger)
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>Returns the pumper profile plus every pump on the account.</summary>
    public async Task<TandemBffPumper?> GetPumperAsync(
        TandemConstants.RegionUrls region, string accessToken, string pumperId,
        CancellationToken cancellationToken)
    {
        var json = await GetStringAsync(
            region, accessToken,
            $"api/reports/bff/pumper/{pumperId}",
            cancellationToken);

        return JsonSerializer.Deserialize<TandemBffPumper>(json);
    }

    /// <summary>
    /// Returns the server-decoded pump events for a device over an inclusive date window. The
    /// server caps each window at roughly four weeks (<see cref="TandemConstants.PumpLogsWindowDays"/>);
    /// callers page longer ranges. When <paramref name="eventIdsFilter"/> is null the full history
    /// log is requested; the server currently ignores the filter and returns every event in the
    /// window regardless, but it is sent to mirror the Tandem Source web app.
    /// </summary>
    public async Task<TandemPumpLogsResponse?> GetPumpLogsAsync(
        TandemConstants.RegionUrls region, string accessToken, string pumperId,
        string deviceAssignmentId, DateTime minDate, DateTime maxDate, int[]? eventIdsFilter,
        CancellationToken cancellationToken)
    {
        var min = minDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var max = maxDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var query = new Dictionary<string, string>
        {
            ["pumperId"] = pumperId,
            ["startDate"] = $"{min}T00:00:00Z",
            ["endDate"] = $"{max}T23:59:59Z",
            ["eventIds"] = eventIdsFilter is { Length: > 0 } ? string.Join(",", eventIdsFilter) : string.Empty,
        };
        var endpoint = $"api/reports/bff/pump-logs/{deviceAssignmentId}?" +
            string.Join("&", query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));

        var json = await GetStringAsync(region, accessToken, endpoint, cancellationToken);

        return JsonSerializer.Deserialize<TandemPumpLogsResponse>(json);
    }

    private async Task<string> GetStringAsync(
        TandemConstants.RegionUrls region, string accessToken, string endpoint,
        CancellationToken cancellationToken)
    {
        var url = region.SourceUrl + endpoint;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        // The WAF enforces same-origin: Origin/Referer must match the Source host, else HTTP 403.
        request.Headers.TryAddWithoutValidation("Origin", region.SourceUrl.TrimEnd('/'));
        request.Headers.TryAddWithoutValidation("Referer", region.SourceUrl);
        request.Headers.UserAgent.ParseAdd(TandemConstants.UserAgent);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Tandem Source API {Endpoint} returned HTTP {StatusCode}", endpoint, (int)response.StatusCode);
            throw new HttpRequestException(
                $"Tandem Source API HTTP {(int)response.StatusCode}", null, response.StatusCode);
        }

        return body;
    }
}
