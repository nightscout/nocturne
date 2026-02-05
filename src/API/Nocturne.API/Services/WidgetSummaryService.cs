using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Widget;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Services;

/// <summary>
/// Service for aggregating widget-friendly summary data from multiple sources.
/// Provides essential diabetes management data optimized for mobile widgets, watch faces, and other constrained displays.
/// </summary>
public class WidgetSummaryService : IWidgetSummaryService
{
    private readonly IEntryService _entryService;
    private readonly IIobService _iobService;
    private readonly ICobService _cobService;
    private readonly ITreatmentService _treatmentService;
    private readonly IDeviceStatusService _deviceStatusService;
    private readonly TrackerRepository _trackerRepository;
    private readonly INotificationV1Service _notificationService;
    private readonly ILogger<WidgetSummaryService> _logger;

    /// <summary>
    /// Standard interval for CGM readings (5 minutes in milliseconds)
    /// </summary>
    private const long CgmIntervalMills = 5 * 60 * 1000;

    public WidgetSummaryService(
        IEntryService entryService,
        IIobService iobService,
        ICobService cobService,
        ITreatmentService treatmentService,
        IDeviceStatusService deviceStatusService,
        TrackerRepository trackerRepository,
        INotificationV1Service notificationService,
        ILogger<WidgetSummaryService> logger
    )
    {
        _entryService = entryService;
        _iobService = iobService;
        _cobService = cobService;
        _treatmentService = treatmentService;
        _deviceStatusService = deviceStatusService;
        _trackerRepository = trackerRepository;
        _notificationService = notificationService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<V4SummaryResponse> GetSummaryAsync(
        string userId,
        int hours = 0,
        bool includePredictions = false,
        CancellationToken cancellationToken = default
    )
    {
        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var response = new V4SummaryResponse
        {
            ServerMills = currentTime,
        };

        // Fetch data in parallel where possible
        var entryCount = hours > 0 ? (hours * 12) + 1 : 1; // 12 readings per hour (5-minute intervals)
        var entriesTask = _entryService.GetEntriesAsync(null, entryCount, 0, cancellationToken);
        var treatmentsTask = _treatmentService.GetTreatmentsAsync(100, 0, cancellationToken);
        var deviceStatusTask = _deviceStatusService.GetRecentDeviceStatusAsync(10, cancellationToken);
        var trackersTask = _trackerRepository.GetActiveInstancesAsync(userId, cancellationToken);

        await Task.WhenAll(entriesTask, treatmentsTask, deviceStatusTask, trackersTask);

        var entries = (await entriesTask).ToList();
        var treatments = (await treatmentsTask).ToList();
        var deviceStatusList = (await deviceStatusTask).ToList();
        var trackerInstances = await trackersTask;

        // Process glucose readings
        ProcessGlucoseReadings(response, entries, hours, currentTime);

        // Calculate IOB and COB
        CalculateIobCob(response, treatments, deviceStatusList);

        // Process tracker statuses
        ProcessTrackers(response, trackerInstances);

        // Get alarm state
        await ProcessAlarmStateAsync(response, cancellationToken);

        // Include predictions if requested
        if (includePredictions)
        {
            ProcessPredictions(response, deviceStatusList);
        }

        return response;
    }

    /// <summary>
    /// Process glucose readings into the widget format
    /// </summary>
    private void ProcessGlucoseReadings(
        V4SummaryResponse response,
        List<Entry> entries,
        int hours,
        long currentTime
    )
    {
        if (!entries.Any())
        {
            return;
        }

        // Entries are typically ordered newest first, so the first entry is current
        var currentEntry = entries.First();
        response.Current = MapEntryToGlucoseReading(currentEntry);

        // If hours > 0, include history (excluding current)
        if (hours > 0)
        {
            var cutoffMills = currentTime - (hours * 60 * 60 * 1000L);

            // Filter entries within the time range and exclude the current reading
            var historyEntries = entries
                .Where(e => e.Mills >= cutoffMills && e.Id != currentEntry.Id)
                .OrderBy(e => e.Mills) // Order oldest to newest for history
                .ToList();

            response.History = historyEntries.Select(MapEntryToGlucoseReading).ToList();
        }
    }

    /// <summary>
    /// Map an Entry to a V4GlucoseReading
    /// </summary>
    private static V4GlucoseReading MapEntryToGlucoseReading(Entry entry)
    {
        return new V4GlucoseReading
        {
            Sgv = entry.Sgv ?? entry.Mgdl,
            Direction = entry.DirectionEnum,
            TrendRate = entry.TrendRate,
            Delta = entry.Delta,
            Mills = entry.Mills,
            Noise = entry.Noise,
        };
    }

    /// <summary>
    /// Calculate IOB and COB values
    /// </summary>
    private void CalculateIobCob(
        V4SummaryResponse response,
        List<Treatment> treatments,
        List<DeviceStatus> deviceStatusList
    )
    {
        try
        {
            // Calculate IOB
            var iobResult = _iobService.CalculateTotal(treatments, deviceStatusList);
            response.Iob = Math.Round(iobResult.Iob * 100) / 100; // Round to 2 decimal places

            // Calculate COB
            var cobResult = _cobService.CobTotal(treatments, deviceStatusList);
            response.Cob = Math.Round(cobResult.Cob);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error calculating IOB/COB for widget summary");
            response.Iob = 0;
            response.Cob = 0;
        }
    }

    /// <summary>
    /// Process active tracker instances into widget format
    /// </summary>
    private void ProcessTrackers(
        V4SummaryResponse response,
        TrackerInstanceEntity[] trackerInstances
    )
    {
        var trackerStatuses = new List<V4TrackerStatus>();

        foreach (var instance in trackerInstances)
        {
            // Skip if definition is not loaded or dashboard visibility is Off
            if (instance.Definition == null)
            {
                continue;
            }

            var definition = instance.Definition;

            // Calculate current urgency based on thresholds
            var urgency = CalculateTrackerUrgency(instance, definition);

            // Check if tracker should be shown based on DashboardVisibility
            if (!ShouldShowTracker(definition.DashboardVisibility, urgency))
            {
                continue;
            }

            var status = new V4TrackerStatus
            {
                Id = instance.Id,
                DefinitionId = definition.Id,
                Name = definition.Name,
                Icon = definition.Icon,
                Category = definition.Category,
                Mode = definition.Mode,
                Urgency = urgency,
                LifespanHours = definition.LifespanHours,
            };

            // Set mode-specific fields
            if (definition.Mode == TrackerMode.Duration)
            {
                status.AgeHours = Math.Round(instance.AgeHours * 10) / 10; // Round to 1 decimal

                if (definition.LifespanHours.HasValue && definition.LifespanHours.Value > 0)
                {
                    status.PercentElapsed = Math.Round(
                        (instance.AgeHours / definition.LifespanHours.Value) * 100
                    );
                }
            }
            else if (definition.Mode == TrackerMode.Event && instance.ScheduledAt.HasValue)
            {
                var hoursUntil = (instance.ScheduledAt.Value - DateTime.UtcNow).TotalHours;
                status.HoursUntilEvent = Math.Round(hoursUntil * 10) / 10;
            }

            trackerStatuses.Add(status);
        }

        response.Trackers = trackerStatuses;
    }

    /// <summary>
    /// Calculate the current urgency level for a tracker instance based on notification thresholds
    /// </summary>
    private static NotificationUrgency CalculateTrackerUrgency(
        TrackerInstanceEntity instance,
        TrackerDefinitionEntity definition
    )
    {
        var thresholds = definition.NotificationThresholds
            .OrderByDescending(t => t.Urgency) // Higher urgency first
            .ToList();

        if (!thresholds.Any())
        {
            return NotificationUrgency.Info;
        }

        // For Duration mode, check against hours since start
        if (definition.Mode == TrackerMode.Duration)
        {
            var ageHours = instance.AgeHours;
            var lifespanHours = definition.LifespanHours ?? 0;

            foreach (var threshold in thresholds)
            {
                double effectiveThresholdHours;

                if (threshold.Hours < 0)
                {
                    // Relative threshold: negative hours means "X hours before end of lifespan"
                    effectiveThresholdHours = lifespanHours + threshold.Hours;
                }
                else
                {
                    // Absolute threshold: positive hours means "X hours after start"
                    effectiveThresholdHours = threshold.Hours;
                }

                if (ageHours >= effectiveThresholdHours)
                {
                    return threshold.Urgency;
                }
            }
        }
        // For Event mode, check against hours until scheduled time
        else if (definition.Mode == TrackerMode.Event && instance.ScheduledAt.HasValue)
        {
            var hoursUntilEvent = (instance.ScheduledAt.Value - DateTime.UtcNow).TotalHours;

            foreach (var threshold in thresholds)
            {
                // For events, negative hours = before event, positive hours = after event
                // Threshold hours represent: negative = X hours before event, positive = X hours after event
                if (threshold.Hours < 0)
                {
                    // Threshold triggers when we're within X hours of the event (approaching)
                    if (hoursUntilEvent <= Math.Abs(threshold.Hours) && hoursUntilEvent >= 0)
                    {
                        return threshold.Urgency;
                    }
                }
                else
                {
                    // Threshold triggers when we're X hours past the event (overdue)
                    if (hoursUntilEvent < 0 && Math.Abs(hoursUntilEvent) >= threshold.Hours)
                    {
                        return threshold.Urgency;
                    }
                }
            }
        }

        return NotificationUrgency.Info;
    }

    /// <summary>
    /// Determine if a tracker should be shown based on dashboard visibility settings
    /// </summary>
    private static bool ShouldShowTracker(DashboardVisibility visibility, NotificationUrgency currentUrgency)
    {
        return visibility switch
        {
            DashboardVisibility.Off => false,
            DashboardVisibility.Always => true,
            DashboardVisibility.Info => currentUrgency >= NotificationUrgency.Info,
            DashboardVisibility.Warn => currentUrgency >= NotificationUrgency.Warn,
            DashboardVisibility.Hazard => currentUrgency >= NotificationUrgency.Hazard,
            DashboardVisibility.Urgent => currentUrgency >= NotificationUrgency.Urgent,
            _ => true,
        };
    }

    /// <summary>
    /// Process alarm state from notification service
    /// </summary>
    private async Task ProcessAlarmStateAsync(
        V4SummaryResponse response,
        CancellationToken cancellationToken
    )
    {
        try
        {
            // Get admin notifications which may include active alarms
            var adminNotifies = await _notificationService.GetAdminNotifiesAsync(
                null, // No subject filter for alarms
                cancellationToken
            );

            if (adminNotifies?.Message?.Notifies?.Any() == true)
            {
                // Find the most recent/active notification
                // Note: AdminNotification doesn't have alarm-specific fields like Level/Group
                // This is a simplified version - for full alarm support, integrate with the alert system
                var activeNotification = adminNotifies.Message.Notifies
                    .OrderByDescending(n => n.LastRecorded)
                    .FirstOrDefault();

                if (activeNotification != null)
                {
                    // Create a basic alarm state from the admin notification
                    response.Alarm = new V4AlarmState
                    {
                        Level = 1, // Default level for admin notifications
                        Type = "admin",
                        Message = activeNotification.Title,
                        TriggeredMills = activeNotification.LastRecorded,
                        IsSilenced = false,
                        SilenceExpiresMills = null,
                    };
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error processing alarm state for widget summary");
            // Alarm state is optional, continue without it
        }
    }

    /// <summary>
    /// Process predictions from device status
    /// </summary>
    private void ProcessPredictions(
        V4SummaryResponse response,
        List<DeviceStatus> deviceStatusList
    )
    {
        // Find the most recent device status with predictions
        var statusWithPredictions = deviceStatusList
            .OrderByDescending(ds => ds.Mills)
            .FirstOrDefault(ds =>
                ds.Loop?.Predicted?.Values != null && ds.Loop.Predicted.Values.Length > 0
            );

        if (statusWithPredictions?.Loop?.Predicted != null)
        {
            var predicted = statusWithPredictions.Loop.Predicted;
            var startMills = ParsePredictionStartDate(predicted.StartDate, statusWithPredictions.Mills);

            response.Predictions = new V4Predictions
            {
                Values = predicted.Values?.ToList(),
                StartMills = startMills,
                IntervalMills = CgmIntervalMills, // Standard 5-minute intervals
                Source = "loop",
            };

            return;
        }

        // Fallback: check OpenAPS for predictions
        var statusWithOpenApsPredictions = deviceStatusList
            .OrderByDescending(ds => ds.Mills)
            .FirstOrDefault(ds =>
                ds.OpenAps?.Suggested != null || ds.OpenAps?.Enacted != null
            );

        if (statusWithOpenApsPredictions?.OpenAps != null)
        {
            var predictions = ExtractOpenApsPredictions(statusWithOpenApsPredictions);
            if (predictions != null)
            {
                response.Predictions = predictions;
            }
        }
    }

    /// <summary>
    /// Parse prediction start date from various formats
    /// </summary>
    private static long ParsePredictionStartDate(string? startDate, long fallbackMills)
    {
        if (string.IsNullOrEmpty(startDate))
        {
            return fallbackMills;
        }

        if (DateTime.TryParse(startDate, out var parsedDate))
        {
            return ((DateTimeOffset)DateTime.SpecifyKind(parsedDate, DateTimeKind.Utc))
                .ToUnixTimeMilliseconds();
        }

        return fallbackMills;
    }

    /// <summary>
    /// Extract predictions from OpenAPS device status
    /// </summary>
    private V4Predictions? ExtractOpenApsPredictions(DeviceStatus deviceStatus)
    {
        try
        {
            // OpenAPS predictions are typically in suggested or enacted objects
            // The structure can vary, so we handle it carefully
            var suggested = deviceStatus.OpenAps?.Suggested;
            var enacted = deviceStatus.OpenAps?.Enacted;

            // Try to extract predBGs from suggested or enacted
            // This is a simplified extraction - OpenAPS has complex prediction structures
            var predictedValues = ExtractPredBGsFromOpenAps(enacted) ??
                                  ExtractPredBGsFromOpenAps(suggested);

            if (predictedValues != null && predictedValues.Count > 0)
            {
                return new V4Predictions
                {
                    Values = predictedValues,
                    StartMills = deviceStatus.Mills,
                    IntervalMills = CgmIntervalMills,
                    Source = "openaps",
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error extracting OpenAPS predictions");
        }

        return null;
    }

    /// <summary>
    /// Extract predicted BG values from OpenAPS object
    /// </summary>
    private static List<double>? ExtractPredBGsFromOpenAps(object? openApsData)
    {
        if (openApsData == null)
        {
            return null;
        }

        try
        {
            // Try to access predBGs property using reflection
            var type = openApsData.GetType();
            var predBGsProperty = type.GetProperty("predBGs") ?? type.GetProperty("PredBGs");

            if (predBGsProperty != null)
            {
                var predBGsValue = predBGsProperty.GetValue(openApsData);
                if (predBGsValue is IEnumerable<double> values)
                {
                    return values.ToList();
                }
            }

            // Try IOB predictions as fallback
            var iobProperty = type.GetProperty("IOB") ?? type.GetProperty("iob");
            if (iobProperty != null)
            {
                var iobValue = iobProperty.GetValue(openApsData);
                if (iobValue is IEnumerable<double> iobValues)
                {
                    return iobValues.ToList();
                }
            }
        }
        catch
        {
            // Silently fail prediction extraction
        }

        return null;
    }
}
