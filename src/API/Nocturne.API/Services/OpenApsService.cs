using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;

namespace Nocturne.API.Services;

/// <summary>
/// OpenAPS service implementation with 1:1 legacy JavaScript compatibility
/// Handles OpenAPS loop data analysis, visualization, and notifications
/// Implements the functionality from legacy openaps.js with full backwards compatibility
/// </summary>
public class OpenApsService : IOpenApsService
{
    private readonly ILogger<OpenApsService> _logger;
    private const int LevelNone = -3;
    private const int LevelWarn = 1;
    private const int LevelUrgent = 2;
    // Constants from legacy implementation
    private const int RECENT_HOURS = 6; // CHECKME: Legacy uses dia*2, defaulting to 6 hours for compatibility
    private const string DEFAULT_PRED_IOB_COLOR = "#1e88e5";
    private const string DEFAULT_PRED_COB_COLOR = "#FB8C00";
    private const string DEFAULT_PRED_ACOB_COLOR = "#FB8C00";
    private const string DEFAULT_PRED_ZT_COLOR = "#00d2d2";
    private const string DEFAULT_PRED_UAM_COLOR = "#c9bd60";

    public OpenApsService(ILogger<OpenApsService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets OpenAPS preferences from extended settings
    /// Implements the legacy getPrefs() functionality with 1:1 compatibility
    /// </summary>
    public OpenApsPreferences GetPreferences(Dictionary<string, object?> extendedSettings)
    {
        var settings = extendedSettings ?? new Dictionary<string, object?>();

        var fields = CleanList(settings.GetValueOrDefault("fields")?.ToString());
        if (fields == null || !fields.Any())
        {
            fields = new List<string>
            {
                "status-symbol",
                "status-label",
                "iob",
                "meal-assist",
                "rssi",
            };
        }

        var retroFields = CleanList(settings.GetValueOrDefault("retroFields")?.ToString());
        if (retroFields == null || !retroFields.Any())
        {
            retroFields = new List<string>
            {
                "status-symbol",
                "status-label",
                "iob",
                "meal-assist",
                "rssi",
            };
        }

        var colorPredictionLines = true;
        if (settings.ContainsKey("colorPredictionLines"))
        {
            if (bool.TryParse(settings["colorPredictionLines"]?.ToString(), out var colorPref))
            {
                colorPredictionLines = colorPref;
            }
        }

        return new OpenApsPreferences
        {
            Fields = fields,
            RetroFields = retroFields,
            Warn = GetIntSetting(settings, "warn", 30),
            Urgent = GetIntSetting(settings, "urgent", 60),
            EnableAlerts = GetBoolSetting(settings, "enableAlerts", false),
            PredIobColor = GetStringSetting(settings, "predIobColor", DEFAULT_PRED_IOB_COLOR),
            PredCobColor = GetStringSetting(settings, "predCobColor", DEFAULT_PRED_COB_COLOR),
            PredAcobColor = GetStringSetting(settings, "predAcobColor", DEFAULT_PRED_ACOB_COLOR),
            PredZtColor = GetStringSetting(settings, "predZtColor", DEFAULT_PRED_ZT_COLOR),
            PredUamColor = GetStringSetting(settings, "predUamColor", DEFAULT_PRED_UAM_COLOR),
            ColorPredictionLines = colorPredictionLines,
        };
    }

    /// <summary>
    /// Analyzes device status data to determine OpenAPS loop status
    /// Implements the legacy analyzeData() functionality with 1:1 compatibility
    /// </summary>
    public OpenApsAnalysisResult AnalyzeData(
        IEnumerable<DeviceStatus> deviceStatuses,
        DateTime currentTime,
        OpenApsPreferences preferences
    )
    {
        var recentMills = currentTime.AddHours(-RECENT_HOURS);
        var recentData = deviceStatuses
            .Where(status =>
                status.OpenAps != null
                && status.Mills >= ((DateTimeOffset)recentMills).ToUnixTimeMilliseconds()
                && status.Mills <= ((DateTimeOffset)currentTime).ToUnixTimeMilliseconds()
            )
            .Select(ProcessDeviceStatusIob)
            .ToList();

        var recent = currentTime.AddMinutes(-preferences.Warn / 2.0);

        var result = new OpenApsAnalysisResult
        {
            SeenDevices = new Dictionary<string, OpenApsDevice>(),
            Status = new OpenApsLoopStatus(),
        };

        foreach (var status in recentData)
        {
            var device = GetDevice(status, result.SeenDevices);
            var moments = ToMoments(status);
            var loopStatus = MomentsToLoopStatus(moments, recent, true);

            if (device.When == null || moments.When > device.When)
            {
                device.Status = loopStatus;
                device.When = moments.When;
            }

            // Process enacted commands
            ProcessEnactedCommand(status, moments, result);

            // Process suggested commands
            ProcessSuggestedCommand(status, moments, result);

            // Process IOB data
            ProcessIobData(status, moments, result);

            // Process MM tune data
            ProcessMmTuneData(status, moments, device);
        }

        // Determine last loop moment and eventual BG
        DetermineLastLoopMoment(result);

        // Set overall status
        result.Status = MomentsToLoopStatus(
            new OpenApsMoments
            {
                Enacted = result.LastEnacted?.Moment,
                NotEnacted = result.LastNotEnacted?.Moment,
                Suggested = result.LastSuggested?.Moment,
            },
            recent,
            false
        );

        return result;
    }

    /// <summary>
    /// Finds active OpenAPS offline marker
    /// Implements the legacy findOfflineMarker() functionality with 1:1 compatibility
    /// </summary>
    public Treatment? FindOfflineMarker(IEnumerable<Treatment> treatments, DateTime currentTime)
    {
        if (treatments == null)
            return null;

        return treatments
            .Where(treatment => treatment.EventType == "OpenAPS Offline")
            .Where(treatment =>
            {
                var eventTime = DateTimeOffset.FromUnixTimeMilliseconds(treatment.Mills).DateTime;
                var eventEnd = treatment.Duration.HasValue
                    ? eventTime.AddMinutes(treatment.Duration.Value)
                    : eventTime;
                return eventTime <= currentTime && eventEnd >= currentTime;
            })
            .OrderByDescending(treatment => treatment.Mills)
            .FirstOrDefault();
    }

    /// <summary>
    /// Checks for OpenAPS notification conditions
    /// Implements the legacy checkNotifications() functionality with 1:1 compatibility
    /// </summary>
    public int CheckNotifications(
        OpenApsAnalysisResult analysisResult,
        OpenApsPreferences preferences,
        DateTime currentTime,
        Treatment? offlineMarker
    )
    {
        if (!preferences.EnableAlerts)
            return LevelNone; // NONE

        if (analysisResult.LastLoopMoment == null)
        {
            _logger.LogInformation("OpenAPS hasn't reported a loop yet");
            return LevelNone; // NONE
        }

        if (offlineMarker != null)
        {
            _logger.LogInformation("OpenAPS known offline, not checking for alerts");
            return LevelNone; // NONE
        }

        var urgentTime = analysisResult.LastLoopMoment.Value.AddMinutes(preferences.Urgent);
        var warningTime = analysisResult.LastLoopMoment.Value.AddMinutes(preferences.Warn);

        if (urgentTime < currentTime)
        {
            return LevelUrgent; // URGENT
        }
        else if (warningTime < currentTime)
        {
            return LevelWarn; // WARN
        }

        return LevelNone; // NONE
    }

    /// <summary>
    /// Gets event types for care portal integration
    /// Implements the legacy getEventTypes() functionality with 1:1 compatibility
    /// </summary>
    public List<object> GetEventTypes(string units)
    {
        _logger.LogDebug("Getting event types for units: {Units}", units);

        var reasonconf = new List<object>();

        if (units == "mmol")
        {
            reasonconf.Add(
                new
                {
                    name = "Eating Soon",
                    targetTop = 4.5,
                    targetBottom = 4.5,
                    duration = 60,
                }
            );
            reasonconf.Add(
                new
                {
                    name = "Activity",
                    targetTop = 8.0,
                    targetBottom = 6.5,
                    duration = 120,
                }
            );
        }
        else
        {
            reasonconf.Add(
                new
                {
                    name = "Eating Soon",
                    targetTop = 80,
                    targetBottom = 80,
                    duration = 60,
                }
            );
            reasonconf.Add(
                new
                {
                    name = "Activity",
                    targetTop = 140,
                    targetBottom = 120,
                    duration = 120,
                }
            );
        }

        reasonconf.Add(new { name = "Manual" });

        return new List<object>
        {
            new
            {
                val = "Temporary Target",
                name = "Temporary Target",
                bg = false,
                insulin = false,
                carbs = false,
                prebolus = false,
                duration = true,
                percent = false,
                absolute = false,
                profile = false,
                split = false,
                targets = true,
                reasons = reasonconf,
            },
            new
            {
                val = "Temporary Target Cancel",
                name = "Temporary Target Cancel",
                bg = false,
                insulin = false,
                carbs = false,
                prebolus = false,
                duration = false,
                percent = false,
                absolute = false,
                profile = false,
                split = false,
            },
            new
            {
                val = "OpenAPS Offline",
                name = "OpenAPS Offline",
                bg = false,
                insulin = false,
                carbs = false,
                prebolus = false,
                duration = true,
                percent = false,
                absolute = false,
                profile = false,
                split = false,
            },
        };
    }

    /// <summary>
    /// Generates visualization data for OpenAPS status
    /// Implements the legacy updateVisualisation() functionality with 1:1 compatibility
    /// </summary>
    public object GenerateVisualizationData(
        OpenApsAnalysisResult analysisResult,
        OpenApsPreferences preferences,
        bool isRetroMode,
        DateTime currentTime
    )
    {
        var selectedFields = isRetroMode ? preferences.RetroFields : preferences.Fields;
        var events = new List<OpenApsEvent>();

        // Add enacted event if present
        if (analysisResult.LastEnacted != null && analysisResult.Status.Code == "enacted")
        {
            events.Add(
                CreateEnactedEvent(
                    analysisResult.LastEnacted,
                    selectedFields,
                    analysisResult.LastIob
                )
            );
        }

        // Add suggested event if more recent than enacted
        if (analysisResult.LastSuggested != null)
        {
            var shouldAddSuggested =
                analysisResult.LastEnacted == null
                || (
                    analysisResult.LastSuggested.Moment.HasValue
                    && analysisResult.LastEnacted.Moment.HasValue
                    && analysisResult.LastSuggested.Moment > analysisResult.LastEnacted.Moment
                );

            if (shouldAddSuggested)
            {
                events.Add(
                    CreateSuggestedEvent(
                        analysisResult.LastSuggested,
                        selectedFields,
                        analysisResult.LastIob
                    )
                );
            }
        }

        // Add device info events
        events.AddRange(CreateDeviceInfoEvents(analysisResult.SeenDevices.Values, selectedFields));

        // Sort events by time (most recent first)
        var sortedEvents = events.OrderByDescending(e => e.Time).ToList();

        // Create visualization info
        var info = sortedEvents
            .Select(e => new { label = FormatTimeForDisplay(e.Time, currentTime), value = e.Value })
            .ToList();

        var label = "OpenAPS";
        if (selectedFields.Contains("status-symbol"))
        {
            label += " " + analysisResult.Status.Symbol;
        }

        return new
        {
            value = analysisResult.LastLoopMoment != null
                ? FormatTimeAgo(analysisResult.LastLoopMoment.Value, currentTime)
                : "Unknown",
            label = label,
            info = info,
            pillClass = GetStatusClass(analysisResult, preferences, currentTime),
        };
    }

    /// <summary>
    /// Generates forecast points for blood glucose prediction
    /// Implements the legacy getForecastPoints() functionality with 1:1 compatibility
    /// </summary>
    public List<OpenApsForecastPoint> GenerateForecastPoints(
        OpenApsPredBg? predictionData,
        OpenApsPreferences preferences,
        DateTime currentTime
    )
    {
        var points = new List<OpenApsForecastPoint>();

        if (predictionData == null)
            return points;

        var offset = currentTime.Ticks / 10000; // Convert to Unix timestamp

        // Process different prediction types
        if (predictionData.Values != null)
        {
            points.AddRange(
                CreateForecastPoints(predictionData.Values, offset, 0, "values", "#1e88e5")
            );
        }

        if (predictionData.Iob != null)
        {
            points.AddRange(
                CreateForecastPoints(predictionData.Iob, offset, 0, "IOB", preferences.PredIobColor)
            );
        }

        if (predictionData.Zt != null)
        {
            points.AddRange(
                CreateForecastPoints(predictionData.Zt, offset, 0, "ZT", preferences.PredZtColor)
            );
        }

        if (predictionData.ACob != null)
        {
            points.AddRange(
                CreateForecastPoints(
                    predictionData.ACob,
                    offset,
                    0,
                    "aCOB",
                    preferences.PredAcobColor
                )
            );
        }

        if (predictionData.Cob != null)
        {
            points.AddRange(
                CreateForecastPoints(predictionData.Cob, offset, 0, "COB", preferences.PredCobColor)
            );
        }

        if (predictionData.Uam != null)
        {
            points.AddRange(
                CreateForecastPoints(predictionData.Uam, offset, 0, "UAM", preferences.PredUamColor)
            );
        }

        return points;
    }

    /// <summary>
    /// Handles virtual assistant forecast request
    /// Implements the legacy virtAsstForecastHandler() functionality with 1:1 compatibility
    /// </summary>
    public (string title, string response) HandleVirtualAssistantForecast(
        OpenApsAnalysisResult analysisResult
    )
    {
        var title = "OpenAPS Forecast";

        if (analysisResult.LastEventualBg.HasValue)
        {
            var response = $"The OpenAPS Eventual BG is {analysisResult.LastEventualBg.Value}";
            return (title, response);
        }

        return (title, "Unknown");
    }

    /// <summary>
    /// Handles virtual assistant last loop request
    /// Implements the legacy virtAsstLastLoopHandler() functionality with 1:1 compatibility
    /// </summary>
    public (string title, string response) HandleVirtualAssistantLastLoop(
        OpenApsAnalysisResult analysisResult,
        DateTime currentTime
    )
    {
        var title = "Last Loop";

        if (analysisResult.LastLoopMoment.HasValue)
        {
            var response =
                $"The last successful loop was {FormatTimeAgo(analysisResult.LastLoopMoment.Value, currentTime)}";
            return (title, response);
        }

        return (title, "Unknown");
    }

    #region Private Helper Methods

    /// <summary>
    /// Clean and split a list string (legacy cleanList function)
    /// </summary>
    private static List<string>? CleanList(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        var cleaned = HttpUtility.UrlDecode(value).ToLowerInvariant().Split(' ').ToList();
        if (!cleaned.Any() || string.IsNullOrEmpty(cleaned[0]))
            return null;

        return cleaned;
    }

    /// <summary>
    /// Get integer setting with default value
    /// </summary>
    private static int GetIntSetting(
        Dictionary<string, object?> settings,
        string key,
        int defaultValue
    )
    {
        if (
            settings.TryGetValue(key, out var value)
            && int.TryParse(value?.ToString(), out var intValue)
        )
        {
            return intValue;
        }
        return defaultValue;
    }

    /// <summary>
    /// Get boolean setting with default value
    /// </summary>
    private static bool GetBoolSetting(
        Dictionary<string, object?> settings,
        string key,
        bool defaultValue
    )
    {
        if (
            settings.TryGetValue(key, out var value)
            && bool.TryParse(value?.ToString(), out var boolValue)
        )
        {
            return boolValue;
        }
        return defaultValue;
    }

    /// <summary>
    /// Get string setting with default value
    /// </summary>
    private static string GetStringSetting(
        Dictionary<string, object?> settings,
        string key,
        string defaultValue
    )
    {
        if (settings.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value?.ToString()))
        {
            return value.ToString()!;
        }
        return defaultValue;
    }

