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
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Core.Models;

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
        private new readonly ILogger<GlookoConnectorService> _logger;
        private readonly IRateLimitingStrategy _rateLimitingStrategy;
        private readonly IRetryDelayStrategy _retryDelayStrategy;
        private readonly IConnectorFileService<GlookoBatchData> _fileService;
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
        public override string ConnectorSource => "glooko";

        public GlookoConnectorService(
            GlookoConnectorConfiguration config,
            ILogger<GlookoConnectorService> logger
        )
            : base()
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _retryDelayStrategy = new ProductionRetryDelayStrategy();
            _rateLimitingStrategy = new ProductionRateLimitingStrategy(
                LoggerFactory
                    .Create(builder => builder.AddConsole())
                    .CreateLogger<ProductionRateLimitingStrategy>()
            );
            _fileService = new ConnectorFileService<GlookoBatchData>(
                LoggerFactory
                    .Create(builder => builder.AddConsole())
                    .CreateLogger<ConnectorFileService<GlookoBatchData>>()
            );
        }

        public GlookoConnectorService(
            GlookoConnectorConfiguration config,
            ILogger<GlookoConnectorService> logger,
            IRetryDelayStrategy retryDelayStrategy
        )
            : base()
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _retryDelayStrategy =
                retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));
            _rateLimitingStrategy = new ProductionRateLimitingStrategy(
                LoggerFactory
                    .Create(builder => builder.AddConsole())
                    .CreateLogger<ProductionRateLimitingStrategy>()
            );
            _fileService = new ConnectorFileService<GlookoBatchData>(
                LoggerFactory
                    .Create(builder => builder.AddConsole())
                    .CreateLogger<ConnectorFileService<GlookoBatchData>>()
            );
        }

        public GlookoConnectorService(
            GlookoConnectorConfiguration config,
            ILogger<GlookoConnectorService> logger,
            IRetryDelayStrategy retryDelayStrategy,
            IRateLimitingStrategy rateLimitingStrategy
        )
            : base()
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _retryDelayStrategy =
                retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));
            _rateLimitingStrategy =
                rateLimitingStrategy
                ?? throw new ArgumentNullException(nameof(rateLimitingStrategy));
            _fileService = new ConnectorFileService<GlookoBatchData>(
                LoggerFactory
                    .Create(builder => builder.AddConsole())
                    .CreateLogger<ConnectorFileService<GlookoBatchData>>()
            );
        }

        public GlookoConnectorService(
            GlookoConnectorConfiguration config,
            ILogger<GlookoConnectorService> logger,
            HttpClient httpClient
        )
            : base(httpClient)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _retryDelayStrategy = new ProductionRetryDelayStrategy();
            _rateLimitingStrategy = new ProductionRateLimitingStrategy(
                LoggerFactory
                    .Create(builder => builder.AddConsole())
                    .CreateLogger<ProductionRateLimitingStrategy>()
            );
            _fileService = new ConnectorFileService<GlookoBatchData>(
                LoggerFactory
                    .Create(builder => builder.AddConsole())
                    .CreateLogger<ConnectorFileService<GlookoBatchData>>()
            );
        }

        public GlookoConnectorService(
            GlookoConnectorConfiguration config,
            ILogger<GlookoConnectorService> logger,
            HttpClient httpClient,
            IApiDataSubmitter apiDataSubmitter
        )
            : base(httpClient, apiDataSubmitter, logger)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _retryDelayStrategy = new ProductionRetryDelayStrategy();
            _rateLimitingStrategy = new ProductionRateLimitingStrategy(
                LoggerFactory
                    .Create(builder => builder.AddConsole())
                    .CreateLogger<ProductionRateLimitingStrategy>()
            );
            _fileService = new ConnectorFileService<GlookoBatchData>(
                LoggerFactory
                    .Create(builder => builder.AddConsole())
                    .CreateLogger<ConnectorFileService<GlookoBatchData>>()
            );
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
                        email = _config.GlookoEmail,
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

                var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"https://{_config.GlookoServer}/api/v2/users/sign_in"
                )
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
                    if (responseBytes.Length >= 2 && responseBytes[0] == 0x1F && responseBytes[1] == 0x8B)
                    {
                        using var compressedStream = new MemoryStream(responseBytes);
                        using var gzipStream = new System.IO.Compression.GZipStream(compressedStream, System.IO.Compression.CompressionMode.Decompress);
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
                            _logger.LogInformation("User data parsed successfully. Glooko code: {GlookoCode}", _userData.UserLogin.GlookoCode);
                        }
                        else
                        {
                            _logger.LogWarning("User data parsed but GlookoCode is missing");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"Could not parse user data: {ex.Message}");
                        _logger.LogDebug("Response JSON: {ResponseJson}", responseJson.Substring(0, Math.Min(500, responseJson.Length)));
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
                        using var gzipStream = new System.IO.Compression.GZipStream(compressedStream, System.IO.Compression.CompressionMode.Decompress);
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
            var entries = new List<Entry>();

            try
            {
                GlookoBatchData? batchData = null;

                // Check if we should load from file instead of fetching from API
                if (_config.LoadFromFile)
                {
                    var fileToLoad = _config.LoadFilePath;

                    if (!string.IsNullOrEmpty(fileToLoad))
                    {
                        _logger.LogInformation(
                            "Loading Glooko data from specified file: {FilePath}",
                            fileToLoad
                        );
                        batchData = await _fileService.LoadDataAsync(fileToLoad);
                    }
                    else
                    {
                        // Load from most recent file in data directory
                        var dataDir = _config.DataDirectory;
                        var filePrefix = "glooko_batch";
                        var availableFiles = _fileService.GetAvailableDataFiles(
                            dataDir,
                            filePrefix
                        );

                        if (availableFiles.Length > 0)
                        {
                            var mostRecentFile = _fileService.GetMostRecentDataFile(
                                dataDir,
                                filePrefix
                            );
                            if (mostRecentFile != null)
                            {
                                _logger.LogInformation(
                                    "Loading Glooko data from most recent file: {FilePath}",
                                    mostRecentFile
                                );
                                batchData = await _fileService.LoadDataAsync(mostRecentFile);
                            }
                        }
                        else
                        {
                            _logger.LogWarning(
                                "No saved Glooko data files found in directory: {DataDirectory}",
                                dataDir
                            );
                        }
                    }
                }

                // If no data loaded from file, fetch from API
                if (batchData == null)
                {
                    _logger.LogInformation("Fetching fresh data from Glooko API");

                    // Use catch-up functionality to determine optimal since timestamp
                    var effectiveSince = await CalculateSinceTimestampAsync(_config, since);
                    batchData = await FetchBatchDataAsync(effectiveSince);

                    // Save the fetched data if SaveRawData is enabled
                    if (batchData != null && _config.SaveRawData)
                    {
                        var dataDir = _config.DataDirectory;
                        var filePrefix = "glooko_batch";
                        var savedPath = await _fileService.SaveDataAsync(
                            batchData,
                            dataDir,
                            filePrefix
                        );
                        if (savedPath != null)
                        {
                            _logger.LogInformation(
                                "Saved Glooko batch data for debugging: {FilePath}",
                                savedPath
                            );
                        }
                    }
                }

                // Convert CGM readings to glucose entries
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

                _logger.LogInformation(
                    "Retrieved {Count} glucose entries from Glooko",
                    entries.Count
                );
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
                _logger.LogError(ex, "Error fetching Glooko glucose data");
            }

            return entries;
        }

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

                // Define all endpoints to fetch (comprehensive diabetes management data)
                var endpoints = new[]
                {
                    "/api/v2/foods",
                    "/api/v2/insulins",
                    "/api/v2/pumps/scheduled_basals",
                    "/api/v2/pumps/normal_boluses",
                    "/api/v2/cgm/readings",
                    "/api/v2/blood_glucose",
                    "/api/v2/ketones",
                    "/api/v2/blood_pressure",
                    "/api/v2/weight",
                    "/api/v2/sleep",
                    "/api/v2/activity",
                    "/api/v2/medications",
                    "/api/v2/insulin_pens",
                };

                // Build URLs with patient and date parameters
                var urlsToFetch = endpoints
                    .Select(endpoint => ConstructGlookoUrl(endpoint, fromDate, toDate))
                    .ToArray();
                // Fetch endpoints sequentially with rate limiting to avoid 422 errors
                var results = new JsonElement?[urlsToFetch.Length];
                for (int i = 0; i < urlsToFetch.Length; i++)
                {
                    // Apply rate limiting strategy
                    await _rateLimitingStrategy.ApplyDelayAsync(i);

                    results[i] = await FetchFromGlookoEndpointWithRetry(urlsToFetch[i]);
                }

                // Parse results into batch data structure
                var batchData = new GlookoBatchData();
                try
                {
                    for (int i = 0; i < results.Length; i++)
                    {
                        if (!results[i].HasValue)
                            continue;

                        var result = results[i]!.Value;
                        switch (i)
                        {
                            case 0: // foods
                                if (result.TryGetProperty("foods", out var foodsElement))
                                    batchData.Foods =
                                        JsonSerializer.Deserialize<GlookoFood[]>(
                                            foodsElement.GetRawText()
                                        ) ?? new GlookoFood[0];
                                break;
                            case 1: // insulins
                                if (result.TryGetProperty("insulins", out var insulinsElement))
                                    batchData.Insulins =
                                        JsonSerializer.Deserialize<GlookoInsulin[]>(
                                            insulinsElement.GetRawText()
                                        ) ?? new GlookoInsulin[0];
                                break;
                            case 2: // scheduled basals
                                if (result.TryGetProperty("scheduledBasals", out var basalsElement))
                                    batchData.ScheduledBasals =
                                        JsonSerializer.Deserialize<GlookoBasal[]>(
                                            basalsElement.GetRawText()
                                        ) ?? new GlookoBasal[0];
                                break;
                            case 3: // normal boluses
                                if (result.TryGetProperty("normalBoluses", out var bolusesElement))
                                    batchData.NormalBoluses =
                                        JsonSerializer.Deserialize<GlookoBolus[]>(
                                            bolusesElement.GetRawText()
                                        ) ?? new GlookoBolus[0];
                                break;
                            case 4: // cgm readings
                                if (result.TryGetProperty("readings", out var readingsElement))
                                    batchData.Readings =
                                        JsonSerializer.Deserialize<GlookoCgmReading[]>(
                                            readingsElement.GetRawText()
                                        ) ?? new GlookoCgmReading[0];
                                break;
                            case 5: // blood glucose
                                if (result.TryGetProperty("bloodGlucose", out var bgElement))
                                    batchData.BloodGlucose =
                                        JsonSerializer.Deserialize<GlookoBloodGlucoseReading[]>(
                                            bgElement.GetRawText()
                                        ) ?? new GlookoBloodGlucoseReading[0];
                                break;
                            case 6: // ketones
                                if (result.TryGetProperty("ketones", out var ketonesElement))
                                    batchData.Ketones =
                                        JsonSerializer.Deserialize<GlookoKetoneReading[]>(
                                            ketonesElement.GetRawText()
                                        ) ?? new GlookoKetoneReading[0];
                                break;
                            case 7: // blood pressure
                                if (result.TryGetProperty("bloodPressure", out var bpElement))
                                    batchData.BloodPressure =
                                        JsonSerializer.Deserialize<GlookoBloodPressureReading[]>(
                                            bpElement.GetRawText()
                                        ) ?? new GlookoBloodPressureReading[0];
                                break;
                            case 8: // weight
                                if (result.TryGetProperty("weight", out var weightElement))
                                    batchData.Weight =
                                        JsonSerializer.Deserialize<GlookoWeightReading[]>(
                                            weightElement.GetRawText()
                                        ) ?? new GlookoWeightReading[0];
                                break;
                            case 9: // sleep
                                if (result.TryGetProperty("sleep", out var sleepElement))
                                    batchData.Sleep =
                                        JsonSerializer.Deserialize<GlookoSleepReading[]>(
                                            sleepElement.GetRawText()
                                        ) ?? new GlookoSleepReading[0];
                                break;
                            case 10: // activity
                                if (result.TryGetProperty("activity", out var activityElement))
                                    batchData.Activity =
                                        JsonSerializer.Deserialize<GlookoActivityReading[]>(
                                            activityElement.GetRawText()
                                        ) ?? new GlookoActivityReading[0];
                                break;
                            case 11: // medications
                                if (
                                    result.TryGetProperty("medications", out var medicationsElement)
                                )
                                    batchData.Medications =
                                        JsonSerializer.Deserialize<GlookoMedicationReading[]>(
                                            medicationsElement.GetRawText()
                                        ) ?? new GlookoMedicationReading[0];
                                break;
                            case 12: // insulin pens
                                if (result.TryGetProperty("insulinPens", out var pensElement))
                                    batchData.InsulinPens =
                                        JsonSerializer.Deserialize<GlookoInsulinPenReading[]>(
                                            pensElement.GetRawText()
                                        ) ?? new GlookoInsulinPenReading[0];
                                break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Error parsing batch data: {ex.Message}");
                }
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
                var timestampDelta = TimeSpan.FromHours(_config.GlookoTimezoneOffset);
                // Process foods (carb entries)
                if (batchData.Foods != null)
                {
                    foreach (var food in batchData.Foods)
                    {
                        var treatment = new Treatment();
                        var foodDate = DateTime.Parse(food.Timestamp);

                        // Look for matching insulin within 45 minutes
                        var matchingInsulin = FindMatchingInsulin(batchData.Insulins, foodDate);

                        if (matchingInsulin != null)
                        {
                            var insulinDate = DateTime.Parse(matchingInsulin.Timestamp);
                            treatment.EventType = "Meal Bolus";
                            treatment.EventTime = insulinDate
                                .Add(timestampDelta)
                                .ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
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
                            treatment.EventTime = foodDate
                                .Add(timestampDelta)
                                .ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                            treatment.Id = GenerateTreatmentId(
                                "Carb Correction",
                                foodDate,
                                $"carbs:{food.Carbs}"
                            );
                        }

                        treatment.Carbs = food.Carbs > 0 ? food.Carbs : food.CarbohydrateGrams;
                        treatment.Notes = JsonSerializer.Serialize(food);
                        treatment.Source = ConnectorSource;
                        treatments.Add(treatment);
                    }
                }
                // Process insulins (correction boluses)
                if (batchData.Insulins != null)
                {
                    foreach (var insulin in batchData.Insulins)
                    {
                        var insulinDate = DateTime.Parse(insulin.Timestamp);

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
                                EventTime = insulinDate
                                    .Add(timestampDelta)
                                    .ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                                Insulin = insulin.Value,
                                Source = ConnectorSource,
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
                        var timestamp = !string.IsNullOrEmpty(bolus.PumpTimestamp)
                            ? bolus.PumpTimestamp
                            : bolus.Timestamp;
                        var bolusDate = DateTime.Parse(timestamp);

                        var treatment = new Treatment
                        {
                            Id = GenerateTreatmentId(
                                "Meal Bolus",
                                bolusDate,
                                $"insulin:{bolus.InsulinDelivered}_carbs:{bolus.CarbsInput}"
                            ),
                            EventType = "Meal Bolus",
                            EventTime = bolusDate
                                .Add(timestampDelta)
                                .ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                            Insulin = bolus.InsulinDelivered,
                            Carbs = bolus.CarbsInput > 0 ? bolus.CarbsInput : null,
                            Notes = JsonSerializer.Serialize(bolus),
                            Source = ConnectorSource,
                        };
                        treatments.Add(treatment);
                    }
                }
                // Process scheduled basals (temp basals)
                if (batchData.ScheduledBasals != null)
                {
                    foreach (var basal in batchData.ScheduledBasals)
                    {
                        var timestamp = !string.IsNullOrEmpty(basal.PumpTimestamp)
                            ? basal.PumpTimestamp
                            : basal.Timestamp;
                        var basalDate = DateTime.Parse(timestamp);

                        var treatment = new Treatment
                        {
                            Id = GenerateTreatmentId(
                                "Temp Basal",
                                basalDate,
                                $"rate:{basal.Rate}_duration:{basal.Duration}"
                            ),
                            EventType = "Temp Basal",
                            CreatedAt = basalDate
                                .Add(timestampDelta)
                                .ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                            Rate = basal.Rate,
                            Absolute = basal.Rate,
                            Duration = basal.Duration / 60.0, // Convert seconds to minutes
                            Notes = JsonSerializer.Serialize(basal),
                            Source = ConnectorSource,
                        };
                        treatments.Add(treatment);
                    }
                }

                _logger.LogInformation($"Generated {treatments.Count} treatments from Glooko data");
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

                // Use catch-up functionality to determine optimal since timestamp
                var effectiveSince = await CalculateSinceTimestampAsync(actualConfig, since);

                // Fetch comprehensive batch data from Glooko
                var batchData = await FetchBatchDataAsync(effectiveSince);
                if (batchData == null)
                {
                    _logger.LogWarning("No batch data retrieved from Glooko");
                    return false;
                } // Save raw data files if configured to do so
                if (actualConfig.SaveRawData)
                {
                    // Save each data type separately using base class methods
                    if (batchData.Foods != null && batchData.Foods.Length > 0)
                    {
                        await SaveDataByTypeAsync(
                            batchData.Foods,
                            "foods",
                            ServiceName,
                            actualConfig,
                            _logger
                        );
                    }

                    if (batchData.Insulins != null && batchData.Insulins.Length > 0)
                    {
                        await SaveDataByTypeAsync(
                            batchData.Insulins,
                            "insulins",
                            ServiceName,
                            actualConfig,
                            _logger
                        );
                    }

                    if (batchData.NormalBoluses != null && batchData.NormalBoluses.Length > 0)
                    {
                        await SaveDataByTypeAsync(
                            batchData.NormalBoluses,
                            "boluses",
                            ServiceName,
                            actualConfig,
                            _logger
                        );
                    }

                    if (batchData.ScheduledBasals != null && batchData.ScheduledBasals.Length > 0)
                    {
                        await SaveDataByTypeAsync(
                            batchData.ScheduledBasals,
                            "basals",
                            ServiceName,
                            actualConfig,
                            _logger
                        );
                    }

                    if (batchData.Readings != null && batchData.Readings.Length > 0)
                    {
                        await SaveDataByTypeAsync(
                            batchData.Readings,
                            "glucose",
                            ServiceName,
                            actualConfig,
                            _logger
                        );
                    }

                    // Also save the complete batch data
                    await SaveDataByTypeAsync(
                        new[] { batchData },
                        "batch",
                        ServiceName,
                        actualConfig,
                        _logger
                    );
                }

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
                } // Upload treatments to Nightscout
                var success = await UploadTreatmentsToNightscoutAsync(treatments, actualConfig);

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

            return $"https://{_config.GlookoServer}{endpoint}?patient={patientCode}&startDate={startDate:yyyy-MM-ddTHH:mm:ss.fffZ}&endDate={endDate:yyyy-MM-ddTHH:mm:ss.fffZ}&lastGuid={lastGuid}&lastUpdatedAt={startDate:yyyy-MM-ddTHH:mm:ss.fffZ}&limit={maxCount}";
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
                    if (responseBytes.Length >= 2 && responseBytes[0] == 0x1F && responseBytes[1] == 0x8B)
                    {
                        using var compressedStream = new MemoryStream(responseBytes);
                        using var gzipStream = new System.IO.Compression.GZipStream(compressedStream, System.IO.Compression.CompressionMode.Decompress);
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
                if (_config.GlookoTimezoneOffset != 0)
                {
                    date = date.AddHours(_config.GlookoTimezoneOffset);
                }

                var entry = new Entry
                {
                    Date = date,
                    Sgv = reading.Value,
                    Type = "sgv",
                    Device = "Glooko",
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
            CancellationToken cancellationToken = default
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
                    var sinceTimestamp = await CalculateSinceTimestampAsync(config);
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
                        _logger.LogInformation(
                            "Glooko health data published successfully via API"
                        );
                        return true;
                    }
                    else if (config.FallbackToDirectApi)
                    {
                        _logger.LogWarning(
                            "Health data publishing failed, falling back to direct API"
                        );
                        return await UploadToNightscoutAsync(glucoseEntries, config);
                    }
                }
                else
                {
                    // Use traditional sync method
                    var success = await SyncDataAsync(config, cancellationToken);
                    if (success)
                    {
                        _logger.LogInformation(
                            "Glooko health data sync completed successfully via direct API"
                        );
                    }
                    return success;
                }

                return false;
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
    }
}
