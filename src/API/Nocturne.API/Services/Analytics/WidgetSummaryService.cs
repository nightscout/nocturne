using System.Text.Json;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Models.Widget;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Entries;

namespace Nocturne.API.Services.Analytics;

/// <summary>
/// Aggregates widget-friendly summary data from multiple sources (CGM entries, IOB, COB, treatments,
/// APS predictions, tracker state, and alarm state) for mobile widgets, watch faces, and other
/// constrained displays where response size and latency are critical.
/// </summary>
/// <seealso cref="IWidgetSummaryService"/>
public class WidgetSummaryService : IWidgetSummaryService
{
    private readonly IEntryService _entryService;
    private readonly IIobCobReader _iobCobReader;
    private readonly IApsSnapshotRepository _apsSnapshots;
    private readonly ITrackerRepository _trackerRepository;
    private readonly INotificationV1Service _notificationService;
    private readonly IGlucoseStatusClassifier _statusClassifier;
    private readonly IAlertRepository _alertRepository;
    private readonly ICategoryReadContext _categoryReadContext;
    private readonly TimeProvider _time;
    private readonly ILogger<WidgetSummaryService> _logger;

    /// <summary>
    /// Standard interval for CGM readings (5 minutes in milliseconds)
    /// </summary>
    private const long CgmIntervalMills = 5 * 60 * 1000;

