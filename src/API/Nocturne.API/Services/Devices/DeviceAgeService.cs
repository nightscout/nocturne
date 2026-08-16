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
    private readonly ILogger<DeviceAgeService> _logger;

    private static readonly DeviceEventType[] CannulaEventTypes = [DeviceEventType.SiteChange, DeviceEventType.CannulaChange];
    private static readonly DeviceEventType[] SensorStartEventTypes = [DeviceEventType.SensorStart];
    private static readonly DeviceEventType[] SensorChangeEventTypes = [DeviceEventType.SensorChange];
    private static readonly DeviceEventType[] InsulinEventTypes = [DeviceEventType.InsulinChange, DeviceEventType.ReservoirChange];
    private static readonly DeviceEventType[] BatteryEventTypes = [DeviceEventType.PumpBatteryChange];

    // Default thresholds matching legacy Nightscout values
    private static readonly DeviceAgePreferences DefaultCannulaPrefs = new() { Info = 44, Warn = 48, Urgent = 72 };
    private static readonly DeviceAgePreferences DefaultSensorPrefs = new() { Info = 144, Warn = 164, Urgent = 166 };
    private static readonly DeviceAgePreferences DefaultInsulinPrefs = new() { Info = 44, Warn = 48, Urgent = 72 };
    private static readonly DeviceAgePreferences DefaultBatteryPrefs = new() { Info = 312, Warn = 336, Urgent = 360 };

    public DeviceAgeService(IDeviceEventRepository repository, ILogger<DeviceAgeService> logger)
    {
        _repository = repository;
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

    private static DeviceAgeInfo CreateDeviceAgeInfo(
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
                Level = 0,
                Display = "n/a"
            };
        }

        var ageSpan = DateTime.UtcNow - deviceEvent.Timestamp;
        var totalHours = ageSpan.TotalHours;
        var age = (int)totalHours;
        var days = age / 24;
        var hours = age % 24;
        var minFractions = (int)((totalHours - age) * 60);
        var treatmentDate = new DateTimeOffset(deviceEvent.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds();

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
        if (prefs.EnableAlerts && level > 0 && minFractions <= 20)
        {
            info.Notification = CreateNotification(age, days, hours, level, group, deviceLabel);
        }

        return info;
    }

    private static int CalculateLevel(int age, DeviceAgePreferences prefs)
    {
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
