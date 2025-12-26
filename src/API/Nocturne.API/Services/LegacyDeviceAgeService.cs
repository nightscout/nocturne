using System.Text.Json;
using Nocturne.API.Extensions;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Services;

/// <summary>
/// Service to provide legacy deviceage endpoint compatibility using the Tracker system
/// </summary>
public interface ILegacyDeviceAgeService
{
    /// <summary>
    /// Get cannula/site age (CAGE)
    /// </summary>
    Task<DeviceAgeInfo> GetCannulaAgeAsync(string userId, DeviceAgePreferences prefs, CancellationToken ct = default);

    /// <summary>
    /// Get sensor age (SAGE) - returns composite with both start and change events
    /// </summary>
    Task<SensorAgeInfo> GetSensorAgeAsync(string userId, DeviceAgePreferences prefs, CancellationToken ct = default);

    /// <summary>
    /// Get insulin reservoir age (IAGE)
    /// </summary>
    Task<DeviceAgeInfo> GetInsulinAgeAsync(string userId, DeviceAgePreferences prefs, CancellationToken ct = default);

    /// <summary>
    /// Get pump battery age (BAGE)
    /// </summary>
    Task<DeviceAgeInfo> GetBatteryAgeAsync(string userId, DeviceAgePreferences prefs, CancellationToken ct = default);
}

public class LegacyDeviceAgeService : ILegacyDeviceAgeService
{
    private readonly TrackerRepository _repository;
    private readonly ILogger<LegacyDeviceAgeService> _logger;

    // Event types that map to each legacy age category
    private static readonly string[] CannulaEventTypes = ["Site Change", "Cannula Change"];
    private static readonly string[] SensorStartEventTypes = ["Sensor Start"];
    private static readonly string[] SensorChangeEventTypes = ["Sensor Change"];
    private static readonly string[] InsulinEventTypes = ["Insulin Change", "Reservoir Change"];
    private static readonly string[] BatteryEventTypes = ["Pump Battery Change"];

    // Default thresholds matching legacy Nightscout values
    private static readonly DeviceAgePreferences DefaultCannulaPrefs = new() { Info = 44, Warn = 48, Urgent = 72 };
    private static readonly DeviceAgePreferences DefaultSensorPrefs = new() { Info = 144, Warn = 164, Urgent = 166 }; // 6d, ~6.8d, ~6.9d
    private static readonly DeviceAgePreferences DefaultInsulinPrefs = new() { Info = 44, Warn = 48, Urgent = 72 };
    private static readonly DeviceAgePreferences DefaultBatteryPrefs = new() { Info = 312, Warn = 336, Urgent = 360 }; // 13d, 14d, 15d