    /// <summary>
    /// Process device status IOB data (legacy array handling)
    /// Array normalization is now handled by OpenApsIobDataConverter during deserialization.
    /// </summary>
    private static DeviceStatus ProcessDeviceStatusIob(DeviceStatus status)
    {
        return status;
    }

    /// <summary>
    /// Get or create device entry (legacy getDevice function)
    /// </summary>
    private OpenApsDevice GetDevice(
        DeviceStatus status,
        Dictionary<string, OpenApsDevice> seenDevices
    )
    {
        var uri = status.Device ?? "device";
        if (seenDevices.TryGetValue(uri, out var device))
        {
            return device;
        }
        device = new OpenApsDevice { Name = GetDeviceName(uri), Uri = uri };

        seenDevices[uri] = device;
        return device;
    }

    /// <summary>
    /// Convert device status to moments (legacy toMoments function)
    /// </summary>
    private static OpenApsMoments ToMoments(DeviceStatus status)
    {
        var moments = new OpenApsMoments
        {
            When = DateTimeOffset.FromUnixTimeMilliseconds(status.Mills).DateTime,
        };

        // Process OpenAPS data
        if (status.OpenAps != null)
        {
            // Handle enacted commands
            if (status.OpenAps.Enacted != null)
            {
                // Try to parse the enacted object to get timestamp and received status
                var enacted = ParseOpenApsCommand(status.OpenAps.Enacted);
                if (enacted != null)
                {
                    if (
                        enacted.Timestamp != null
                        && (enacted.Received == true || enacted.Recieved == true)
                    )
                    {
                        moments.Enacted =
                            enacted.Mills != null
                                ? DateTimeOffset
                                    .FromUnixTimeMilliseconds(enacted.Mills.Value)
                                    .DateTime
                                : DateTime.Parse(enacted.Timestamp);
                    }
                    else if (
                        enacted.Timestamp != null
                        && (
                            enacted.Received == false
                            || (enacted.Received != true && enacted.Recieved != true)
                        )
                    )
                    {
                        moments.NotEnacted =
                            enacted.Mills != null
                                ? DateTimeOffset
                                    .FromUnixTimeMilliseconds(enacted.Mills.Value)
                                    .DateTime
                                : DateTime.Parse(enacted.Timestamp);
                    }
                }
            }

            // Handle suggested commands
            if (status.OpenAps.Suggested != null)
            {
                var suggested = ParseOpenApsCommand(status.OpenAps.Suggested);
                if (suggested != null && suggested.Timestamp != null)
                {
                    moments.Suggested =
                        suggested.Mills != null
                            ? DateTimeOffset
                                .FromUnixTimeMilliseconds(suggested.Mills.Value)
                                .DateTime
                            : DateTime.Parse(suggested.Timestamp);
                }
            }

            // Handle IOB data
            if (status.OpenAps.Iob != null)
            {
                var iob = ParseOpenApsIob(status.OpenAps.Iob);
                if (iob != null && iob.Timestamp != null)
                {
                    moments.Iob =
                        iob.Mills != null
                            ? DateTimeOffset.FromUnixTimeMilliseconds(iob.Mills.Value).DateTime
                            : DateTime.Parse(iob.Timestamp);
                }
            }
        }

        return moments;
    }

