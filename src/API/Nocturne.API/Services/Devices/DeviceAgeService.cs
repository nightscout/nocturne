using Nocturne.API.Services.Monitoring;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Devices;

/// <summary>
/// Calculates consumable device ages (cannula, sensor, insulin reservoir, pump battery) by querying
/// the most recent relevant <see cref="DeviceEvent"/> records and comparing them against configurable
/// info/warn/urgent thresholds. Default thresholds match legacy Nightscout values.
/// </summary>
/// <seealso cref="IDeviceAgeService"/>
public class DeviceAgeService : IDeviceAgeService
{
    private readonly IDeviceEventRepository _repository;
    private readonly TimeProvider _time;
    private readonly ILogger<DeviceAgeService> _logger;

    // NIGHTSCOUT-COMPAT: upstream CAGE reads Site Change alone. A pod change, however it is reported,
    // is a new site, so a pod user's CAGE follows it.
    private static readonly DeviceEventType[] CannulaEventTypes = [.. ConsumableTrackerSpecs.EventTypes(LegacyConsumableKind.Site)];
    private static readonly DeviceEventType[] SensorStartEventTypes = [DeviceEventType.SensorStart];
    private static readonly DeviceEventType[] SensorChangeEventTypes = [DeviceEventType.SensorChange];
    // NIGHTSCOUT-COMPAT: upstream IAGE reads Insulin Change alone. A pod holds the insulin, so a new
    // pod is a new reservoir and a pod user's IAGE follows it.
    private static readonly DeviceEventType[] InsulinEventTypes =
    [
        .. ConsumableTrackerSpecs.EventTypes(LegacyConsumableKind.Reservoir),
        DeviceEventType.PodChange,
        DeviceEventType.PodActivated,
    ];
    private static readonly DeviceEventType[] BatteryEventTypes = [DeviceEventType.PumpBatteryChange];

    // Default thresholds matching legacy Nightscout values
    private static readonly DeviceAgePreferences DefaultCannulaPrefs = new() { Info = 44, Warn = 48, Urgent = 72 };
    private static readonly DeviceAgePreferences DefaultSensorPrefs = new() { Info = 144, Warn = 164, Urgent = 166 };
    private static readonly DeviceAgePreferences DefaultInsulinPrefs = new() { Info = 44, Warn = 48, Urgent = 72 };
    private static readonly DeviceAgePreferences DefaultBatteryPrefs = new() { Info = 312, Warn = 336, Urgent = 360 };

    public DeviceAgeService(
        IDeviceEventRepository repository,
        TimeProvider timeProvider,
        ILogger<DeviceAgeService> logger)
    {
        _repository = repository;
        _time = timeProvider;
        _logger = logger;
    }

    public async Task<DeviceAgeInfo> GetCannulaAgeAsync(DeviceAgePreferences prefs, Guid? patientDeviceId = null, CancellationToken ct = default)
    {
        var effectivePrefs = MergePreferences(prefs, DefaultCannulaPrefs);
        var latestEvent = await _repository.GetLatestByEventTypesAsync(CannulaEventTypes, patientDeviceId, ct);
        return CreateDeviceAgeInfo(latestEvent, effectivePrefs, "CAGE", "Cannula");
    }

    public async Task<SensorAgeInfo> GetSensorAgeAsync(DeviceAgePreferences prefs, Guid? patientDeviceId = null, CancellationToken ct = default)
    {
        var effectivePrefs = MergePreferences(prefs, DefaultSensorPrefs);

        var sensorStartEvent = await _repository.GetLatestByEventTypesAsync(SensorStartEventTypes, patientDeviceId, ct);
        var sensorChangeEvent = await _repository.GetLatestByEventTypesAsync(SensorChangeEventTypes, patientDeviceId, ct);

        var sensorStart = CreateDeviceAgeInfo(sensorStartEvent, effectivePrefs, "SAGE", "Sensor");
        var sensorChange = CreateDeviceAgeInfo(sensorChangeEvent, effectivePrefs, "SAGE", "Sensor");

        // Determine which is more recent (min = most recent valid one)
        var min = TreatmentTypes.SensorStart;
        if (sensorChange.Found && sensorStart.Found)
        {
            if (sensorChange.TreatmentDate >= sensorStart.TreatmentDate)
            {
                min = TreatmentTypes.SensorChange;
                // Legacy behavior: if Sensor Change is more recent, hide Sensor Start
                sensorStart.Found = false;
            }
        }
        else if (sensorChange.Found && !sensorStart.Found)
        {
            min = TreatmentTypes.SensorChange;
        }

        return new SensorAgeInfo
        {
            SensorStart = sensorStart,
            SensorChange = sensorChange,
            Min = min
        };
    }

