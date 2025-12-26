using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Configurations;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Models;
using Nocturne.Core.Constants;

#nullable enable

namespace Nocturne.Connectors.MiniMed.Services
{
    /// <summary>
    /// Connector service for MiniMed CareLink data source
    /// Based on the original nightscout-connect MiniMed CareLink implementation
    /// </summary>
    public class CareLinkConnectorService : BaseConnectorService<CareLinkConnectorConfiguration>
    {
        private readonly CareLinkConnectorConfiguration _config;
        private readonly IRetryDelayStrategy _retryDelayStrategy;
        private readonly IRateLimitingStrategy _rateLimitingStrategy;
        private readonly IAuthTokenProvider _tokenProvider;
        private int _failedRequestCount = 0;

        private static readonly Dictionary<string, string> KnownServers = new()
        {
            { "us", "carelink.minimed.com" },
            { "eu", "carelink.minimed.eu" },
        };

        private static readonly IReadOnlyDictionary<string, Direction> CarelinkTrendToDirection =
            new Dictionary<string, Direction>()
            {
                { "TRIPLE_UP", Direction.TripleUp },
                { "DOUBLE_UP", Direction.DoubleUp },
                { "SINGLE_UP", Direction.SingleUp },
                { "FORTY_FIVE_UP", Direction.FortyFiveUp },
                { "FLAT", Direction.Flat },
                { "FORTY_FIVE_DOWN", Direction.FortyFiveDown },
                { "SINGLE_DOWN", Direction.SingleDown },
                { "DOUBLE_DOWN", Direction.DoubleDown },
                { "TRIPLE_DOWN", Direction.TripleDown },
            };

        /// <summary>
        /// Gets whether the connector is in a healthy state based on recent request failures
        /// </summary>
        public bool IsHealthy => _failedRequestCount < 5;

        /// <summary>
        /// Gets the number of consecutive failed requests
        /// </summary>
        public int FailedRequestCount => _failedRequestCount;

        /// <summary>
        /// Resets the failed request counter
        /// </summary>
        public void ResetFailedRequestCount()
        {
            _failedRequestCount = 0;
            _logger.LogInformation("CareLink connector failed request count reset");
        }

        public override string ServiceName => "MiniMed CareLink";

        /// <summary>
        /// Gets the source identifier for this connector
        /// </summary>
        public override string ConnectorSource => DataSources.MiniMedConnector;

        public override List<SyncDataType> SupportedDataTypes => new() { SyncDataType.Glucose };