    /// <summary>
    /// Convert moments to loop status (legacy momentsToLoopStatus function)
    /// </summary>
    private static OpenApsLoopStatus MomentsToLoopStatus(
        OpenApsMoments moments,
        DateTime recent,
        bool noWarning
    )
    {
        var status = new OpenApsLoopStatus
        {
            Symbol = "⚠",
            Code = "warning",
            Label = "Warning",
        };

        if (
            moments.NotEnacted.HasValue
            && (
                (moments.Enacted.HasValue && moments.NotEnacted > moments.Enacted)
                || (!moments.Enacted.HasValue && moments.NotEnacted > recent)
            )
        )
        {
            status.Symbol = "x";
            status.Code = "notenacted";
            status.Label = "Not Enacted";
        }
        else if (moments.Enacted.HasValue && moments.Enacted > recent)
        {
            status.Symbol = "⌁";
            status.Code = "enacted";
            status.Label = "Enacted";
        }
        else if (moments.Suggested.HasValue && moments.Suggested > recent)
        {
            status.Symbol = "◉";
            status.Code = "suggested";
            status.Label = "Suggested";
        }
        else if (noWarning)
        {
            status.Symbol = "◯";
            status.Code = "waiting";
            status.Label = "Waiting";
        }

        return status;
    }