    public async Task<DeviceAgeInfo> GetInsulinAgeAsync(DeviceAgePreferences prefs, Guid? patientDeviceId = null, CancellationToken ct = default)
    {
        var effectivePrefs = MergePreferences(prefs, DefaultInsulinPrefs);
        var latestEvent = await _repository.GetLatestByEventTypesAsync(InsulinEventTypes, patientDeviceId, ct);
        return CreateDeviceAgeInfo(latestEvent, effectivePrefs, "IAGE", "Insulin reservoir");
    }

    public async Task<DeviceAgeInfo> GetBatteryAgeAsync(DeviceAgePreferences prefs, Guid? patientDeviceId = null, CancellationToken ct = default)
    {
        var effectivePrefs = MergePreferences(prefs, DefaultBatteryPrefs);
        var latestEvent = await _repository.GetLatestByEventTypesAsync(BatteryEventTypes, patientDeviceId, ct);
        return CreateDeviceAgeInfo(latestEvent, effectivePrefs, "BAGE", "Pump battery");
    }

    private DeviceAgeInfo CreateDeviceAgeInfo(
        DeviceEvent? deviceEvent,
        DeviceAgePreferences prefs,
        string group,
        string deviceLabel)
    {
        if (deviceEvent == null)
        {
            return new DeviceAgeInfo
            {
                Found = false,
                Age = 0,
                Days = 0,
                Hours = 0,
                Level = DeviceAgeLevel.None,
                Display = "n/a"
            };
        }

        var ageSpan = _time.GetUtcNow().UtcDateTime - deviceEvent.Timestamp;
        var totalHours = ageSpan.TotalHours;
        var age = (int)totalHours;
        var days = age / 24;
        var hours = age % 24;
        var minFractions = (int)((totalHours - age) * 60);
        var treatmentDate = deviceEvent.Timestamp.ToMills();

        var level = CalculateLevel(age, prefs);

        var display = FormatDisplay(age, days, hours, prefs.Display);

        var info = new DeviceAgeInfo
        {
            Found = true,
            Age = age,
            Days = days,
            Hours = hours,
            TreatmentDate = treatmentDate,
            Notes = deviceEvent.Notes,
            MinFractions = minFractions,
            Level = level,
            Display = display
        };

        // Add notification if alerts enabled and threshold reached
        if (prefs.EnableAlerts && level >= DeviceAgeLevel.Info && minFractions <= 20)
        {
            info.Notification = CreateNotification(age, days, hours, level, group, deviceLabel);
        }

        return info;
    }

    private static int CalculateLevel(int age, DeviceAgePreferences prefs)
    {
        if (age >= prefs.Urgent)
            return DeviceAgeLevel.Urgent;
        if (age >= prefs.Warn)
            return DeviceAgeLevel.Warn;
        if (age >= prefs.Info)
            return DeviceAgeLevel.Info;

        return DeviceAgeLevel.None;
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
        var sound = level >= DeviceAgeLevel.Urgent ? "persistent" : "incoming";
        var (title, message) = level switch
        {
            DeviceAgeLevel.Urgent => ($"{deviceLabel} age {age} hours", $"{deviceLabel} change overdue!"),
            DeviceAgeLevel.Warn => ($"{deviceLabel} age {age} hours", $"Time to change {deviceLabel.ToLower()}"),
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
