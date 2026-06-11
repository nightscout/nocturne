using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Tandem.Configurations;
using Nocturne.Connectors.Tandem.Models;

namespace Nocturne.Connectors.Tandem.Services;

/// <summary>
/// Thin client over the Tandem Source reports API. Endpoints and the base64 pump-events payload
/// shape are ported from <c>tconnectsync</c>'s <c>api/tandemsource.py</c>. Non-success responses
/// throw <see cref="HttpRequestException"/> (carrying the status code) so the caller's retry/
/// re-authentication loop can react to 401/5xx.
/// </summary>
public sealed class TandemSourceApiClient(HttpClient httpClient, ILogger logger)
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>Returns metadata for every pump on the account.</summary>
    public async Task<List<TandemPumpEventMetadata>> GetPumpEventMetadataAsync(
        TandemConstants.RegionUrls region, string accessToken, string pumperId,
        CancellationToken cancellationToken)
    {
        var json = await GetStringAsync(
            region, accessToken,
            $"api/reports/reportsfacade/{pumperId}/pumpeventmetadata",
            cancellationToken);

        return JsonSerializer.Deserialize<List<TandemPumpEventMetadata>>(json)
               ?? [];
    }

    /// <summary>
    /// Returns the raw base64 pump-events payload for a device over a date range. When
    /// <paramref name="eventIdsFilter"/> is null the full history log is returned, otherwise the
    /// request is filtered to those event ids (matching the Tandem Source backend default).
    /// </summary>
    public async Task<string?> GetPumpEventsRawAsync(
        TandemConstants.RegionUrls region, string accessToken, string pumperId,
        string tconnectDeviceId, DateTime minDate, DateTime maxDate, int[]? eventIdsFilter,
        CancellationToken cancellationToken)
    {
        var min = minDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var max = maxDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var endpoint =
            $"api/reports/reportsfacade/pumpevents/{pumperId}/{tconnectDeviceId}?minDate={min}&maxDate={max}";
        if (eventIdsFilter is { Length: > 0 })
            endpoint += "&eventIds=" + string.Join("%2C", eventIdsFilter);

        var json = await GetStringAsync(region, accessToken, endpoint, cancellationToken);

        // The endpoint returns the base64 events as a JSON string literal.
        return JsonSerializer.Deserialize<string>(json);
    }

    private async Task<string> GetStringAsync(
        TandemConstants.RegionUrls region, string accessToken, string endpoint,
        CancellationToken cancellationToken)
    {
        var url = region.SourceUrl + endpoint;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Origin", "https://tconnect.tandemdiabetes.com");
        request.Headers.TryAddWithoutValidation("Referer", "https://tconnect.tandemdiabetes.com/");
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