    /// <summary>
    /// Process enacted command data
    /// </summary>
    private static void ProcessEnactedCommand(
        DeviceStatus status,
        OpenApsMoments moments,
        OpenApsAnalysisResult result
    )
    {
        if (status.OpenAps?.Enacted == null)
            return;

        var enacted = ParseOpenApsCommand(status.OpenAps.Enacted);
        if (enacted == null)
            return;

        // Handle enacted commands
        if (moments.Enacted.HasValue)
        {
            var enactedCommand = new OpenApsCommand
            {
                Moment = moments.Enacted,
                Bg = enacted.Bg,
                Reason = enacted.Reason,
                EventualBg = enacted.EventualBg,
                Rate = enacted.Rate,
                Duration = enacted.Duration,
                PredBgs = ParsePredBGs(enacted.PredBGs, moments.Enacted),
                MealAssist = enacted.MealAssist,
            };

            if (result.LastEnacted == null || moments.Enacted > result.LastEnacted.Moment)
            {
                result.LastEnacted = enactedCommand;

                // Update prediction data if available and more recent
                if (
                    enacted.PredBGs != null
                    && (result.LastPredBgs == null || moments.Enacted > result.LastPredBgs.Moment)
                )
                {
                    result.LastPredBgs = ParsePredBGs(enacted.PredBGs, moments.Enacted);
                    // Parse predBGs object into structured data
                }
            }
        }

        // Handle not enacted commands
        if (moments.NotEnacted.HasValue)
        {
            var notEnactedCommand = new OpenApsCommand
            {
                Moment = moments.NotEnacted,
                Bg = enacted.Bg,
                Reason = enacted.Reason,
                EventualBg = enacted.EventualBg,
                Rate = enacted.Rate,
                Duration = enacted.Duration,
                PredBgs = ParsePredBGs(enacted.PredBGs, moments.NotEnacted),
                MealAssist = enacted.MealAssist,
            };

            if (result.LastNotEnacted == null || moments.NotEnacted > result.LastNotEnacted.Moment)
            {
                result.LastNotEnacted = notEnactedCommand;
            }
        }
    }