    public WidgetSummaryService(
        IEntryService entryService,
        IIobCobReader iobCobReader,
        IApsSnapshotRepository apsSnapshots,
        ITrackerRepository trackerRepository,
        INotificationV1Service notificationService,
        IGlucoseStatusClassifier statusClassifier,
        IAlertRepository alertRepository,
        ICategoryReadContext categoryReadContext,
        TimeProvider timeProvider,
        ILogger<WidgetSummaryService> logger
    )
    {
        _time = timeProvider;
        _entryService = entryService;
        _iobCobReader = iobCobReader;
        _apsSnapshots = apsSnapshots;
        _trackerRepository = trackerRepository;
        _notificationService = notificationService;
        _statusClassifier = statusClassifier;
        _alertRepository = alertRepository;
        _categoryReadContext = categoryReadContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<V4SummaryResponse> GetSummaryAsync(
        int hours = 0,
        bool includePredictions = false,
        CancellationToken cancellationToken = default
    )
    {
        var currentTime = _time.GetUtcNow().ToUnixTimeMilliseconds();
        var response = new V4SummaryResponse
        {
            ServerMills = currentTime,
        };

        // Fetch data sequentially (EF Core DbContext is not thread-safe)
        var entryCount = hours > 0 ? (hours * 12) + 1 : 1; // 12 readings per hour (5-minute intervals)
        // Meter and calibration entries are not CGM readings, so they never become the current reading.
        var entries = (await _entryService.GetEntriesAsync("sgv", entryCount, 0, cancellationToken)).ToList();
        var trackerInstances = await _trackerRepository.GetActiveInstancesAsync(cancellationToken);

        // Process glucose readings
        ProcessGlucoseReadings(response, entries, hours, currentTime);
        await ClassifyCurrentReadingAsync(response, currentTime, cancellationToken);

        // Calculate IOB and COB using v4 types
        await CalculateIobCobAsync(response, cancellationToken);

        // Process tracker statuses
        ProcessTrackers(response, trackerInstances);

        // Get alarm state
        await ProcessAlarmStateAsync(response, cancellationToken);

        // Include predictions if requested
        if (includePredictions)
        {
            await ProcessPredictionsAsync(response, cancellationToken);
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

    private async Task ClassifyCurrentReadingAsync(
        V4SummaryResponse response,
        long currentTime,
        CancellationToken cancellationToken
    )
    {
        if (response.Current is not { } current)
        {
            return;
        }

        try
        {
            var thresholds = _categoryReadContext.IsShare
                ? _statusClassifier.Defaults
                : _statusClassifier.ResolveThresholds(
                    await _alertRepository.GetEnabledRulesAsync(cancellationToken));
            current.Status = _statusClassifier.Classify(
                current.Sgv,
                DateTimeOffset.FromUnixTimeMilliseconds(current.Mills).UtcDateTime,
                lastReadingAt: null,
                thresholds,
                DateTimeOffset.FromUnixTimeMilliseconds(currentTime).UtcDateTime);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Error classifying current glucose for widget summary");
        }
    }

    /// <summary>
    /// Map an Entry to a V4GlucoseReading
    /// </summary>
    private static V4GlucoseReading MapEntryToGlucoseReading(Entry entry)
    {
        return new V4GlucoseReading
        {
            Sgv = entry.GlucoseMgdl(),
            Direction = entry.DirectionEnum,
            TrendRate = entry.TrendRate,
            Delta = entry.Delta,
            Mills = entry.Mills,
            Noise = entry.Noise,
        };
    }

    /// <summary>
    /// Calculate IOB and COB values from v4 repositories
    /// </summary>
    private async Task CalculateIobCobAsync(
        V4SummaryResponse response,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var now = _time.GetUtcNow().ToUnixTimeMilliseconds();

            var reading = await _iobCobReader.ReadTotalAsync(now, live: true, cancellationToken);
            response.Iob = Math.Round(reading.Iob.Iob * 100) / 100;
            response.Cob = reading.Cob is null ? 0 : Math.Round(reading.Cob.Cob);
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
                var hoursUntil = (instance.ScheduledAt.Value - _time.GetUtcNow().UtcDateTime).TotalHours;
                status.HoursUntilEvent = Math.Round(hoursUntil * 10) / 10;
            }

            trackerStatuses.Add(status);
        }

        response.Trackers = trackerStatuses;
    }

    /// <summary>
    /// Calculate the current urgency level for a tracker instance based on notification thresholds
    /// </summary>
    private NotificationUrgency CalculateTrackerUrgency(
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
            var hoursUntilEvent = (instance.ScheduledAt.Value - _time.GetUtcNow().UtcDateTime).TotalHours;

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
    /// Extract predictions from the most recent APS snapshot
    /// </summary>
    private async Task ProcessPredictionsAsync(
        V4SummaryResponse response,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var snapshots = await _apsSnapshots.GetAsync(
                from: null,
                to: null,
                device: null,
                source: null,
                limit: 1,
                offset: 0,
                descending: true,
                ct: cancellationToken);

            var latest = snapshots.FirstOrDefault();
            if (latest == null)
            {
                return;
            }

            if (latest.AidAlgorithm == AidAlgorithm.Loop)
            {
                ExtractLoopPredictions(response, latest);
            }
            else
            {
                ExtractOpenApsPredictions(response, latest);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error processing predictions for widget summary");
        }
    }

    /// <summary>
    /// Extract predictions from a Loop APS snapshot
    /// </summary>
    private static void ExtractLoopPredictions(V4SummaryResponse response, ApsSnapshot snapshot)
    {
        var values = DeserializeCurve(snapshot.PredictedDefaultJson);
        if (values == null || values.Count == 0)
        {
            return;
        }

        var startMills = snapshot.PredictedStartMills
            ?? snapshot.Timestamp.ToMills();

        response.Predictions = new V4Predictions
        {
            Values = values,
            StartMills = startMills,
            IntervalMills = CgmIntervalMills,
            Source = "loop",
        };
    }

    /// <summary>
    /// Extract predictions from an OpenAPS/AAPS/Trio APS snapshot
    /// </summary>
    private static void ExtractOpenApsPredictions(V4SummaryResponse response, ApsSnapshot snapshot)
    {
        var values = DeserializeCurve(snapshot.PredictedIobJson)
            ?? DeserializeCurve(snapshot.PredictedDefaultJson)
            ?? DeserializeCurve(snapshot.PredictedCobJson)
            ?? DeserializeCurve(snapshot.PredictedZtJson)
            ?? DeserializeCurve(snapshot.PredictedUamJson);

        if (values == null || values.Count == 0)
        {
            return;
        }

        var startMills = snapshot.PredictedStartMills
            ?? snapshot.Timestamp.ToMills();

        response.Predictions = new V4Predictions
        {
            Values = values,
            StartMills = startMills,
            IntervalMills = CgmIntervalMills,
            Source = "openaps",
        };
    }

    /// <summary>
    /// Deserialize a JSON array of nullable doubles into a list, filtering out nulls
    /// </summary>
    private static List<double>? DeserializeCurve(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            var values = JsonSerializer.Deserialize<List<double?>>(json);
            return values?.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
