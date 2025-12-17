using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Configurations;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Core.Models;
using Nocturne.Core.Constants;

#nullable enable

namespace Nocturne.Connectors.Glooko.Services
{
    /// <summary>
    /// Connector service for Glooko data source
    /// Based on the original nightscout-connect Glooko implementation
    /// </summary>
    public class GlookoConnectorService : BaseConnectorService<GlookoConnectorConfiguration>
    {
        private readonly GlookoConnectorConfiguration _config;
        private readonly IRateLimitingStrategy _rateLimitingStrategy;
        private readonly IRetryDelayStrategy _retryDelayStrategy;
        private readonly IConnectorFileService<GlookoBatchData>? _fileService;
        private string? _sessionCookie;
        private GlookoUserData? _userData;
        private int _failedRequestCount = 0;

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
            _logger.LogInformation("Glooko connector failed request count reset");
        }

        public override string ServiceName => "Glooko";
        public override string ConnectorSource => DataSources.GlookoConnector;

        public GlookoConnectorService(
            HttpClient httpClient,
            IOptions<GlookoConnectorConfiguration> config,
            ILogger<GlookoConnectorService> logger,
            IRetryDelayStrategy retryDelayStrategy,
            IRateLimitingStrategy rateLimitingStrategy,
            IConnectorFileService<GlookoBatchData>? fileService = null,
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
            _fileService = fileService;
        }