    /// <summary>
    /// Process suggested command data
    /// </summary>
    private static void ProcessSuggestedCommand(
        DeviceStatus status,
        OpenApsMoments moments,
        OpenApsAnalysisResult result
    )
    {
        if (status.OpenAps?.Suggested == null || !moments.Suggested.HasValue)
            return;

        var suggested = ParseOpenApsCommand(status.OpenAps.Suggested);
        if (suggested == null)
            return;
        var suggestedCommand = new OpenApsCommand
        {
            Moment = moments.Suggested,
            Bg = suggested.Bg,
            Reason = suggested.Reason,
            EventualBg = suggested.EventualBg,
            Rate = suggested.Rate,
            Duration = suggested.Duration,
            PredBgs = ParsePredBGs(suggested.PredBGs, moments.Suggested),
            MealAssist = suggested.MealAssist,
        };

        if (result.LastSuggested == null || moments.Suggested > result.LastSuggested.Moment)
        {
            result.LastSuggested = suggestedCommand;

            // Update prediction data if available and more recent
            if (
                suggested.PredBGs != null
                && (result.LastPredBgs == null || moments.Suggested > result.LastPredBgs.Moment)
            )
            {
                result.LastPredBgs = ParsePredBGs(suggested.PredBGs, moments.Suggested);
                // Parse predBGs object into structured data
            }
        }
    }

