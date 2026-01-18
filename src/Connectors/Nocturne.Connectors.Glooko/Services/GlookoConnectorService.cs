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
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Configurations;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Core.Constants;
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
        private readonly IRateLimitingStrategy _rateLimitingStrategy;
        private readonly IRetryDelayStrategy _retryDelayStrategy;
        private readonly IConnectorFileService<GlookoBatchData>? _fileService;
        private readonly GlookoAuthTokenProvider _tokenProvider;

        public override string ServiceName => "Glooko";
        public override string ConnectorSource => DataSources.GlookoConnector;

        public override List<SyncDataType> SupportedDataTypes =>
            [
                SyncDataType.Glucose,
                SyncDataType.Treatments,
                SyncDataType.Food,
                SyncDataType.Activity,
            ];

        public GlookoConnectorService(
            HttpClient httpClient,
            IOptions<GlookoConnectorConfiguration> config,
            ILogger<GlookoConnectorService> logger,
            IRetryDelayStrategy retryDelayStrategy,
            IRateLimitingStrategy rateLimitingStrategy,
            GlookoAuthTokenProvider tokenProvider,
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
            _tokenProvider =
                tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        }

        public override async Task<bool> AuthenticateAsync()
        {
            var token = await _tokenProvider.GetValidTokenAsync();
            if (token == null)
            {
                TrackFailedRequest("Failed to get valid token");
                return false;
            }

            TrackSuccessfulRequest();
            return true;
        }

        protected override async Task<SyncResult> PerformSyncInternalAsync(
            SyncRequest request,
            GlookoConnectorConfiguration config,
            CancellationToken cancellationToken
        )
        {
            var result = new SyncResult
            {
                Success = true,
                Message = "Sync completed successfully",
                StartTime = DateTime.UtcNow,
            };

            _stateService?.SetState(ConnectorState.Syncing, "Starting Glooko batch sync...");

            try
            {
                if (IsSessionExpired())
                {
                    if (!await AuthenticateAsync())
                    {
                        result.Success = false;
                        result.Message = "Authentication failed";
                        result.Errors.Add("Authentication failed");
                        _stateService?.SetState(ConnectorState.Error, "Authentication failed");
                        return result;
                    }
                }

                // Glooko fetches everything in one go, so determine the earliest 'From' date needed
                DateTime? from = request.From;

                var batchData = await FetchBatchDataAsync(from);

                if (batchData == null)
                {
                    result.Success = false;
                    result.Message = "Failed to fetch data";
                    result.Errors.Add("No data returned from Glooko");
                    _stateService?.SetState(ConnectorState.Error, "Failed to fetch data");
                    return result;
                }

                // 1. Process Glucose
                if (request.DataTypes.Contains(SyncDataType.Glucose))
                {
                    var entries = TransformBatchDataToEntries(batchData).ToList();
                    if (entries.Any())
                    {
                        var success = await PublishGlucoseDataInBatchesAsync(
                            entries,
                            config,
                            cancellationToken
                        );
                        if (success)
                        {
                            result.ItemsSynced[SyncDataType.Glucose] = entries.Count;
                            result.LastEntryTimes[SyncDataType.Glucose] = entries.Max(e => e.Date);
                        }
                    }
                }

                // 2. Process Treatments
                if (request.DataTypes.Contains(SyncDataType.Treatments))
                {
                    var treatments = TransformBatchDataToTreatments(batchData);

                    // V3 API Integration: Fetch additional data types not available in v2
                    if (_config.UseV3Api)
                    {
                        try
                        {
                            _logger.LogInformation(
                                "[{ConnectorSource}] Fetching additional data from v3 API...",
                                ConnectorSource
                            );
                            var v3Data = await FetchV3GraphDataAsync(from);
                            if (v3Data != null)
                            {
                                var v3Treatments = TransformV3ToTreatments(v3Data);
                                // Add v3 treatments that don't already exist (based on Id)
                                var existingIds = treatments.Select(t => t.Id).ToHashSet();
                                var newV3Treatments = v3Treatments
                                    .Where(t => !existingIds.Contains(t.Id))
                                    .ToList();
                                treatments.AddRange(newV3Treatments);
                                _logger.LogInformation(
                                    "[{ConnectorSource}] Added {Count} unique treatments from v3 API",
                                    ConnectorSource,
                                    newV3Treatments.Count
                                );

                                // Optionally add CGM backfill entries
                                if (
                                    _config.V3IncludeCgmBackfill
                                    && request.DataTypes.Contains(SyncDataType.Glucose)
                                )
                                {
                                    var v3Entries = TransformV3ToEntries(v3Data).ToList();
                                    if (v3Entries.Any())
                                    {
                                        await PublishGlucoseDataInBatchesAsync(
                                            v3Entries,
                                            config,
                                            cancellationToken
                                        );
                                        _logger.LogInformation(
                                            "[{ConnectorSource}] Published {Count} CGM backfill entries from v3",
                                            ConnectorSource,
                                            v3Entries.Count
                                        );
                                    }
                                }

                                // Transform and publish StateSpans (pump modes, connectivity, profiles)
                                var stateSpans = TransformV3ToStateSpans(v3Data);
                                if (stateSpans.Any())
                                {
                                    var stateSpanSuccess = await PublishStateSpanDataAsync(
                                        stateSpans,
                                        config,
                                        cancellationToken
                                    );
                                    if (stateSpanSuccess)
                                    {
                                        _logger.LogInformation(
                                            "[{ConnectorSource}] Published {Count} state spans from v3",
                                            ConnectorSource,
                                            stateSpans.Count
                                        );
                                    }
                                }

                                // Transform and publish SystemEvents (alarms, warnings)
                                var systemEvents = TransformV3ToSystemEvents(v3Data);
                                if (systemEvents.Any())
                                {
                                    var eventSuccess = await PublishSystemEventDataAsync(
                                        systemEvents,
                                        config,
                                        cancellationToken
                                    );
                                    if (eventSuccess)
                                    {
                                        _logger.LogInformation(
                                            "[{ConnectorSource}] Published {Count} system events from v3",
                                            ConnectorSource,
                                            systemEvents.Count
                                        );
                                    }
                                }
                            }
                        }
                        catch (Exception v3Ex)
                        {
                            _logger.LogWarning(
                                v3Ex,
                                "[{ConnectorSource}] V3 API fetch failed, continuing with v2 data only",
                                ConnectorSource
                            );
                        }
                    }

                    // Transform and publish V2 StateSpans (temp basals, suspend basals)
                    // These come from the V2 API which is always fetched
                    var v2StateSpans = TransformV2ToStateSpans(batchData);
                    if (v2StateSpans.Any())
                    {
                        var v2StateSpanSuccess = await PublishStateSpanDataAsync(
                            v2StateSpans,
                            config,
                            cancellationToken
                        );
                        if (v2StateSpanSuccess)
                        {
                            _logger.LogInformation(
                                "[{ConnectorSource}] Published {Count} state spans from v2",
                                ConnectorSource,
                                v2StateSpans.Count
                            );
                        }
                    }

                    if (treatments.Any())
                    {
                        var coreTreatments = treatments.Select(t => t.ToTreatment()).ToList();
                        var success = await PublishTreatmentDataInBatchesAsync(
                            coreTreatments,
                            config,
                            cancellationToken
                        );

                        if (success)
                        {
                            result.ItemsSynced[SyncDataType.Treatments] = coreTreatments.Count;

                            // Parse timestamp safely
                            var maxDateStr = treatments.Max(t => t.CreatedAt);
                            if (DateTime.TryParse(maxDateStr, out var maxDate))
                            {
                                result.LastEntryTimes[SyncDataType.Treatments] = maxDate;
                            }
                        }
                    }
                }

                result.EndTime = DateTime.UtcNow;
                _stateService?.SetState(ConnectorState.Idle, "Sync completed");
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during Glooko batch sync");
                result.Success = false;
                result.Message = "Sync failed with exception";
                result.Errors.Add(ex.Message);
                result.EndTime = DateTime.UtcNow;
                _stateService?.SetState(ConnectorState.Error, "Sync failed");
                return result;
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
                if (string.IsNullOrEmpty(_tokenProvider.SessionCookie))
                {
                    throw new InvalidOperationException(
                        "Not authenticated with Glooko. Call AuthenticateAsync first."
                    );
                }
                if (_tokenProvider.UserData?.UserLogin?.GlookoCode == null)
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
                                var insulinDate = GetRawGlookoDate(
                                    matchingInsulin.Timestamp,
                                    matchingInsulin.PumpTimestamp
                                );
                                treatment.EventType = "Meal Bolus";
                                var correctedInsulinTime = GetCorrectedGlookoTime(insulinDate)
                                    .ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                                treatment.CreatedAt = correctedInsulinTime;
                                treatment.EventTime = correctedInsulinTime;
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
                                var correctedFoodTime = GetCorrectedGlookoTime(foodDate)
                                    .ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                                treatment.CreatedAt = correctedFoodTime;
                                treatment.EventTime = correctedFoodTime;
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
                        var insulinDate = GetRawGlookoDate(
                            insulin.Timestamp,
                            insulin.PumpTimestamp
                        );

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
                                CreatedAt = GetCorrectedGlookoTime(insulinDate)
                                    .ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
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
                            CreatedAt = GetCorrectedGlookoTime(bolusDate)
                                .ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
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
                            CreatedAt = GetCorrectedGlookoTime(basalDate)
                                .ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
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

        private string ConstructGlookoUrl(string endpoint, DateTime startDate, DateTime endDate)
        {
            var patientCode = _tokenProvider.UserData?.UserLogin?.GlookoCode;

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
                request.Headers.TryAddWithoutValidation("Cookie", _tokenProvider.SessionCookie);
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
                // Skip insulin entries with 0 value - these are carb-only events
                if (insulin.Value <= 0)
                    return false;

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

        /// <summary>
        /// Normalize alarm type strings to standard values
        /// Handles various Glooko alarm type formats (different devices may use different naming)
        /// </summary>
        private string NormalizeAlarmType(string? alarmType)
        {
            if (string.IsNullOrWhiteSpace(alarmType))
                return "Unknown";

            // Normalize to lowercase and remove common prefixes/suffixes for comparison
            var normalized = alarmType.Trim().ToLowerInvariant();

            // Map various alarm types to standardized names
            return normalized switch
            {
                // Occlusion-related alarms (various naming conventions from different pumps)
                "occlusion" => "Occlusion",
                "occluded" => "Occlusion",
                "pump occlusion" => "Occlusion",
                "infusion set occlusion" => "Occlusion",
                "cartridge occlusion" => "Occlusion",
                "blockage" => "Occlusion",

                // Battery-related alarms
                "low battery" => "Low Battery",
                "battery low" => "Low Battery",
                "battery" => "Low Battery",

                // Reservoir-related alarms
                "low reservoir" => "Low Reservoir",
                "reservoir low" => "Low Reservoir",
                "empty reservoir" => "Empty Reservoir",

                // Infusion set alarms
                "infusion set change" => "Infusion Set Change",
                "set change" => "Infusion Set Change",

                // Sensor/CGM alarms
                "sensor" => "Sensor Error",
                "sensor error" => "Sensor Error",
                "cgm sensor" => "Sensor Error",

                // Communication alarms
                "no delivery" => "No Delivery",
                "delivery error" => "Delivery Error",
                "communication error" => "Communication Error",

                // General alarms
                "error" => "Device Error",
                "alarm" => "Device Alarm",

                // Return original (capitalized) if not in map
                _ => char.ToUpper(normalized[0]) + normalized.Substring(1),
            };
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

        /// <summary>
        /// Get corrected Glooko timestamp. Glooko reports local time as if it were UTC.
        /// </summary>
        private DateTime GetCorrectedGlookoTime(DateTime rawDate)
        {
            var offsetHours = _config.TimezoneOffset;
            var corrected = rawDate.AddHours(-offsetHours);
            _logger.LogDebug(
                "GetCorrectedGlookoTime: Raw={Raw}, ConfigOffset={ConfigOffset}, Result={Result}",
                rawDate,
                _config.TimezoneOffset,
                corrected
            );
            return corrected;
        }

        /// <summary>
        /// Get corrected Glooko timestamp from unix seconds.
        /// </summary>
        private DateTime GetCorrectedGlookoTime(long unixSeconds)
        {
            var rawUtc = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
            return GetCorrectedGlookoTime(rawUtc);
        }

        private bool IsSessionExpired()
        {
            return string.IsNullOrEmpty(_tokenProvider.SessionCookie);
        }

        private DateTime GetRawGlookoDate(string timestamp, string? pumpTimestamp)
        {
            return DateTime.Parse(
                !string.IsNullOrEmpty(pumpTimestamp) ? pumpTimestamp : timestamp,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind
            );
        }

        #region V3 API Methods

        private string? _meterUnits;

        /// <summary>
        /// Fetch user profile from v3 API to get meter units setting
        /// </summary>
        public async Task<GlookoV3UsersResponse?> FetchV3UserProfileAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(_tokenProvider.SessionCookie))
                {
                    throw new InvalidOperationException(
                        "Not authenticated with Glooko. Call AuthenticateAsync first."
                    );
                }

                var url = "/api/v3/session/users";
                _logger.LogDebug("Fetching Glooko v3 user profile from {Url}", url);

                var result = await FetchFromGlookoEndpoint(url);
                if (result.HasValue)
                {
                    var profile = JsonSerializer.Deserialize<GlookoV3UsersResponse>(
                        result.Value.GetRawText()
                    );
                    if (profile?.CurrentUser != null)
                    {
                        _meterUnits = profile.CurrentUser.MeterUnits;
                        _logger.LogInformation(
                            "[{ConnectorSource}] User profile loaded. MeterUnits: {Units}",
                            ConnectorSource,
                            _meterUnits
                        );
                    }
                    return profile;
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching Glooko v3 user profile");
                return null;
            }
        }

        /// <summary>
        /// Fetch data from v3 graph/data API - single call for all data types
        /// </summary>
        public async Task<GlookoV3GraphResponse?> FetchV3GraphDataAsync(DateTime? since = null)
        {
            try
            {
                if (string.IsNullOrEmpty(_tokenProvider.SessionCookie))
                {
                    throw new InvalidOperationException(
                        "Not authenticated with Glooko. Call AuthenticateAsync first."
                    );
                }

                if (_tokenProvider.UserData?.UserLogin?.GlookoCode == null)
                {
                    _logger.LogWarning("Missing Glooko user code, cannot fetch v3 data");
                    return null;
                }

                // Ensure we have meter units
                if (string.IsNullOrEmpty(_meterUnits))
                {
                    await FetchV3UserProfileAsync();
                }

                var fromDate = since ?? DateTime.UtcNow.AddDays(-1);
                var toDate = DateTime.UtcNow;

                var url = ConstructV3GraphUrl(fromDate, toDate);
                _logger.LogInformation(
                    "[{ConnectorSource}] Fetching v3 graph data from {StartDate:yyyy-MM-dd} to {EndDate:yyyy-MM-dd}",
                    ConnectorSource,
                    fromDate,
                    toDate
                );

                var result = await FetchFromGlookoEndpointWithRetry(url);
                if (result.HasValue)
                {
                    var graphData = JsonSerializer.Deserialize<GlookoV3GraphResponse>(
                        result.Value.GetRawText()
                    );

                    if (graphData?.Series != null)
                    {
                        _logger.LogInformation(
                            "[{ConnectorSource}] Fetched v3 graph data: "
                                + "AutomaticBolus={AutoBolus}, DeliveredBolus={Bolus}, "
                                + "PumpAlarm={Alarms}, ReservoirChange={Reservoir}, SetSiteChange={SetSite}, "
                                + "CgmReadings={Cgm}",
                            ConnectorSource,
                            graphData.Series.AutomaticBolus?.Length ?? 0,
                            graphData.Series.DeliveredBolus?.Length ?? 0,
                            graphData.Series.PumpAlarm?.Length ?? 0,
                            graphData.Series.ReservoirChange?.Length ?? 0,
                            graphData.Series.SetSiteChange?.Length ?? 0,
                            (graphData.Series.CgmHigh?.Length ?? 0)
                                + (graphData.Series.CgmNormal?.Length ?? 0)
                                + (graphData.Series.CgmLow?.Length ?? 0)
                        );
                    }

                    return graphData;
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching Glooko v3 graph data");
                return null;
            }
        }

        /// <summary>
        /// Construct URL for v3 graph/data endpoint with all requested series
        /// </summary>
        private string ConstructV3GraphUrl(DateTime startDate, DateTime endDate)
        {
            var patientCode = _tokenProvider.UserData?.UserLogin?.GlookoCode;

            // Series to request
            var series = new[]
            {
                "automaticBolus",
                "deliveredBolus",
                "injectionBolus",
                "pumpAlarm",
                "reservoirChange",
                "setSiteChange",
                "carbAll",
                "scheduledBasal",
                "temporaryBasal",
                "suspendBasal",
                "profileChange",
            };

            // Add CGM series if backfill is enabled
            if (_config.V3IncludeCgmBackfill)
            {
                series = series.Concat(new[] { "cgmHigh", "cgmNormal", "cgmLow" }).ToArray();
            }

            var seriesParams = string.Join("&", series.Select(s => $"series[]={s}"));

            return $"/api/v3/graph/data?patient={patientCode}"
                + $"&startDate={startDate:yyyy-MM-ddTHH:mm:ss.fffZ}"
                + $"&endDate={endDate:yyyy-MM-ddTHH:mm:ss.fffZ}"
                + $"&{seriesParams}"
                + "&locale=en&insulinTooltips=false&filterBgReadings=false&splitByDay=false";
        }

        /// <summary>
        /// Transform v3 graph data to Treatment objects
        /// </summary>
        public List<Treatment> TransformV3ToTreatments(GlookoV3GraphResponse graphData)
        {
            var treatments = new List<Treatment>();

            if (graphData?.Series == null)
                return treatments;

            var series = graphData.Series;

            // Process Automatic Boluses
            if (series.AutomaticBolus != null)
            {
                foreach (var bolus in series.AutomaticBolus)
                {
                    // Use raw timestamp for ID (matching v2 behavior) but corrected time for CreatedAt
                    var rawTimestamp = DateTimeOffset.FromUnixTimeSeconds(bolus.X).UtcDateTime;
                    var correctedTimestamp = GetCorrectedGlookoTime(bolus.X);
                    treatments.Add(
                        new Treatment
                        {
                            Id = GenerateTreatmentId(
                                "Automatic Bolus",
                                rawTimestamp,
                                $"insulin:{bolus.Y}"
                            ),
                            EventType = "Automatic Bolus",
                            CreatedAt = correctedTimestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                            Insulin = bolus.Y,
                            InsulinDelivered = bolus.Data?.DeliveredUnits ?? bolus.Y,
                            InsulinProgrammed = bolus.Data?.ProgrammedUnits,
                            InsulinRecommendationForCorrection = bolus.Data?.CorrectionUnits,
                            InsulinRecommendationForCarbs = bolus.Data?.FoodUnits,
                            BloodGlucoseInput = bolus.Data?.BgInput,
                            CalculationType = CalculationType.Automatic,
                            DataSource = ConnectorSource,
                            Notes = "AID automatic bolus",
                        }
                    );
                }
            }

            // NOTE: DeliveredBolus processing is intentionally skipped.
            // V2 API handles carb+insulin treatments (Meal Bolus, Correction Bolus) more capably.
            // The V3 DeliveredBolus series overlaps with V2 data and causes duplication issues.

            // Process Pump Alarms
            if (series.PumpAlarm != null)
            {
                foreach (var alarm in series.PumpAlarm)
                {
                    var rawTimestamp = DateTimeOffset.FromUnixTimeSeconds(alarm.X).UtcDateTime;
                    var correctedTimestamp = GetCorrectedGlookoTime(alarm.X);

                    // Normalize alarm type (handle various cases and formats)
                    var normalizedAlarmType = NormalizeAlarmType(alarm.AlarmType);

                    // Build descriptive notes from available fields
                    var alarmDescription =
                        alarm.Data?.AlarmDescription
                        ?? alarm.Label
                        ?? alarm.AlarmType
                        ?? "Unknown alarm";

                    treatments.Add(
                        new Treatment
                        {
                            Id = GenerateTreatmentId(
                                "Pump Alarm",
                                rawTimestamp,
                                $"type:{normalizedAlarmType}"
                            ),
                            EventType = "Pump Alarm",
                            CreatedAt = correctedTimestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                            Notes = $"[{normalizedAlarmType}] {alarmDescription}",
                            Reason = normalizedAlarmType, // Use standardized alarm type as reason for better filtering
                            DataSource = ConnectorSource,
                        }
                    );

                    _logger.LogInformation(
                        "[{ConnectorSource}] Imported pump alarm: type={AlarmType}, description={Description}, timestamp={Timestamp}",
                        ConnectorSource,
                        normalizedAlarmType,
                        alarmDescription,
                        correctedTimestamp
                    );
                }
            }

            // Process Reservoir Changes
            if (series.ReservoirChange != null)
            {
                foreach (var change in series.ReservoirChange)
                {
                    var rawTimestamp = DateTimeOffset.FromUnixTimeSeconds(change.X).UtcDateTime;
                    var correctedTimestamp = GetCorrectedGlookoTime(change.X);
                    treatments.Add(
                        new Treatment
                        {
                            Id = GenerateTreatmentId("Reservoir Change", rawTimestamp, null),
                            EventType = "Reservoir Change",
                            CreatedAt = correctedTimestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                            Notes = change.Label,
                            DataSource = ConnectorSource,
                        }
                    );
                }
            }

            // Process Set Site Changes
            if (series.SetSiteChange != null)
            {
                foreach (var change in series.SetSiteChange)
                {
                    var rawTimestamp = DateTimeOffset.FromUnixTimeSeconds(change.X).UtcDateTime;
                    var correctedTimestamp = GetCorrectedGlookoTime(change.X);
                    treatments.Add(
                        new Treatment
                        {
                            Id = GenerateTreatmentId("Site Change", rawTimestamp, null),
                            EventType = "Site Change",
                            CreatedAt = correctedTimestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                            Notes = change.Label,
                            DataSource = ConnectorSource,
                        }
                    );
                }
            }

            // NOTE: CarbAll processing is intentionally skipped.
            // V2 API handles carb treatments (Carb Correction, Meal Bolus) more capably.
            // The V3 CarbAll series overlaps with V2 data and causes duplication issues.

            // NOTE: Profile Changes are no longer created as treatments.
            // They are handled as StateSpans in TransformV3ToStateSpans().
            // See implementation_plan.md for migration details.

            _logger.LogInformation(
                "[{ConnectorSource}] Transformed {Count} treatments from v3 data",
                ConnectorSource,
                treatments.Count
            );

            return treatments;
        }

        /// <summary>
        /// Transform v3 CGM data to Entry objects (for optional backfill)
        /// </summary>
        public IEnumerable<Entry> TransformV3ToEntries(GlookoV3GraphResponse graphData)
        {
            var entries = new List<Entry>();

            if (graphData?.Series == null)
                return entries;

            var series = graphData.Series;
            var allCgm = (series.CgmHigh ?? Array.Empty<GlookoV3GlucoseDataPoint>())
                .Concat(series.CgmNormal ?? Array.Empty<GlookoV3GlucoseDataPoint>())
                .Concat(series.CgmLow ?? Array.Empty<GlookoV3GlucoseDataPoint>())
                .OrderBy(p => p.X);

            foreach (var reading in allCgm)
            {
                if (reading.Calculated)
                    continue; // Skip interpolated values

                var timestamp = GetCorrectedGlookoTime(reading.X);
                var sgvMgdl = ConvertToMgdl(reading.Y);

                entries.Add(
                    new Entry
                    {
                        Id = $"glooko_v3_{reading.X}",
                        Date = timestamp,
                        Sgv = (int)Math.Round(sgvMgdl),
                        Type = "sgv",
                        Device = ConnectorSource,
                        Direction = Direction.Flat.ToString(), // v3 doesn't provide trend
                    }
                );
            }

            _logger.LogInformation(
                "[{ConnectorSource}] Transformed {Count} CGM entries from v3 data",
                ConnectorSource,
                entries.Count
            );

            return entries;
        }

        /// <summary>
        /// Convert glucose value to mg/dL based on user's meter units setting
        /// </summary>
        private double ConvertToMgdl(double value)
        {
            if (_meterUnits?.ToLowerInvariant() == "mmol")
            {
                return value * 18.0182;
            }
            return value;
        }

        /// <summary>
        /// Transform v3 graph data to StateSpan objects for pump modes and connectivity
        /// </summary>
        public List<StateSpan> TransformV3ToStateSpans(GlookoV3GraphResponse graphData)
        {
            var stateSpans = new List<StateSpan>();

            if (graphData?.Series == null)
                return stateSpans;

            var series = graphData.Series;

            // Process Suspend Basals as pump mode state spans
            if (series.SuspendBasal != null)
            {
                foreach (var suspend in series.SuspendBasal)
                {
                    var startTimestamp = GetCorrectedGlookoTime(suspend.X);
                    var durationSeconds = suspend.Duration ?? 0;
                    var endMills =
                        durationSeconds > 0
                            ? new DateTimeOffset(startTimestamp).ToUnixTimeMilliseconds()
                                + (durationSeconds * 1000)
                            : (long?)null;

                    stateSpans.Add(
                        new StateSpan
                        {
                            OriginalId = $"glooko_suspend_{suspend.X}",
                            Category = StateSpanCategory.PumpMode,
                            State = PumpModeState.Suspended.ToString(),
                            StartMills = new DateTimeOffset(
                                startTimestamp
                            ).ToUnixTimeMilliseconds(),
                            EndMills = endMills,
                            Source = ConnectorSource,
                            Metadata = new Dictionary<string, object>
                            {
                                { "label", suspend.Label ?? "Suspended" },
                                { "durationSeconds", durationSeconds },
                            },
                        }
                    );
                }
            }

            // Process LGS/PLGS events as pump mode state spans
            if (series.LgsPlgs != null)
            {
                foreach (var lgsEvent in series.LgsPlgs)
                {
                    var startTimestamp = GetCorrectedGlookoTime(lgsEvent.X);
                    var durationSeconds = lgsEvent.Duration ?? 0;
                    var endMills =
                        durationSeconds > 0
                            ? new DateTimeOffset(startTimestamp).ToUnixTimeMilliseconds()
                                + (durationSeconds * 1000)
                            : (long?)null;

                    // Map LGS/PLGS event types to pump mode states
                    var stateValue = lgsEvent.EventType?.ToUpperInvariant() switch
                    {
                        "LGS" => PumpModeState.Limited.ToString(),
                        "PLGS" => PumpModeState.Limited.ToString(),
                        "SUSPEND" => PumpModeState.Suspended.ToString(),
                        _ => PumpModeState.Limited.ToString(),
                    };

                    stateSpans.Add(
                        new StateSpan
                        {
                            OriginalId = $"glooko_lgsplgs_{lgsEvent.X}",
                            Category = StateSpanCategory.PumpMode,
                            State = stateValue,
                            StartMills = new DateTimeOffset(
                                startTimestamp
                            ).ToUnixTimeMilliseconds(),
                            EndMills = endMills,
                            Source = ConnectorSource,
                            Metadata = new Dictionary<string, object>
                            {
                                { "label", lgsEvent.Label ?? lgsEvent.EventType ?? "LGS/PLGS" },
                                { "eventType", lgsEvent.EventType ?? "unknown" },
                                { "durationSeconds", durationSeconds },
                            },
                        }
                    );
                }
            }

            // Process Profile Changes as profile state spans
            if (series.ProfileChange != null)
            {
                var profileChanges = series.ProfileChange.OrderBy(p => p.X).ToList();
                for (int i = 0; i < profileChanges.Count; i++)
                {
                    var change = profileChanges[i];
                    var startTimestamp = GetCorrectedGlookoTime(change.X);

                    // End time is when the next profile change occurs, or null if this is the last one
                    long? endMills = null;
                    if (i < profileChanges.Count - 1)
                    {
                        endMills = new DateTimeOffset(
                            GetCorrectedGlookoTime(profileChanges[i + 1].X)
                        ).ToUnixTimeMilliseconds();
                    }

                    stateSpans.Add(
                        new StateSpan
                        {
                            OriginalId = $"glooko_profile_{change.X}",
                            Category = StateSpanCategory.Profile,
                            State = ProfileState.Active.ToString(),
                            StartMills = new DateTimeOffset(
                                startTimestamp
                            ).ToUnixTimeMilliseconds(),
                            EndMills = endMills,
                            Source = ConnectorSource,
                            Metadata = new Dictionary<string, object>
                            {
                                { "profileName", change.ProfileName ?? change.Label ?? "Unknown" },
                            },
                        }
                    );
                }
            }

            // Process Temporary Basals as TempBasal state spans
            if (series.TemporaryBasal != null)
            {
                foreach (var tempBasal in series.TemporaryBasal)
                {
                    var startTimestamp = GetCorrectedGlookoTime(tempBasal.X);
                    var durationSeconds = tempBasal.Duration ?? 0;
                    var endMills =
                        durationSeconds > 0
                            ? new DateTimeOffset(startTimestamp).ToUnixTimeMilliseconds()
                                + (durationSeconds * 1000)
                            : (long?)null;

                    // Rate is stored in Y field (U/hr)
                    var rate = tempBasal.Y ?? 0;

                    // Calculate total insulin delivered: rate (U/hr) × duration (seconds) / 3600 (seconds/hr)
                    var calculatedInsulin = rate * durationSeconds / 3600.0;

                    stateSpans.Add(
                        new StateSpan
                        {
                            OriginalId = $"glooko_tempbasal_{tempBasal.X}",
                            Category = StateSpanCategory.TempBasal,
                            State = TempBasalState.Active.ToString(),
                            StartMills = new DateTimeOffset(
                                startTimestamp
                            ).ToUnixTimeMilliseconds(),
                            EndMills = endMills,
                            Source = ConnectorSource,
                            Metadata = new Dictionary<string, object>
                            {
                                { "rate", rate },
                                { "durationSeconds", durationSeconds },
                                { "durationMinutes", durationSeconds / 60.0 },
                                { "calculatedInsulin", calculatedInsulin },
                                { "label", tempBasal.Label ?? "Temp Basal" },
                            },
                        }
                    );
                }
            }

            _logger.LogInformation(
                "[{ConnectorSource}] Transformed {Count} state spans from v3 data",
                ConnectorSource,
                stateSpans.Count
            );

            return stateSpans;
        }

        /// <summary>
        /// Transform v2 batch data to StateSpan objects for temporary basals and suspend basals
        /// </summary>
        public List<StateSpan> TransformV2ToStateSpans(GlookoBatchData batchData)
        {
            var stateSpans = new List<StateSpan>();

            if (batchData == null)
                return stateSpans;

            // Process Temporary Basals as TempBasal state spans
            if (batchData.TempBasals != null)
            {
                foreach (var tempBasal in batchData.TempBasals)
                {
                    var rawTimestamp = DateTime.Parse(
                        tempBasal.Timestamp,
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind
                    );
                    var startTimestamp = GetCorrectedGlookoTime(rawTimestamp);
                    var durationSeconds = tempBasal.Duration;
                    var endMills =
                        durationSeconds > 0
                            ? new DateTimeOffset(startTimestamp).ToUnixTimeMilliseconds()
                                + (durationSeconds * 1000)
                            : (long?)null;

                    // Rate is U/hr
                    var rate = tempBasal.Rate;

                    // Calculate total insulin delivered: rate (U/hr) × duration (seconds) / 3600 (seconds/hr)
                    var calculatedInsulin = rate * durationSeconds / 3600.0;

                    stateSpans.Add(
                        new StateSpan
                        {
                            OriginalId = $"glooko_v2_tempbasal_{rawTimestamp.Ticks}",
                            Category = StateSpanCategory.TempBasal,
                            State = TempBasalState.Active.ToString(),
                            StartMills = new DateTimeOffset(
                                startTimestamp
                            ).ToUnixTimeMilliseconds(),
                            EndMills = endMills,
                            Source = ConnectorSource,
                            Metadata = new Dictionary<string, object>
                            {
                                { "rate", rate },
                                { "durationSeconds", durationSeconds },
                                { "durationMinutes", durationSeconds / 60.0 },
                                { "calculatedInsulin", calculatedInsulin },
                                { "percent", tempBasal.Percent ?? 100 },
                                { "tempBasalType", tempBasal.TempBasalType ?? "unknown" },
                            },
                        }
                    );
                }
            }

            // Process Suspend Basals as PumpMode state spans
            if (batchData.SuspendBasals != null)
            {
                foreach (var suspend in batchData.SuspendBasals)
                {
                    var rawTimestamp = DateTime.Parse(
                        suspend.Timestamp,
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind
                    );
                    var startTimestamp = GetCorrectedGlookoTime(rawTimestamp);
                    var durationSeconds = suspend.Duration;
                    var endMills =
                        durationSeconds > 0
                            ? new DateTimeOffset(startTimestamp).ToUnixTimeMilliseconds()
                                + (durationSeconds * 1000)
                            : (long?)null;

                    stateSpans.Add(
                        new StateSpan
                        {
                            OriginalId = $"glooko_v2_suspend_{rawTimestamp.Ticks}",
                            Category = StateSpanCategory.PumpMode,
                            State = PumpModeState.Suspended.ToString(),
                            StartMills = new DateTimeOffset(
                                startTimestamp
                            ).ToUnixTimeMilliseconds(),
                            EndMills = endMills,
                            Source = ConnectorSource,
                            Metadata = new Dictionary<string, object>
                            {
                                { "suspendReason", suspend.SuspendReason ?? "unknown" },
                                { "durationSeconds", durationSeconds },
                            },
                        }
                    );
                }
            }

            _logger.LogInformation(
                "[{ConnectorSource}] Transformed {Count} state spans from v2 data (TempBasals={TempBasalCount}, Suspends={SuspendCount})",
                ConnectorSource,
                stateSpans.Count,
                batchData.TempBasals?.Length ?? 0,
                batchData.SuspendBasals?.Length ?? 0
            );

            return stateSpans;
        }

        /// <summary>
        /// Transform v3 graph data to SystemEvent objects for alarms and warnings
        /// </summary>
        public List<SystemEvent> TransformV3ToSystemEvents(GlookoV3GraphResponse graphData)
        {
            var events = new List<SystemEvent>();

            if (graphData?.Series == null)
                return events;

            var series = graphData.Series;

            // Process Pump Alarms as system events
            if (series.PumpAlarm != null)
            {
                foreach (var alarm in series.PumpAlarm)
                {
                    var timestamp = GetCorrectedGlookoTime(alarm.X);

                    // Determine event type based on alarm type/code
                    var eventType = DetermineAlarmEventType(alarm.AlarmType, alarm.Data?.AlarmCode);

                    events.Add(
                        new SystemEvent
                        {
                            OriginalId = $"glooko_alarm_{alarm.X}",
                            EventType = eventType,
                            Category = SystemEventCategory.Pump,
                            Code = alarm.Data?.AlarmCode ?? alarm.AlarmType,
                            Description =
                                alarm.Data?.AlarmDescription
                                ?? alarm.Label
                                ?? alarm.AlarmType
                                ?? "Unknown alarm",
                            Mills = new DateTimeOffset(timestamp).ToUnixTimeMilliseconds(),
                            Source = ConnectorSource,
                            Metadata = new Dictionary<string, object>
                            {
                                { "alarmType", alarm.AlarmType ?? "unknown" },
                                { "label", alarm.Label ?? "" },
                            },
                        }
                    );
                }
            }

            _logger.LogInformation(
                "[{ConnectorSource}] Transformed {Count} system events from v3 data",
                ConnectorSource,
                events.Count
            );

            return events;
        }

        /// <summary>
        /// Determine SystemEventType based on alarm type and code
        /// </summary>
        private static SystemEventType DetermineAlarmEventType(string? alarmType, string? alarmCode)
        {
            var type = (alarmType ?? "").ToUpperInvariant();
            var code = (alarmCode ?? "").ToUpperInvariant();

            // Critical/hazard keywords
            if (
                type.Contains("OCCLUSION")
                || type.Contains("EMPTY")
                || code.Contains("OCCLUSION")
                || code.Contains("EMPTY")
            )
            {
                return SystemEventType.Alarm;
            }

            // Warning keywords
            if (
                type.Contains("LOW")
                || type.Contains("BATTERY")
                || code.Contains("LOW")
                || code.Contains("BATTERY")
            )
            {
                return SystemEventType.Warning;
            }

            // Info keywords
            if (type.Contains("INFO") || type.Contains("REMINDER"))
            {
                return SystemEventType.Info;
            }

            // Default to warning for unknown alarms
            return SystemEventType.Warning;
        }

        #endregion
    }
}