        public override async Task<bool> AuthenticateAsync()
        {
            try
            {
                _logger.LogInformation(
                    $"Authenticating with Glooko server: {_config.GlookoServer}"
                );

                // Setup headers to mimic browser behavior (based on legacy implementation)
                var loginHeaders = new Dictionary<string, string>
                {
                    { "Accept", "application/json, text/plain, */*" },
                    { "Accept-Encoding", "gzip, deflate, br" },
                    {
                        "User-Agent",
                        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.5 Safari/605.1.15"
                    },
                    { "Referer", "https://eu.my.glooko.com/" },
                    { "Origin", "https://eu.my.glooko.com" },
                    { "Connection", "keep-alive" },
                    { "Accept-Language", "en-GB,en;q=0.9" },
                };

                var loginData = new
                {
                    userLogin = new
                    {
                        email = _config.GlookoUsername,
                        password = _config.GlookoPassword,
                    },
                    deviceInformation = new
                    {
                        applicationType = "logbook",
                        os = "android",
                        osVersion = "33",
                        device = "Google Pixel 8 Pro",
                        deviceManufacturer = "Google",
                        deviceModel = "Pixel 8 Pro",
                        serialNumber = "HIDDEN",
                        clinicalResearch = false,
                        deviceId = "HIDDEN",
                        applicationVersion = "6.1.3",
                        buildNumber = "0",
                        gitHash = "g4fbed2011b",
                    },
                };

                var json = JsonSerializer.Serialize(loginData);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var request = new HttpRequestMessage(HttpMethod.Post, "/api/v2/users/sign_in")
                {
                    Content = content,
                };

                // Add headers
                foreach (var header in loginHeaders)
                {
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    // Read response as bytes first to handle compression properly
                    var responseBytes = await response.Content.ReadAsByteArrayAsync();

                    // Decompress if needed (check for gzip magic number 0x1F 0x8B)
                    string responseJson;
                    if (
                        responseBytes.Length >= 2
                        && responseBytes[0] == 0x1F
                        && responseBytes[1] == 0x8B
                    )
                    {
                        using var compressedStream = new MemoryStream(responseBytes);
                        using var gzipStream = new System.IO.Compression.GZipStream(
                            compressedStream,
                            System.IO.Compression.CompressionMode.Decompress
                        );
                        using var decompressedStream = new MemoryStream();
                        await gzipStream.CopyToAsync(decompressedStream);
                        responseJson = Encoding.UTF8.GetString(decompressedStream.ToArray());
                    }
                    else
                    {
                        responseJson = Encoding.UTF8.GetString(responseBytes);
                    }

                    _logger.LogDebug("GLOOKO AUTH response received and decompressed");

                    // Extract session cookie from response headers
                    if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
                    {
                        foreach (var cookie in cookies)
                        {
                            if (cookie.StartsWith("_logbook-web_session="))
                            {
                                _sessionCookie = cookie.Split(';')[0]; // Get just the session part
                                _logger.LogInformation("Session cookie extracted successfully");
                                break;
                            }
                        }
                    }

                    // Parse user data
                    try
                    {
                        _userData = JsonSerializer.Deserialize<GlookoUserData>(responseJson);
                        if (_userData?.UserLogin?.GlookoCode != null)
                        {
                            _logger.LogInformation(
                                "User data parsed successfully. Glooko code: {GlookoCode}",
                                _userData.UserLogin.GlookoCode
                            );
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"Could not parse user data: {ex.Message}");
                        _logger.LogDebug(
                            "Response JSON: {ResponseJson}",
                            responseJson.Substring(0, Math.Min(500, responseJson.Length))
                        );
                    }

                    if (!string.IsNullOrEmpty(_sessionCookie))
                    {
                        _logger.LogInformation("Glooko authentication successful");
                        return true;
                    }
                }

                _logger.LogError($"Glooko authentication failed: {response.StatusCode}");

                // Try to read error response with decompression
                try
                {
                    var errorBytes = await response.Content.ReadAsByteArrayAsync();
                    string errorContent;
                    if (errorBytes.Length >= 2 && errorBytes[0] == 0x1F && errorBytes[1] == 0x8B)
                    {
                        using var compressedStream = new MemoryStream(errorBytes);
                        using var gzipStream = new System.IO.Compression.GZipStream(
                            compressedStream,
                            System.IO.Compression.CompressionMode.Decompress
                        );
                        using var decompressedStream = new MemoryStream();
                        await gzipStream.CopyToAsync(decompressedStream);
                        errorContent = Encoding.UTF8.GetString(decompressedStream.ToArray());
                    }
                    else
                    {
                        errorContent = Encoding.UTF8.GetString(errorBytes);
                    }
                    _logger.LogError($"Error response: {errorContent}");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Could not read error response: {ex.Message}");
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Glooko authentication error: {ex.Message}");
                return false;
            }
        }

        public override async Task<IEnumerable<Entry>> FetchGlucoseDataAsync(DateTime? since = null)
        {
            // Use the base class helper for file I/O and data fetching
            var entries = await FetchWithOptionalFileIOAsync(
                _config,
                async (s) => await FetchBatchDataAsync(s),
                TransformBatchDataToEntries,
                _fileService,
                "glooko_batch",
                since
            );

            var entriesList = entries.ToList();
            _logger.LogInformation(
                "[{ConnectorSource}] Retrieved {Count} glucose entries from Glooko (since: {Since})",
                ConnectorSource,
                entriesList.Count,
                since?.ToString("yyyy-MM-dd HH:mm:ss") ?? "default lookback"
            );

            return entriesList;
        }

        // Adapter method to match Func<TData, IEnumerable<Entry>> signature
        private IEnumerable<Entry> TransformBatchDataToEntries(GlookoBatchData batchData)
        {
            var entries = new List<Entry>();
            if (batchData?.Readings != null)
            {
                foreach (var reading in batchData.Readings)
                {
                    var entry = ParseEntry(reading);
                    if (entry != null)
                    {
                        entries.Add(entry);
                    }
                }
            }
            return entries;
        }

        /// <summary>
        /// Fetch comprehensive batch data from all Glooko endpoints
        /// This matches the legacy implementation's dataFromSession method
        /// </summary>
        /// <summary>
        /// Fetch comprehensive batch data from all Glooko endpoints
        /// This matches the legacy implementation's dataFromSession method
        /// </summary>
        public async Task<GlookoBatchData?> FetchBatchDataAsync(DateTime? since = null)
        {
            try
            {
                if (string.IsNullOrEmpty(_sessionCookie))
                {
                    throw new InvalidOperationException(
                        "Not authenticated with Glooko. Call AuthenticateAsync first."
                    );
                }
                if (_userData?.UserLogin?.GlookoCode == null)
                {
                    _logger.LogWarning("Missing Glooko user code, cannot fetch data");
                    return null;
                }

                // Calculate date range - fetch from specified date or last 24 hours
                var fromDate = since ?? DateTime.UtcNow.AddDays(-1);
                var toDate = DateTime.UtcNow;

                _logger.LogInformation(
                    $"Fetching comprehensive Glooko data from {fromDate:yyyy-MM-dd} to {toDate:yyyy-MM-dd}"
                );

                var batchData = new GlookoBatchData();

                // Define endpoints and their handlers
                var endpointDefinitions = new[]
                {
                    new
                    {
                        Endpoint = "/api/v2/foods",
                        Handler = new Action<JsonElement>(json =>
                        {
                            if (json.TryGetProperty("foods", out var element))
                                batchData.Foods =
                                    JsonSerializer.Deserialize<GlookoFood[]>(element.GetRawText())
                                    ?? Array.Empty<GlookoFood>();
                        }),
                    },
                    new
                    {
                        Endpoint = "/api/v2/pumps/scheduled_basals",
                        Handler = new Action<JsonElement>(json =>
                        {
                            if (json.TryGetProperty("scheduledBasals", out var element))
                                batchData.ScheduledBasals =
                                    JsonSerializer.Deserialize<GlookoBasal[]>(element.GetRawText())
                                    ?? Array.Empty<GlookoBasal>();
                        }),
                    },
                    new
                    {
                        Endpoint = "/api/v2/pumps/normal_boluses",
                        Handler = new Action<JsonElement>(json =>
                        {
                            if (json.TryGetProperty("normalBoluses", out var element))
                                batchData.NormalBoluses =
                                    JsonSerializer.Deserialize<GlookoBolus[]>(element.GetRawText())
                                    ?? Array.Empty<GlookoBolus>();
                        }),
                    },
                    new
                    {
                        Endpoint = "/api/v2/cgm/readings",
                        Handler = new Action<JsonElement>(json =>
                        {
                            if (json.TryGetProperty("readings", out var element))
                                batchData.Readings =
                                    JsonSerializer.Deserialize<GlookoCgmReading[]>(
                                        element.GetRawText()
                                    ) ?? Array.Empty<GlookoCgmReading>();
                        }),
                    },
                    new
                    {
                        Endpoint = "/api/v2/readings", // BGM Readings
                        Handler = new Action<JsonElement>(json =>
                        {
                            // Internal API returns 'readings' for BGM as well
                            if (json.TryGetProperty("readings", out var element))
                                batchData.BloodGlucose =
                                    JsonSerializer.Deserialize<GlookoBloodGlucoseReading[]>(
                                        element.GetRawText()
                                    ) ?? Array.Empty<GlookoBloodGlucoseReading>();
                        }),
                    },
                    new
                    {
                        Endpoint = "/api/v2/blood_pressures",
                        Handler = new Action<JsonElement>(json =>
                        {
                            if (json.TryGetProperty("bloodPressure", out var element))
                                batchData.BloodPressure =
                                    JsonSerializer.Deserialize<GlookoBloodPressureReading[]>(
                                        element.GetRawText()
                                    ) ?? Array.Empty<GlookoBloodPressureReading>();
                        }),
                    },
                    new
                    {
                        Endpoint = "/api/v2/exercises",
                        Handler = new Action<JsonElement>(json =>
                        {
                            if (json.TryGetProperty("activity", out var element))
                                batchData.Activity =
                                    JsonSerializer.Deserialize<GlookoActivityReading[]>(
                                        element.GetRawText()
                                    ) ?? Array.Empty<GlookoActivityReading>();
                        }),
                    },
                    // Medications endpoint removed due to 404 errors
                    /*
                    new
                    {
                        Endpoint = "/api/v2/medications",
                        Handler = new Action<JsonElement>(json =>
                        {
                            if (json.TryGetProperty("medications", out var element))
                                batchData.Medications = JsonSerializer.Deserialize<GlookoMedicationReading[]>(element.GetRawText()) ?? Array.Empty<GlookoMedicationReading>();
                        })
                    },
                    */
                    new
                    {
                        Endpoint = "/api/v2/pumps/extended_boluses",
                        Handler = new Action<JsonElement>(json =>
                        {
                            if (json.TryGetProperty("extendedBoluses", out var element))
                                batchData.ExtendedBoluses =
                                    JsonSerializer.Deserialize<GlookoExtendedBolus[]>(
                                        element.GetRawText()
                                    ) ?? Array.Empty<GlookoExtendedBolus>();
                        }),
                    },
                    new
                    {
                        Endpoint = "/api/v2/pumps/suspend_basals",
                        Handler = new Action<JsonElement>(json =>
                        {
                            if (json.TryGetProperty("suspendBasals", out var element))
                                batchData.SuspendBasals =
                                    JsonSerializer.Deserialize<GlookoSuspendBasal[]>(
                                        element.GetRawText()
                                    ) ?? Array.Empty<GlookoSuspendBasal>();
                        }),
                    },
                    new
                    {
                        Endpoint = "/api/v2/pumps/temporary_basals",
                        Handler = new Action<JsonElement>(json =>
                        {
                            if (json.TryGetProperty("temporaryBasals", out var element))
                                batchData.TempBasals =
                                    JsonSerializer.Deserialize<GlookoTempBasal[]>(
                                        element.GetRawText()
                                    ) ?? Array.Empty<GlookoTempBasal>();
                        }),
                    },
                    new
                    {
                        Endpoint = "/api/v2/pumps/settings",
                        Handler = new Action<JsonElement>(json =>
                        {
                            if (json.TryGetProperty("settings", out var element))
                                batchData.PumpSettings =
                                    JsonSerializer.Deserialize<GlookoPumpSettings[]>(
                                        element.GetRawText()
                                    ) ?? Array.Empty<GlookoPumpSettings>();
                        }),
                    },
                    new
                    {
                        Endpoint = "/api/v2/pumps/alarms",
                        Handler = new Action<JsonElement>(json =>
                        {
                            if (json.TryGetProperty("alarms", out var element))
                                batchData.PumpAlarms =
                                    JsonSerializer.Deserialize<GlookoPumpAlarm[]>(
                                        element.GetRawText()
                                    ) ?? Array.Empty<GlookoPumpAlarm>();
                        }),
                    },
                    new
                    {
                        Endpoint = "/api/v2/pumps/events",
                        Handler = new Action<JsonElement>(json =>
                        {
                            if (json.TryGetProperty("events", out var element))
                                batchData.PumpEvents =
                                    JsonSerializer.Deserialize<GlookoPumpEvent[]>(
                                        element.GetRawText()
                                    ) ?? Array.Empty<GlookoPumpEvent>();
                        }),
                    },
                };

                // Fetch endpoints sequentially with rate limiting
                for (int i = 0; i < endpointDefinitions.Length; i++)
                {
                    var def = endpointDefinitions[i];
                    var url = ConstructGlookoUrl(def.Endpoint, fromDate, toDate);

                    // Apply rate limiting strategy
                    await _rateLimitingStrategy.ApplyDelayAsync(i);

                    try
                    {
                        var result = await FetchFromGlookoEndpointWithRetry(url);
                        if (result.HasValue)
                        {
                            try
                            {
                                def.Handler(result.Value);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(
                                    ex,
                                    "Error parsing data from {Endpoint}",
                                    def.Endpoint
                                );
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Failed to fetch from {Url}. Continuing with other endpoints.",
                            url
                        );
                    }
                }

                // Log a summary of fetched data with source identifier
                _logger.LogInformation(
                    "[{ConnectorSource}] Fetched Glooko batch data summary: "
                        + "Readings={ReadingsCount}, Foods={FoodsCount}, Insulins={InsulinsCount}, "
                        + "NormalBoluses={BolusCount}, TempBasals={TempBasalCount}, "
                        + "ScheduledBasals={ScheduledBasalCount}, Activity={ActivityCount}",
                    ConnectorSource,
                    batchData.Readings?.Length ?? 0,
                    batchData.Foods?.Length ?? 0,
                    batchData.Insulins?.Length ?? 0,
                    batchData.NormalBoluses?.Length ?? 0,
                    batchData.TempBasals?.Length ?? 0,
                    batchData.ScheduledBasals?.Length ?? 0,
                    batchData.Activity?.Length ?? 0
                );

                return batchData;
            }
            catch (InvalidOperationException)
            {
                // Re-throw authentication-related exceptions
                throw;
            }
            catch (HttpRequestException)
            {
                // Re-throw HTTP-related exceptions (including rate limiting)
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error fetching Glooko batch data: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Transform Glooko batch data into Nightscout treatments
        ///
        /// This matches the nightscout-connect `transformData` method
        /// </summary>
        public List<Treatment> TransformBatchDataToTreatments(GlookoBatchData batchData)
        {
            var treatments = new List<Treatment>();

            try
            {
                // Apply timezone offset
                var timestampDelta = TimeSpan.FromHours(_config.TimezoneOffset);
                // Process foods (carb entries)
                if (batchData.Foods != null)
                {
                    foreach (var food in batchData.Foods)
                    {
                        var treatment = new Treatment();
                        var foodDate = GetRawGlookoDate(food.Timestamp, food.PumpTimestamp);

                        // Look for matching insulin within 45 minutes
                        var matchingInsulin = FindMatchingInsulin(batchData.Insulins, foodDate);

                        if (matchingInsulin != null)
                        if (matchingInsulin != null)
                        {
                            var insulinDate = GetRawGlookoDate(matchingInsulin.Timestamp, matchingInsulin.PumpTimestamp);
                            treatment.EventType = "Meal Bolus";
                            treatment.EventType = "Meal Bolus";
                            treatment.CreatedAt = GetCorrectedGlookoTime(insulinDate).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                            treatment.Insulin = matchingInsulin.Value;
                            treatment.PreBolus = (foodDate - insulinDate).TotalMinutes;
                            treatment.Id = GenerateTreatmentId(
                                "Meal Bolus",
                                insulinDate,
                                $"carbs:{food.Carbs}_insulin:{matchingInsulin.Value}"
                            );
                        }
                        else
                        {
                            treatment.EventType = "Carb Correction";
                            treatment.CreatedAt = GetCorrectedGlookoTime(foodDate).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                            treatment.Id = GenerateTreatmentId(
                                "Carb Correction",
                                foodDate,
                                $"carbs:{food.Carbs}"
                            );
                        }

                        treatment.Carbs = food.Carbs > 0 ? food.Carbs : food.CarbohydrateGrams;
                        treatment.AdditionalProperties = JsonSerializer.Deserialize<
                            Dictionary<string, object>
                        >(JsonSerializer.Serialize(food));
                        treatment.DataSource = ConnectorSource;
                        treatments.Add(treatment);
                    }
                }
                // Process insulins (correction boluses)
                if (batchData.Insulins != null)
                {
                    foreach (var insulin in batchData.Insulins)
                    {
                        var insulinDate = GetRawGlookoDate(insulin.Timestamp, insulin.PumpTimestamp);

                        // Only create correction bolus if no matching food
                        var matchingFood = FindMatchingFood(batchData.Foods, insulinDate);
                        if (matchingFood == null)
                        {
                            var treatment = new Treatment
                            {
                                Id = GenerateTreatmentId(
                                    "Correction Bolus",
                                    insulinDate,
                                    $"insulin:{insulin.Value}"
                                ),
                                EventType = "Correction Bolus",
                                CreatedAt = GetCorrectedGlookoTime(insulinDate).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                                Insulin = insulin.Value,
                                DataSource = ConnectorSource,
                            };
                            treatments.Add(treatment);
                        }
                    }
                }
                // Process pump boluses
                if (batchData.NormalBoluses != null)
                {
                    foreach (var bolus in batchData.NormalBoluses)
                    {
                        var bolusDate = GetRawGlookoDate(bolus.Timestamp, bolus.PumpTimestamp);

                        var treatment = new Treatment
                        {
                            Id = GenerateTreatmentId(
                                "Meal Bolus",
                                bolusDate,
                                $"insulin:{bolus.InsulinDelivered}_carbs:{bolus.CarbsInput}"
                            ),
                            EventType = "Meal Bolus",
                            CreatedAt = GetCorrectedGlookoTime(bolusDate).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                            Insulin = bolus.InsulinDelivered,
                            Carbs = bolus.CarbsInput > 0 ? bolus.CarbsInput : null,
                            AdditionalProperties = JsonSerializer.Deserialize<
                                Dictionary<string, object>
                            >(JsonSerializer.Serialize(bolus)),
                            DataSource = ConnectorSource,
                        };
                        treatments.Add(treatment);
                    }
                }
                // Process scheduled basals (temp basals)
                if (batchData.ScheduledBasals != null)
                {
                    foreach (var basal in batchData.ScheduledBasals)
                    {
                        var basalDate = GetRawGlookoDate(basal.Timestamp, basal.PumpTimestamp);

                        // Duration is in seconds, rate is U/hr
                        // Calculate insulin delivered: rate (U/hr) × duration (seconds) / 3600 (seconds/hr)
                        var durationMinutes = basal.Duration / 60.0;
                        var insulinDelivered = basal.Rate * basal.Duration / 3600.0;

                        var treatment = new Treatment
                        {
                            Id = GenerateTreatmentId(
                                "Temp Basal",
                                basalDate,
                                $"rate:{basal.Rate}_duration:{basal.Duration}"
                            ),
                            EventType = "Temp Basal",
                            CreatedAt = GetCorrectedGlookoTime(basalDate).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                            Rate = basal.Rate,
                            Absolute = basal.Rate,
                            Duration = durationMinutes,
                            Insulin = insulinDelivered > 0 ? insulinDelivered : null,
                            AdditionalProperties = JsonSerializer.Deserialize<
                                Dictionary<string, object>
                            >(JsonSerializer.Serialize(basal)),
                            DataSource = ConnectorSource,
                        };
                        treatments.Add(treatment);
                    }
                }

                _logger.LogInformation(
                    "[{ConnectorSource}] Generated {Count} treatments from Glooko batch data",
                    ConnectorSource,
                    treatments.Count
                );
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error transforming Glooko data to treatments: {ex.Message}");
            }

            return treatments;
        }

        /// <summary>
        /// Fetch Glooko data and upload treatments to Nightscout
        /// This provides the complete treatment upload workflow
        /// </summary>
        public async Task<bool> FetchAndUploadTreatmentsAsync(
            DateTime? since = null,
            GlookoConnectorConfiguration? config = null
        )
        {
            try
            {
                if (string.IsNullOrEmpty(_sessionCookie))
                {
                    _logger.LogWarning(
                        "Not authenticated with Glooko. Call AuthenticateAsync first."
                    );
                    return false;
                }

                var actualConfig = config ?? _config;
                _logger.LogInformation(
                    $"FetchAndUploadTreatmentsAsync: SaveRawData={actualConfig.SaveRawData}, DataDirectory={actualConfig.DataDirectory}"
                );

                // Use catch-up functionality to determine optimal since timestamp
                var effectiveSince = await CalculateSinceTimestampAsync(actualConfig, since);

                // Fetch comprehensive batch data from Glooko
                var batchData = await FetchBatchDataAsync(effectiveSince);
                if (batchData == null)
                {
                    _logger.LogWarning("No batch data retrieved from Glooko");
                    return false;
                }

                // Save raw data files if configured - now using reflection-based helper
                await SaveBatchDataAsync(batchData, ServiceName, actualConfig, _logger);

                // Transform to treatments
                var nightscoutTreatments = TransformBatchDataToTreatments(batchData);
                if (nightscoutTreatments.Count == 0)
                {
                    _logger.LogInformation("No treatments to upload");
                    return true;
                }

                // Convert to Core Treatment models
                var treatments = nightscoutTreatments.Select(t => t.ToTreatment()).ToList();

                // Save transformed treatments if configured to do so
                if (actualConfig.SaveRawData)
                {
                    await SaveTreatmentsToFileAsync(treatments, ServiceName, actualConfig, _logger);
                }
                // Upload treatments to Nocturne API (via ApiDataSubmitter)
                var success = await PublishTreatmentDataInBatchesAsync(treatments, actualConfig);

                _logger.LogInformation(
                    $"Treatment upload {(success ? "succeeded" : "failed")}: {treatments.Count} treatments"
                );

                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error fetching and uploading treatments: {ex.Message}");
                return false;
            }
        }

        private string ConstructGlookoUrl(string endpoint, DateTime startDate, DateTime endDate)
        {
            var patientCode = _userData?.UserLogin?.GlookoCode;

            // Add the required parameters matching the legacy implementation
            var lastGuid = "1e0c094e-1e54-4a4f-8e6a-f94484b53789"; // hardcoded as per legacy
            var maxCount = Math.Max(1, (int)Math.Ceiling((endDate - startDate).TotalMinutes / 5)); // 5-minute intervals

            return $"{endpoint}?patient={patientCode}&startDate={startDate:yyyy-MM-ddTHH:mm:ss.fffZ}&endDate={endDate:yyyy-MM-ddTHH:mm:ss.fffZ}&lastGuid={lastGuid}&lastUpdatedAt={startDate:yyyy-MM-ddTHH:mm:ss.fffZ}&limit={maxCount}";
        }

        private async Task<JsonElement?> FetchFromGlookoEndpoint(string url)
        {
            try
            {
                _logger.LogDebug($"GLOOKO FETCHER LOADING {url}");

                var request = new HttpRequestMessage(HttpMethod.Get, url);

                // Add required headers (matching legacy implementation)
                request.Headers.TryAddWithoutValidation(
                    "Accept",
                    "application/json, text/plain, */*"
                );
                request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate, br");
                request.Headers.TryAddWithoutValidation(
                    "User-Agent",
                    "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.5 Safari/605.1.15"
                );
                request.Headers.TryAddWithoutValidation("Referer", "https://eu.my.glooko.com/");
                request.Headers.TryAddWithoutValidation("Origin", "https://eu.my.glooko.com");
                request.Headers.TryAddWithoutValidation("Connection", "keep-alive");
                request.Headers.TryAddWithoutValidation("Accept-Language", "en-GB,en;q=0.9");
                request.Headers.TryAddWithoutValidation("Cookie", _sessionCookie);
                request.Headers.TryAddWithoutValidation("Host", _config.GlookoServer);
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-site");

                var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    // Read response as bytes first to handle compression properly
                    var responseBytes = await response.Content.ReadAsByteArrayAsync();

                    // Decompress if needed (check for gzip magic number 0x1F 0x8B)
                    string responseJson;
                    if (
                        responseBytes.Length >= 2
                        && responseBytes[0] == 0x1F
                        && responseBytes[1] == 0x8B
                    )
                    {
                        using var compressedStream = new MemoryStream(responseBytes);
                        using var gzipStream = new System.IO.Compression.GZipStream(
                            compressedStream,
                            System.IO.Compression.CompressionMode.Decompress
                        );
                        using var decompressedStream = new MemoryStream();
                        await gzipStream.CopyToAsync(decompressedStream);
                        responseJson = Encoding.UTF8.GetString(decompressedStream.ToArray());
                    }
                    else
                    {
                        responseJson = Encoding.UTF8.GetString(responseBytes);
                    }

                    return JsonSerializer.Deserialize<JsonElement>(responseJson);
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity) // 422
                {
                    _logger.LogWarning($"Rate limited (422) fetching from {url}");
                    throw new HttpRequestException($"422 UnprocessableEntity - Rate limited");
                }
                else
                {
                    _logger.LogWarning($"Failed to fetch from {url}: {response.StatusCode}");
                    throw new HttpRequestException(
                        $"HTTP {(int)response.StatusCode} {response.StatusCode}"
                    );
                }
            }
            catch (HttpRequestException)
            {
                // Re-throw HTTP exceptions (including rate limiting) to be handled by retry logic
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error fetching from {url}: {ex.Message}");
                throw new HttpRequestException($"Request failed: {ex.Message}", ex);
            }
        }

        private GlookoInsulin? FindMatchingInsulin(GlookoInsulin[]? insulins, DateTime foodDate)
        {
            if (insulins == null)
                return null;

            return insulins.FirstOrDefault(insulin =>
            {
                var insulinDate = DateTime.Parse(insulin.Timestamp);
                var timeDifference = Math.Abs((foodDate - insulinDate).TotalMinutes);
                return timeDifference < 46; // Within 45 minutes
            });
        }

        /// <summary>
        /// Generate a unique ID for a treatment based on its data and timestamp
        /// </summary>
        private string GenerateTreatmentId(
            string eventType,
            DateTime timestamp,
            string? additionalData = null
        )
        {
            var dataToHash = $"glooko_{eventType}_{timestamp.Ticks}_{additionalData ?? ""}";
            using var sha1 = SHA1.Create();
            var hashBytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(dataToHash));
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        private GlookoFood? FindMatchingFood(GlookoFood[]? foods, DateTime insulinDate)
        {
            if (foods == null)
                return null;

            return foods.FirstOrDefault(food =>
            {
                var foodDate = DateTime.Parse(food.Timestamp);
                var timeDifference = Math.Abs((insulinDate - foodDate).TotalMinutes);
                return timeDifference < 46; // Within 45 minutes
            });
        }

        private Entry? ParseEntry(GlookoCgmReading reading)
        {
            try
            {
                if (string.IsNullOrEmpty(reading.Timestamp) || reading.Value <= 0)
                {
                    return null;
                }

                var date = DateTime.Parse(reading.Timestamp).ToUniversalTime();

                // Apply timezone offset if configured
                if (_config.TimezoneOffset != 0)
                {
                    date = date.AddHours(-_config.TimezoneOffset);
                }

                var entry = new Entry
                {
                    Date = date,
                    Sgv = reading.Value,
                    Type = "sgv",
                    Device = ConnectorSource,
                    Direction = ParseTrendToDirection(reading.Trend).ToString(),
                    Id = $"glooko_{date.Ticks}",
                };

                return entry;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error parsing Glooko CGM reading: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Fetch from Glooko endpoint with retry logic and exponential backoff
        /// Implements the legacy rate limiting strategy to avoid 422 errors
        /// </summary>
        private async Task<JsonElement?> FetchFromGlookoEndpointWithRetry(
            string url,
            int maxRetries = 3
        )
        {
            HttpRequestException? lastException = null;

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    var result = await FetchFromGlookoEndpoint(url);
                    if (result.HasValue)
                    {
                        return result;
                    }

                    // If we get here, the request failed but didn't throw
                    _logger.LogWarning($"Attempt {attempt + 1} failed for {url}");
                }
                catch (HttpRequestException ex) when (ex.Message.Contains("422"))
                {
                    lastException = ex;
                    _logger.LogWarning($"Rate limited (422) on attempt {attempt + 1} for {url}");
                }
                catch (HttpRequestException ex)
                {
                    lastException = ex;
                    _logger.LogError($"Attempt {attempt + 1} failed for {url}: {ex.Message}");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Attempt {attempt + 1} failed for {url}: {ex.Message}");
                    lastException = new HttpRequestException($"Request failed: {ex.Message}", ex);
                } // Don't delay after the last attempt
                if (attempt < maxRetries - 1)
                {
                    _logger.LogInformation($"Applying retry backoff before retry {attempt + 2}");
                    await _retryDelayStrategy.ApplyRetryDelayAsync(attempt);
                }
            }

            _logger.LogError($"All {maxRetries} attempts failed for {url}");

            // Throw the last exception if we have one, otherwise throw a generic exception
            if (lastException != null)
            {
                throw lastException;
            }
            throw new HttpRequestException($"All {maxRetries} attempts failed for {url}");
        }

        /// <summary>
        /// Syncs Glooko health data using message publishing when available, with fallback to direct API
        /// This includes glucose, blood pressure, weight, and sleep data
        /// </summary>
        /// <param name="config">Connector configuration</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>True if sync was successful</returns>
        public async Task<bool> SyncGlookoHealthDataAsync(
            GlookoConnectorConfiguration config,
            CancellationToken cancellationToken = default,
            DateTime? since = null
        )
        {
            try
            {
                _logger.LogInformation(
                    "Starting Glooko health data sync using {Mode} mode",
                    config.UseAsyncProcessing ? "asynchronous" : "direct API"
                );

                // Authenticate first if we don't have a session cookie
                if (string.IsNullOrEmpty(_sessionCookie))
                {
                    _logger.LogInformation("Authenticating with Glooko...");
                    var authSuccess = await AuthenticateAsync();
                    if (!authSuccess)
                    {
                        _logger.LogError("Failed to authenticate with Glooko");
                        return false;
                    }
                }

                if (config.UseAsyncProcessing && _apiDataSubmitter != null)
                {
                    // For Glooko, we can fetch comprehensive health data and publish it
                    var sinceTimestamp = await CalculateSinceTimestampAsync(config, since);
                    var glucoseEntries = await FetchGlucoseDataAsync(sinceTimestamp);

                    // Publish health data (for now, just glucose - other health data would require additional API calls)
                    var success = await PublishHealthDataAsync(
                        glucoseEntries.ToArray(),
                        Array.Empty<object>(), // Blood pressure readings - would need separate API call
                        Array.Empty<object>(), // Weight readings - would need separate API call
                        Array.Empty<object>(), // Sleep readings - would need separate API call
                        config,
                        cancellationToken
                    );

                    if (success)
                    {
                        _logger.LogInformation("Glooko health data published successfully via API");
                        return true;
                    }
                    else
                    {
                        _logger.LogWarning("Health data publishing failed");
                        return false;
                    }
                }
                else
                {
                    // Use traditional sync method
                    var success = await SyncDataAsync(config, cancellationToken, since);
                    if (success)
                    {
                        _logger.LogInformation(
                            "Glooko health data sync completed successfully via direct API"
                        );
                    }
                    return success;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during Glooko health data sync");
                return false;
            }
        }

        /// <summary>
        /// Parses a Glooko trend string to Direction enum
        /// </summary>
        /// <param name="trend">The trend string from Glooko API</param>
        /// <returns>Corresponding Direction enum value</returns>
        private static Direction ParseTrendToDirection(string? trend)
        {
            if (string.IsNullOrWhiteSpace(trend))
                return Direction.Flat;

            return trend.ToUpperInvariant() switch
            {
                "DOUBLEUP" or "DOUBLE_UP" => Direction.DoubleUp,
                "SINGLEUP" or "SINGLE_UP" => Direction.SingleUp,
                "FORTYFIVEUP" or "FORTY_FIVE_UP" => Direction.FortyFiveUp,
                "FLAT" => Direction.Flat,
                "FORTYFIVEDOWN" or "FORTY_FIVE_DOWN" => Direction.FortyFiveDown,
                "SINGLEDOWN" or "SINGLE_DOWN" => Direction.SingleDown,
                "DOUBLEDOWN" or "DOUBLE_DOWN" => Direction.DoubleDown,
                "TRIPLEUP" or "TRIPLE_UP" => Direction.TripleUp,
                "TRIPLEDOWN" or "TRIPLE_DOWN" => Direction.TripleDown,
                "NOT COMPUTABLE" or "NOTCOMPUTABLE" => Direction.NotComputable,
                "RATE OUT OF RANGE" or "RATEOUTOFRANGE" => Direction.RateOutOfRange,
                _ => Direction.Flat, // Default fallback
            };
        }

        private DateTime GetCorrectedGlookoTime(DateTime rawDate, bool isV3 = false)
        {
            // V2 sends tomorrow's date with a timezone offset.
            // V3 sends today's date with a timezone offset.
            // In both cases, the time is local time, but marked as UTC.

            // To correct V2: Subtract offset AND 24 hours.
            // To correct V3: Subtract offset.

            var offsetHours = _config.TimezoneOffset + (isV3 ? 0 : 24);
            var corrected = rawDate.AddHours(-offsetHours);
             _logger.LogInformation("GetCorrectedGlookoTime: Raw={Raw}, IsV3={IsV3}, ConfigOffset={ConfigOffset}, CalcOffset={CalcOffset}, Result={Result}",
                rawDate, isV3, _config.TimezoneOffset, offsetHours, corrected);
            return corrected;
        }

        private DateTime GetRawGlookoDate(string timestamp, string? pumpTimestamp)
        {
             return DateTime.Parse(
                 !string.IsNullOrEmpty(pumpTimestamp) ? pumpTimestamp : timestamp,
                 System.Globalization.CultureInfo.InvariantCulture,
                 System.Globalization.DateTimeStyles.RoundtripKind
             );
        }
    }
}