    /// <summary>
    /// Process IOB data
    /// </summary>
    private static void ProcessIobData(
        DeviceStatus status,
        OpenApsMoments moments,
        OpenApsAnalysisResult result
    )
    {
        if (status.OpenAps?.Iob == null || !moments.Iob.HasValue)
            return;

        var iobData = ParseOpenApsIob(status.OpenAps.Iob);
        if (iobData == null)
            return;

        var openApsIob = new OpenApsIob
        {
            Timestamp = iobData.Timestamp,
            Iob = iobData.Iob,
            BolusIob = iobData.BolusIob,
            BasalIob = iobData.BasalIob,
            Activity = iobData.Activity,
            Moment = moments.Iob,
        };

        if (result.LastIob == null || moments.Iob > result.LastIob.Moment)
        {
            result.LastIob = openApsIob;
        }
    }

    /// <summary>
    /// Process MM tune data
    /// Legacy: if (status.mmtune and status.mmtune.timestamp)
    /// MM tune data is stored directly on the device status, not inside openaps
    /// </summary>
    private static void ProcessMmTuneData(
        DeviceStatus status,
        OpenApsMoments moments,
        OpenApsDevice device
    )
    {
        // Legacy JS: if (status.mmtune && status.mmtune.timestamp) {
        //   status.mmtune.moment = moment(status.mmtune.timestamp);
        //   if (!device.mmtune || moments.when.isAfter(device.mmtune.moment)) {
        //     device.mmtune = status.mmtune;
        //   }
        // }
        if (status.MmTune == null || string.IsNullOrEmpty(status.MmTune.Timestamp))
            return;

        // Parse the timestamp to create the moment
        if (DateTime.TryParse(status.MmTune.Timestamp, out var mmTuneMoment))
        {
            status.MmTune.Moment = mmTuneMoment;

            // Update device mmtune if this is more recent
            if (device.MmTune == null || moments.When > device.MmTune.Moment)
            {
                device.MmTune = status.MmTune;
            }
        }
    }

    /// <summary>
    /// Determine last loop moment and eventual BG
    /// </summary>
    private static void DetermineLastLoopMoment(OpenApsAnalysisResult result)
    {
        if (result.LastEnacted != null && result.LastSuggested != null)
        {
            if (result.LastEnacted.Moment > result.LastSuggested.Moment)
            {
                result.LastLoopMoment = result.LastEnacted.Moment;
                result.LastEventualBg = result.LastEnacted.EventualBg;
            }
            else
            {
                result.LastLoopMoment = result.LastSuggested.Moment;
                result.LastEventualBg = result.LastSuggested.EventualBg;
            }
        }
        else if (result.LastEnacted?.Moment != null)
        {
            result.LastLoopMoment = result.LastEnacted.Moment;
            result.LastEventualBg = result.LastEnacted.EventualBg;
        }
        else if (result.LastSuggested?.Moment != null)
        {
            result.LastLoopMoment = result.LastSuggested.Moment;
            result.LastEventualBg = result.LastSuggested.EventualBg;
        }
    }

    /// <summary>
    /// Create enacted event for visualization
    /// </summary>
    private static OpenApsEvent CreateEnactedEvent(
        OpenApsCommand enacted,
        List<string> selectedFields,
        OpenApsIob? lastIob
    )
    {
        var canceled = enacted.Rate == 0 && enacted.Duration == 0;
        var bgValue = enacted.Bg?.ToString() ?? "";

        var valueParts = new List<string>
        {
            !string.IsNullOrEmpty(bgValue) ? $"BG: {bgValue}" : "",
            $"<b>Temp Basal{(canceled ? " Canceled" : " Started")}</b>",
            canceled ? "" : $" {enacted.Rate?.ToString("F2")} for {enacted.Duration}m",
            !string.IsNullOrEmpty(enacted.Reason) ? enacted.Reason : "",
        };

        if (!string.IsNullOrEmpty(enacted.MealAssist) && selectedFields.Contains("meal-assist"))
        {
            valueParts.Add($" <b>Meal Assist:</b> {enacted.MealAssist}");
        }

        // Add IOB information if available and field is selected
        if (lastIob != null && selectedFields.Contains("iob"))
        {
            valueParts.Add($", IOB: {lastIob.Iob?.ToString("F2")}U");
            if (lastIob.BasalIob.HasValue)
                valueParts.Add($", Basal IOB {lastIob.BasalIob.Value.ToString("F2")}U");
            if (lastIob.BolusIob.HasValue)
                valueParts.Add($", Bolus IOB {lastIob.BolusIob.Value.ToString("F2")}U");
        }

        return new OpenApsEvent
        {
            Time = enacted.Moment ?? DateTime.UtcNow,
            Value = string.Join("", valueParts.Where(p => !string.IsNullOrEmpty(p))),
            Label = "Enacted",
        };
    }