        public CareLinkConnectorService(
            HttpClient httpClient,
            IOptions<CareLinkConnectorConfiguration> config,
            ILogger<CareLinkConnectorService> logger,
            IRetryDelayStrategy retryDelayStrategy,
            IRateLimitingStrategy rateLimitingStrategy,
            IAuthTokenProvider tokenProvider,
            IApiDataSubmitter? apiDataSubmitter = null,
            IConnectorMetricsTracker? metricsTracker = null,
            IConnectorStateService? stateService = null
        )
            : base(httpClient, logger, apiDataSubmitter, metricsTracker, stateService)
        {
            _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
            _retryDelayStrategy =
                retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));
            _rateLimitingStrategy =
                rateLimitingStrategy
                ?? throw new ArgumentNullException(nameof(rateLimitingStrategy));
            _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        }

        public override async Task<bool> AuthenticateAsync()
        {
            var token = await _tokenProvider.GetValidTokenAsync();
            if (token == null)
            {
                _failedRequestCount++;
                return false;
            }

            // Set up authorization header for subsequent requests
            _httpClient.DefaultRequestHeaders.Remove("Authorization");
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

            _failedRequestCount = 0;
            return true;
        }

        private async Task ApplyRetryDelay(int attempt)
        {
            await _retryDelayStrategy.ApplyRetryDelayAsync(attempt);
            _logger.LogDebug("Applied retry delay for attempt {Attempt}", attempt + 1);
        }

        private static bool IsRetryableError(Exception? ex)
        {
            return ex switch
            {
                HttpRequestException httpEx => httpEx.Message.Contains("timeout")
                    || httpEx.Message.Contains("network")
                    || httpEx.Message.Contains("connection"),
                TaskCanceledException => true,
                TimeoutException => true,
                _ => false,
            };
        }

        private async Task<CarelinkLoginFlow> GetLoginFlowAsync()
        {
            try
            {
                var url = $"/patient/sso/login?country={_config.CareLinkCountry}&lang=en";
                var response = await _httpClient.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError(
                        "Failed to get CareLink login flow: {StatusCode}",
                        response.StatusCode
                    );
                    return new CarelinkLoginFlow
                    {
                        Endpoint = "",
                        SessionId = "",
                        SessionData = "",
                    };
                }

                var content = await response.Content.ReadAsStringAsync();

                // Extract form data using regex (as per legacy implementation)
                var endpointMatch = Regex.Match(content, @"<form action=""(.*)"" method=""POST""");
                var sessionIdMatch = Regex.Match(
                    content,
                    @"<input type=""hidden"" name=""sessionID"" value=""(.*)"""
                );
                var sessionDataMatch = Regex.Match(
                    content,
                    @"<input type=""hidden"" name=""sessionData"" value=""(.*)"""
                );

                if (!endpointMatch.Success || !sessionIdMatch.Success || !sessionDataMatch.Success)
                {
                    _logger.LogError("Failed to parse CareLink login flow from response");
                    return new CarelinkLoginFlow
                    {
                        Endpoint = "",
                        SessionId = "",
                        SessionData = "",
                    };
                }

                return new CarelinkLoginFlow
                {
                    Endpoint = endpointMatch.Groups[1].Value,
                    SessionId = sessionIdMatch.Groups[1].Value,
                    SessionData = sessionDataMatch.Groups[1].Value,
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting CareLink login flow");
                return new CarelinkLoginFlow
                {
                    Endpoint = "",
                    SessionId = "",
                    SessionData = "",
                };
            }
        }

        private async Task<CarelinkAuthResult?> SubmitLoginAsync(CarelinkLoginFlow loginFlow)
        {
            try
            {
                var payload = new Dictionary<string, string>
                {
                    { "sessionID", loginFlow.SessionId },
                    { "sessionData", loginFlow.SessionData },
                    { "locale", _config.CareLinkCountry },
                    { "action", "login" },
                    { "username", _config.CareLinkUsername },
                    { "password", _config.CareLinkPassword },
                };

                var formContent = new FormUrlEncodedContent(payload);
                var response = await _httpClient.PostAsync(loginFlow.Endpoint, formContent);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError(
                        "Failed to submit CareLink login: {StatusCode}",
                        response.StatusCode
                    );
                    return null;
                }

                var content = await response.Content.ReadAsStringAsync();

                // Check for authentication failure
                if (content.Contains("error") || content.Contains("invalid"))
                {
                    _logger.LogError("Invalid CareLink credentials");
                    return null;
                }

                // Extract consent form data
                var endpointMatch = Regex.Match(content, @"<form action=""(.*)"" method=""POST""");
                var sessionIdMatch = Regex.Match(
                    content,
                    @"<input type=""hidden"" name=""sessionID"" value=""(.*)"""
                );
                var sessionDataMatch = Regex.Match(
                    content,
                    @"<input type=""hidden"" name=""sessionData"" value=""(.*)"""
                );

                if (!endpointMatch.Success)
                {
                    _logger.LogError("Failed to parse CareLink consent flow from response");
                    return null;
                }

                return new CarelinkAuthResult
                {
                    ConsentEndpoint = endpointMatch.Groups[1].Value,
                    SessionId = sessionIdMatch.Success
                        ? sessionIdMatch.Groups[1].Value
                        : loginFlow.SessionId,
                    SessionData = sessionDataMatch.Success
                        ? sessionDataMatch.Groups[1].Value
                        : loginFlow.SessionData,
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error submitting CareLink login");
                return null;
            }
        }

        private async Task<CarelinkTokenResult?> HandleConsentAsync(CarelinkAuthResult authResult)
        {
            try
            {
                var payload = new Dictionary<string, string>
                {
                    { "sessionID", authResult.SessionId },
                    { "sessionData", authResult.SessionData },
                    { "action", "consent" },
                    { "response_type", "code" },
                    { "response_mode", "query" },
                };

                var formContent = new FormUrlEncodedContent(payload);
                var response = await _httpClient.PostAsync(authResult.ConsentEndpoint, formContent);

                // The response should redirect and contain authorization tokens
                var location = response.Headers.Location?.ToString();
                if (string.IsNullOrEmpty(location))
                {
                    _logger.LogError("No redirect location in CareLink consent response");
                    return null;
                }

                // Extract token from redirect URL or cookies
                // For simplicity, we'll extract from cookies as per legacy implementation
                var cookies = response.Headers.GetValues("Set-Cookie").ToList();
                var authCookie = cookies.FirstOrDefault(c => c.Contains("auth_tmp_token"));

                if (string.IsNullOrEmpty(authCookie))
                {
                    _logger.LogError("No auth token found in CareLink consent response");
                    return null;
                }

                // Extract token value (simplified)
                var tokenMatch = Regex.Match(authCookie, @"auth_tmp_token=([^;]+)");
                var token = tokenMatch.Success
                    ? tokenMatch.Groups[1].Value
                    : Guid.CreateVersion7().ToString();

                return new CarelinkTokenResult { Token = token };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling CareLink consent");
                return null;
            }
        }

        private async Task LoadUserProfileAsync()
        {
            try
            {
                _httpClient.DefaultRequestHeaders.Remove("Authorization");
                _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {(await _tokenProvider.GetValidTokenAsync())}");

                var response = await _httpClient.GetAsync("/patient/users/me/profile");

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "Failed to load CareLink user profile: {StatusCode}",
                        response.StatusCode
                    );
                    return;
                }

                var responseContent = await response.Content.ReadAsStringAsync();
                // User profile loaded through token provider, no local storage needed
                _logger.LogDebug("User profile response received: {Length} chars", responseContent.Length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading CareLink user profile");
            }
        }

        public override async Task<IEnumerable<Entry>> FetchGlucoseDataAsync(DateTime? since = null)
        {
            var attempt = 0;
            const int maxRetries = 3;

            while (attempt < maxRetries)
            {
                try
                { // Apply rate limiting before each attempt
                    await _rateLimitingStrategy.ApplyDelayAsync(attempt);

                    // Check session validity using token provider
                    if (_tokenProvider.IsTokenExpired)
                    {
                        _logger.LogInformation(
                            "Session expired or invalid, attempting to re-authenticate"
                        );
                        if (!await AuthenticateAsync())
                        {
                            _logger.LogError("Failed to re-authenticate with CareLink");
                            return Enumerable.Empty<Entry>();
                        }
                    }

                    _logger.LogDebug(
                        "Fetching glucose data from MiniMed CareLink (attempt {Attempt}/{MaxRetries})",
                        attempt + 1,
                        maxRetries
                    );

                    // Get recent uploads to determine device type
                    var recentUploads = await GetRecentUploadsAsync();
                    if (recentUploads == null)
                    {
                        if (IsRetryableError(null))
                        {
                            await ApplyRetryDelay(attempt);
                            attempt++;
                            continue;
                        }
                        return Enumerable.Empty<Entry>();
                    }

                    // Fetch glucose data based on device family
                    var glucoseData =
                        recentUploads.DeviceFamily == "GUARDIAN"
                            ? await FetchM2MDataAsync()
                            : await FetchBLEDataAsync();

                    if (glucoseData == null || !glucoseData.HasValue)
                    {
                        if (IsRetryableError(null))
                        {
                            await ApplyRetryDelay(attempt);
                            attempt++;
                            continue;
                        }
                        return Enumerable.Empty<Entry>();
                    }

                    var glucoseEntries = ProcessCarelinkData(glucoseData.Value, since);

                    _failedRequestCount = 0;
                    _logger.LogInformation(
                        "[{ConnectorSource}] Successfully fetched {Count} glucose entries from MiniMed CareLink",
                        ConnectorSource,
                        glucoseEntries.Count()
                    );
                    return glucoseEntries;
                }
                catch (HttpRequestException ex) when (IsRetryableError(ex))
                {
                    _logger.LogWarning(
                        ex,
                        "Retryable error fetching CareLink data (attempt {Attempt}/{MaxRetries}): {Message}",
                        attempt + 1,
                        maxRetries,
                        ex.Message
                    );

                    await ApplyRetryDelay(attempt);
                    attempt++;
                }
                catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
                {
                    _logger.LogWarning(
                        ex,
                        "Timeout fetching CareLink data (attempt {Attempt}/{MaxRetries})",
                        attempt + 1,
                        maxRetries
                    );

                    await ApplyRetryDelay(attempt);
                    attempt++;
                }
                catch (JsonException ex)
                {
                    _logger.LogError(
                        ex,
                        "JSON parsing error in CareLink response: {Message}",
                        ex.Message
                    );
                    _failedRequestCount++;
                    return Enumerable.Empty<Entry>();
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Non-retryable error fetching glucose data from MiniMed CareLink: {Message}",
                        ex.Message
                    );
                    _failedRequestCount++;
                    return Enumerable.Empty<Entry>();
                }
            }

            _logger.LogError(
                "Failed to fetch glucose data from MiniMed CareLink after {MaxRetries} attempts",
                maxRetries
            );
            _failedRequestCount++;
            return Enumerable.Empty<Entry>();
        }

        private async Task<CarelinkRecentUploads?> GetRecentUploadsAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync(
                    "/patient/connect/data/recentuploads?numUploads=1"
                );

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError(
                        "Failed to get CareLink recent uploads: {StatusCode}",
                        response.StatusCode
                    );
                    return null;
                }

                var content = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<CarelinkRecentUploads>(content);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting CareLink recent uploads");
                return null;
            }
        }

        private async Task<JsonElement?> FetchM2MDataAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync("/patient/connect/data/m2m");

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError(
                        "Failed to fetch CareLink M2M data: {StatusCode}",
                        response.StatusCode
                    );
                    return null;
                }

                var content = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<JsonElement>(content);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching CareLink M2M data");
                return null;
            }
        }

        private async Task<JsonElement?> FetchBLEDataAsync()
        {
            try
            {
                var payload = new
                {
                    username = _config.CareLinkUsername,
                    role = string.IsNullOrEmpty(_config.CareLinkPatientUsername)
                        ? "patient"
                        : "carepartner",
                    patientId = _config.CareLinkPatientUsername,
                };

                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync("/patient/connect/data/ble", content);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError(
                        "Failed to fetch CareLink BLE data: {StatusCode}",
                        response.StatusCode
                    );
                    return null;
                }

                var responseContent = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<JsonElement>(responseContent);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching CareLink BLE data");
                return null;
            }
        }

        private IEnumerable<Entry> ProcessCarelinkData(JsonElement data, DateTime? since)
        {
            var entries = new List<Entry>();

            try
            {
                if (!data.TryGetProperty("sgs", out var sgsElement))
                {
                    return entries;
                }

                var recentMillis = since?.Ticks ?? 0;

                foreach (var sg in sgsElement.EnumerateArray())
                {
                    if (
                        !sg.TryGetProperty("datetime", out var datetimeElement)
                        || !sg.TryGetProperty("sg", out var sgElement)
                    )
                    {
                        continue;
                    }

                    if (
                        !DateTime.TryParse(datetimeElement.GetString(), out var datetime)
                        || !sgElement.TryGetInt32(out var glucoseValue)
                    )
                    {
                        continue;
                    }

                    if (datetime.Ticks <= recentMillis)
                    {
                        continue;
                    }
                    var entry = new Entry
                    {
                        Date = datetime,
                        Sgv = glucoseValue,
                        Direction = Direction.Flat.ToString(), // Default, will be updated with trend data
                        Device =
                            $"nightscout-connect-carelink/{data.GetProperty("medicalDeviceFamily").GetString()}",
                        Type = "sgv",
                    };

                    entries.Add(entry);
                }

                // Apply trend to the last entry if available
                if (entries.Count > 0 && data.TryGetProperty("lastSGTrend", out var trendElement))
                {
                    var trend = trendElement.GetString();
                    var lastEntry = entries.Last();
                    lastEntry.Direction = CarelinkTrendToDirection
                        .GetValueOrDefault(trend ?? string.Empty, Direction.Flat)
                        .ToString();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing CareLink glucose data");
            }

            return entries.OrderBy(e => e.Date);
        }



        private class CarelinkLoginFlow
        {
            public required string Endpoint { get; set; }
            public required string SessionId { get; set; }
            public required string SessionData { get; set; }
        }

        private class CarelinkAuthResult
        {
            public required string ConsentEndpoint { get; set; }
            public required string SessionId { get; set; }
            public required string SessionData { get; set; }
        }

        private class CarelinkTokenResult
        {
            public required string Token { get; set; }
        }

        private class CarelinkUserProfile
        {
            public required string Username { get; set; }
        }

        private class CarelinkRecentUploads
        {
            public required string DeviceFamily { get; set; }
        }
    }
}