    public LegacyDeviceAgeService(TrackerRepository repository, ILogger<LegacyDeviceAgeService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<DeviceAgeInfo> GetCannulaAgeAsync(string userId, DeviceAgePreferences prefs, CancellationToken ct = default)
    {
        var effectivePrefs = MergePreferences(prefs, DefaultCannulaPrefs);
        return await GetDeviceAgeAsync(userId, CannulaEventTypes, effectivePrefs, "CAGE", "Cannula", ct);
    }

    public async Task<SensorAgeInfo> GetSensorAgeAsync(string userId, DeviceAgePreferences prefs, CancellationToken ct = default)
    {
        var effectivePrefs = MergePreferences(prefs, DefaultSensorPrefs);
        var instances = await _repository.GetActiveInstancesAsync(userId, ct);

        var sensorStartInstance = FindMatchingInstance(instances, SensorStartEventTypes);
        var sensorChangeInstance = FindMatchingInstance(instances, SensorChangeEventTypes);

        var sensorStart = CreateDeviceAgeInfo(sensorStartInstance, effectivePrefs, "SAGE", "Sensor");
        var sensorChange = CreateDeviceAgeInfo(sensorChangeInstance, effectivePrefs, "SAGE", "Sensor");

        // Determine which is more recent (min = most recent valid one)
        var min = "Sensor Start";
        if (sensorChange.Found && sensorStart.Found)
        {
            if (sensorChange.TreatmentDate > sensorStart.TreatmentDate)
            {
                min = "Sensor Change";
                // Legacy behavior: if Sensor Change is more recent, hide Sensor Start
                sensorStart.Found = false;
            }
        }
        else if (sensorChange.Found && !sensorStart.Found)
        {
            min = "Sensor Change";
        }

        return new SensorAgeInfo
        {
            SensorStart = sensorStart,
            SensorChange = sensorChange,
            Min = min
        };
    }

    public async Task<DeviceAgeInfo> GetInsulinAgeAsync(string userId, DeviceAgePreferences prefs, CancellationToken ct = default)
    {
        var effectivePrefs = MergePreferences(prefs, DefaultInsulinPrefs);
        return await GetDeviceAgeAsync(userId, InsulinEventTypes, effectivePrefs, "IAGE", "Insulin reservoir", ct);
    }

    public async Task<DeviceAgeInfo> GetBatteryAgeAsync(string userId, DeviceAgePreferences prefs, CancellationToken ct = default)
    {
        var effectivePrefs = MergePreferences(prefs, DefaultBatteryPrefs);
        return await GetDeviceAgeAsync(userId, BatteryEventTypes, effectivePrefs, "BAGE", "Pump battery", ct);
    }

    private async Task<DeviceAgeInfo> GetDeviceAgeAsync(
        string userId,
        string[] eventTypes,
        DeviceAgePreferences prefs,
        string group,
        string deviceLabel,
        CancellationToken ct)
    {
        var instances = await _repository.GetActiveInstancesAsync(userId, ct);
        var matchingInstance = FindMatchingInstance(instances, eventTypes);
        return CreateDeviceAgeInfo(matchingInstance, prefs, group, deviceLabel);
    }

    private static TrackerInstanceEntity? FindMatchingInstance(TrackerInstanceEntity[] instances, string[] eventTypes)
    {
        // Find active instances with definitions matching the event types
        return instances
            .Where(i => i.Definition != null && DefinitionMatchesEventTypes(i.Definition, eventTypes))
            .OrderByDescending(i => i.StartedAt) // Most recent first
            .FirstOrDefault();
    }

    private static bool DefinitionMatchesEventTypes(TrackerDefinitionEntity definition, string[] eventTypes)
    {
        try
        {
            var triggerTypes = JsonSerializer.Deserialize<List<string>>(definition.TriggerEventTypes) ?? [];
            return triggerTypes.Any(t => eventTypes.Contains(t, StringComparer.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static DeviceAgeInfo CreateDeviceAgeInfo(
        TrackerInstanceEntity? instance,
        DeviceAgePreferences prefs,
        string group,
        string deviceLabel)
    {
        if (instance == null)
        {
            return new DeviceAgeInfo
            {
                Found = false,
                Age = 0,
                Days = 0,
                Hours = 0,
                Level = 0, // NONE
                Display = "n/a"
            };
        }

        var age = (int)instance.AgeHours;
        var days = age / 24;
        var hours = age % 24;
        var minFractions = (int)((instance.AgeHours - age) * 60);
        var treatmentDate = new DateTimeOffset(instance.StartedAt, TimeSpan.Zero).ToUnixTimeMilliseconds();

        // Calculate level based on thresholds
        // Priority order: check tracker's own thresholds first, then fall back to provided prefs
        var level = CalculateLevel(instance, age, prefs);

        // Build display string
        var display = FormatDisplay(age, days, hours, prefs.Display);

        var info = new DeviceAgeInfo
        {
            Found = true,
            Age = age,
            Days = days,
            Hours = hours,
            TreatmentDate = treatmentDate,
            Notes = instance.StartNotes,
            MinFractions = minFractions,
            Level = level,
            Display = display
        };

        // Add notification if alerts enabled and threshold reached
        if (prefs.EnableAlerts && level > 0 && minFractions <= 20)
        {
            info.Notification = CreateNotification(age, days, hours, level, group, deviceLabel);
        }

        return info;
    }

    private static int CalculateLevel(TrackerInstanceEntity instance, int age, DeviceAgePreferences prefs)
    {
        // First check if tracker definition has thresholds
        var thresholds = instance.Definition?.NotificationThresholds?
            .OrderByDescending(t => t.Hours)
            .ToList();

        if (thresholds != null && thresholds.Count > 0)
        {
            foreach (var threshold in thresholds)
            {
                if (age >= threshold.Hours)
                {
                    return threshold.Urgency switch
                    {
                        NotificationUrgency.Urgent => 2, // URGENT
                        NotificationUrgency.Hazard => 2, // Map hazard to URGENT for legacy compat
                        NotificationUrgency.Warn => 1,   // WARN
                        NotificationUrgency.Info => 1,   // Map info to WARN for legacy (legacy has no INFO level display)
                        _ => 0
                    };
                }
            }
        }

        // Fall back to provided preferences
        if (age >= prefs.Urgent)
            return 2; // URGENT
        if (age >= prefs.Warn)
            return 1; // WARN
        if (age >= prefs.Info)
            return 1; // INFO (maps to WARN display in legacy)

        return 0; // NONE
    }

    private static string FormatDisplay(int age, int days, int hours, string displayMode)
    {
        if (displayMode?.Equals("days", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (age >= 24)
                return $"{days}d{hours}h";
            return $"{hours}h";
        }

        // Default: hours mode
        return $"{age}h";
    }

    private static DeviceAgeNotification CreateNotification(int age, int days, int hours, int level, string group, string deviceLabel)
    {
        var sound = level >= 2 ? "persistent" : "incoming";
        var (title, message) = level switch
        {
            2 => ($"{deviceLabel} age {age} hours", $"{deviceLabel} change overdue!"),
            1 => ($"{deviceLabel} age {age} hours", $"Time to change {deviceLabel.ToLower()}"),
            _ => ($"{deviceLabel} age {age} hours", $"Change {deviceLabel.ToLower()} soon")
        };

        return new DeviceAgeNotification
        {
            Title = title,
            Message = message,
            PushoverSound = sound,
            Level = level,
            Group = group
        };
    }

    private static DeviceAgePreferences MergePreferences(DeviceAgePreferences provided, DeviceAgePreferences defaults)
    {
        return new DeviceAgePreferences
        {
            Info = provided.Info > 0 ? provided.Info : defaults.Info,
            Warn = provided.Warn > 0 ? provided.Warn : defaults.Warn,
            Urgent = provided.Urgent > 0 ? provided.Urgent : defaults.Urgent,
            Display = !string.IsNullOrEmpty(provided.Display) ? provided.Display : defaults.Display,
            EnableAlerts = provided.EnableAlerts
        };
    }
}