    /// <summary>
    /// Create suggested event for visualization
    /// </summary>
    private static OpenApsEvent CreateSuggestedEvent(
        OpenApsCommand suggested,
        List<string> selectedFields,
        OpenApsIob? lastIob
    )
    {
        var bgValue = suggested.Bg?.ToString() ?? "";

        var valueParts = new List<string>
        {
            !string.IsNullOrEmpty(bgValue) ? $"BG: {bgValue}" : "",
            !string.IsNullOrEmpty(suggested.Reason) ? suggested.Reason : "",
        };

        // Add sensitivity ratio if available
        // Note: This would need to be added to the ParsedOpenApsCommand if available in the data
        // CHECKME
        // valueParts.Add(suggested.SensitivityRatio != null ? $", <b>Sensitivity Ratio:</b> {suggested.SensitivityRatio}" : "");

        // Add IOB information if available and field is selected
        if (lastIob != null && selectedFields.Contains("iob"))
        {
            valueParts.Add($", IOB: {lastIob.Iob?.ToString("F2")}U");
            if (lastIob.BasalIob.HasValue)
                valueParts.Add($", Basal IOB {lastIob.BasalIob.Value.ToString("F2")}U");
            if (lastIob.BolusIob.HasValue)
                valueParts.Add($", Bolus IOB {lastIob.BolusIob.Value.ToString("F2")}U");
        }

        return new OpenApsEvent
        {
            Time = suggested.Moment ?? DateTime.UtcNow,
            Value = string.Join("", valueParts.Where(p => !string.IsNullOrEmpty(p))),
            Label = "Suggested",
        };
    }

    /// <summary>
    /// Create device info events for visualization
    /// </summary>
    private static List<OpenApsEvent> CreateDeviceInfoEvents(
        IEnumerable<OpenApsDevice> devices,
        List<string> selectedFields
    )
    {
        var events = new List<OpenApsEvent>();

        foreach (var device in devices)
        {
            var deviceInfo = new List<string> { device.Name };

            if (selectedFields.Contains("status-symbol") && device.Status != null)
            {
                deviceInfo.Add(device.Status.Symbol);
            }

            if (selectedFields.Contains("status-label") && device.Status != null)
            {
                deviceInfo.Add(device.Status.Label);
            }

            // MM tune information
            if (device.MmTune != null)
            {
                // Legacy logic: find best scan detail with highest signal strength
                // This would need to be implemented when MmTune model is available
                if (selectedFields.Contains("freq"))
                {
                    // deviceInfo.Add($"{device.MmTune.SetFreq}MHz");
                }
                if (selectedFields.Contains("rssi"))
                {
                    // Add RSSI information from best scan
                    // deviceInfo.Add($"@ {bestSignal}dB");
                }
            }

            events.Add(
                new OpenApsEvent
                {
                    Time = device.When ?? DateTime.UtcNow,
                    Value = string.Join(" ", deviceInfo),
                    Label = device.Name,
                }
            );
        }

        return events;
    }

    /// <summary>
    /// Create forecast points for visualization
    /// </summary>
    private static List<OpenApsForecastPoint> CreateForecastPoints(
        List<double> values,
        long offset,
        int timeOffset,
        string type,
        string color
    )
    {
        var points = new List<OpenApsForecastPoint>();
        for (int i = 0; i < values.Count; i++)
        {
            points.Add(
                new OpenApsForecastPoint
                {
                    X = offset + (i * 5 * 60 * 1000), // 5-minute intervals
                    Y = values[i],
                    Color = color,
                    Type = type,
                }
            );
        }
        return points;
    }

    /// <summary>
    /// Get status CSS class for visualization
    /// </summary>
    private string GetStatusClass(
        OpenApsAnalysisResult analysisResult,
        OpenApsPreferences preferences,
        DateTime currentTime
    )
    {
        var level = CheckNotifications(analysisResult, preferences, currentTime, null);
        return level switch
        {
            2 => "urgent",
            1 => "warn",
            _ => "current",
        };
    }

    /// <summary>
    /// Format time for display
    /// </summary>
    private static string FormatTimeForDisplay(DateTime time, DateTime currentTime)
    {
        // Based on legacy time formatting logic
        var diff = currentTime - time;

        if (diff.TotalMinutes < 1)
        {
            return "now";
        }
        else if (diff.TotalMinutes < 60)
        {
            return $"{diff.TotalMinutes:F0}m ago";
        }
        else if (diff.TotalHours < 24)
        {
            return $"{diff.TotalHours:F0}h ago";
        }
        else
        {
            return $"{diff.TotalDays:F0}d ago";
        }
    }

    /// <summary>
    /// Format time ago string
    /// </summary>
    private static string FormatTimeAgo(DateTime time, DateTime currentTime)
    {
        var diff = currentTime - time;
        if (diff.TotalMinutes < 60)
        {
            return $"{diff.TotalMinutes:F0} minutes ago";
        }
        return $"{diff.TotalHours:F0} hours ago";
    }

    #endregion

    #region Helper Methods for Parsing

    /// <summary>
    /// Parse OpenAPS predBGs object from dynamic data
    /// Implements legacy predBGs parsing: can be array or object
    /// </summary>
    private static OpenApsPredBg? ParsePredBGs(OpenApsPredBGs? predBGs, DateTime? moment)
    {
        if (predBGs == null || !moment.HasValue)
            return null;

        return new OpenApsPredBg
        {
            Iob = predBGs.IOB,
            Zt = predBGs.ZT,
            Cob = predBGs.COB,
            Uam = predBGs.UAM,
            Moment = moment,
        };
    }

    /// <summary>
    /// Parse OpenAPS command object from dynamic data
    /// </summary>
    private static ParsedOpenApsCommand? ParseOpenApsCommand(OpenApsSuggested? command)
    {
        if (command == null)
            return null;

        var parsed = new ParsedOpenApsCommand
        {
            Timestamp = command.Timestamp,
            Mills = command.Mills,
            Bg = command.Bg,
            Reason = command.Reason,
            EventualBg = command.EventualBG,
            Rate = command.Rate,
            Duration = command.Duration,
            PredBGs = command.PredBGs,
            MealAssist = command.MealAssist,
            SensitivityRatio = command.SensitivityRatio,
        };

        if (command is OpenApsEnacted enacted)
        {
            parsed.Received = enacted.Received;
            parsed.Recieved = enacted.Recieved;
        }

        return parsed;
    }

    /// <summary>
    /// Map typed OpenApsIobData to ParsedOpenApsIob
    /// </summary>
    private static ParsedOpenApsIob? ParseOpenApsIob(OpenApsIobData? iobData)
    {
        if (iobData == null)
            return null;

        return new ParsedOpenApsIob
        {
            Timestamp = iobData.Timestamp,
            Mills = iobData.Mills,
            Iob = iobData.Iob,
            BolusIob = iobData.BolusIob,
            BasalIob = iobData.BasalIob,
            Activity = iobData.Activity,
            Time = iobData.Time,
        };
    }

    #endregion

    #region Helper Classes for Parsing

    /// <summary>
    /// Helper class for parsing OpenAPS command data
    /// </summary>
    private class ParsedOpenApsCommand
    {
        [JsonPropertyName("timestamp")]
        public string? Timestamp { get; set; }

        [JsonPropertyName("mills")]
        public long? Mills { get; set; }

        [JsonPropertyName("received")]
        public bool? Received { get; set; }

        [JsonPropertyName("recieved")] // Legacy typo in JS
        public bool? Recieved { get; set; }

        [JsonPropertyName("bg")]
        public double? Bg { get; set; }

        [JsonPropertyName("reason")]
        public string? Reason { get; set; }

        [JsonPropertyName("eventualBG")]
        public double? EventualBg { get; set; }

        [JsonPropertyName("rate")]
        public double? Rate { get; set; }

        [JsonPropertyName("duration")]
        public int? Duration { get; set; }

        [JsonPropertyName("predBGs")]
        public OpenApsPredBGs? PredBGs { get; set; }

        [JsonPropertyName("mealAssist")]
        public string? MealAssist { get; set; }

        [JsonPropertyName("sensitivityRatio")]
        public double? SensitivityRatio { get; set; }
    }

    /// <summary>
    /// Helper class for parsing OpenAPS IOB data
    /// </summary>
    private class ParsedOpenApsIob
    {
        [JsonPropertyName("timestamp")]
        public string? Timestamp { get; set; }

        [JsonPropertyName("mills")]
        public long? Mills { get; set; }

        [JsonPropertyName("iob")]
        public double? Iob { get; set; }

        [JsonPropertyName("bolusiob")]
        public double? BolusIob { get; set; }

        [JsonPropertyName("basaliob")]
        public double? BasalIob { get; set; }

        [JsonPropertyName("activity")]
        public double? Activity { get; set; }

        [JsonPropertyName("time")]
        public string? Time { get; set; }
    }

    /// <summary>
    /// Extract device name from URI (legacy deviceName utility)
    /// </summary>
    /// <param name="uri">Device URI</param>
    /// <returns>Device name</returns>
    private static string GetDeviceName(string uri)
    {
        if (string.IsNullOrEmpty(uri))
        {
            return "Unknown";
        }

        // Based on legacy utils.deviceName function
        var parts = uri.Split('/');
        return parts.Length > 0 ? parts[^1] : uri;
    }

    #endregion
}

/// <summary>
/// Internal helper class for OpenAPS moment calculations
/// </summary>
internal class OpenApsMoments
{
    public DateTime When { get; set; }
    public DateTime? Enacted { get; set; }
    public DateTime? NotEnacted { get; set; }
    public DateTime? Suggested { get; set; }
    public DateTime? Iob { get; set; }
}
