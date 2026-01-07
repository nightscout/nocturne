using Nocturne.Core.Contracts;
using Nocturne.Core.Models;

namespace Nocturne.API.Services;

/// <summary>
/// Service for monitoring pump status and generating alerts
/// Implements legacy Nightscout pump.js plugin functionality with 1:1 API compatibility
/// </summary>
public class PumpAlertService : IPumpAlertService
{
    private static readonly string[] AllStatusFields =
    [
        "reservoir",
        "battery",
        "clock",
        "status",
        "device",
    ];

    private readonly IOpenApsService _openApsService;
    private readonly ILogger<PumpAlertService> _logger;

    public PumpAlertService(IOpenApsService openApsService, ILogger<PumpAlertService> logger)
    {
        _openApsService = openApsService;
        _logger = logger;
    }

    /// <inheritdoc />
    public PumpPreferences GetPreferences(
        Dictionary<string, object?>? extendedSettings,
        double? dayStart = null,
        double? dayEnd = null
    )
    {
        var prefs = new PumpPreferences { DayStart = dayStart ?? 7.0, DayEnd = dayEnd ?? 21.0 };

        if (extendedSettings is null)
            return prefs;

        // Parse fields
        if (
            extendedSettings.TryGetValue("fields", out var fieldsObj)
            && fieldsObj is string fieldsStr
        )
        {
            var fields = ParseFieldList(fieldsStr);
            if (fields.Length > 0)
                prefs.Fields = fields;
        }

        if (
            extendedSettings.TryGetValue("retroFields", out var retroFieldsObj)
            && retroFieldsObj is string retroFieldsStr
        )
        {
            var retroFields = ParseFieldList(retroFieldsStr);
            if (retroFields.Length > 0)
                prefs.RetroFields = retroFields;
        }

        // Parse thresholds
        prefs.WarnClock = GetIntSetting(extendedSettings, "warnClock", 30);
        prefs.UrgentClock = GetIntSetting(extendedSettings, "urgentClock", 60);
        prefs.WarnRes = GetDoubleSetting(extendedSettings, "warnRes", 10);
        prefs.UrgentRes = GetDoubleSetting(extendedSettings, "urgentRes", 5);
        prefs.WarnBattV = GetDoubleSetting(extendedSettings, "warnBattV", 1.35);
        prefs.UrgentBattV = GetDoubleSetting(extendedSettings, "urgentBattV", 1.3);
        prefs.WarnBattP = GetIntSetting(extendedSettings, "warnBattP", 30);
        prefs.UrgentBattP = GetIntSetting(extendedSettings, "urgentBattP", 20);

        // Parse boolean settings
        prefs.WarnOnSuspend = GetBoolSetting(extendedSettings, "warnOnSuspend", false);
        prefs.EnableAlerts = GetBoolSetting(extendedSettings, "enableAlerts", false);
        prefs.WarnBattQuietNight = GetBoolSetting(extendedSettings, "warnBattQuietNight", false);

        return prefs;
    }

    /// <inheritdoc />
    public PumpStatusResult BuildPumpStatus(
        IEnumerable<DeviceStatus> deviceStatuses,
        long currentTime,
        PumpPreferences preferences,
        IProfileService profileService,
        IEnumerable<Treatment>? treatments = null
    )
    {
        var recentMills = currentTime - (preferences.UrgentClock * 2 * 60 * 1000);

        // Filter to recent device statuses with pump data
        var filtered = deviceStatuses
            .Where(status => status.Pump is not null)
            .Where(status => status.Mills <= currentTime && status.Mills >= recentMills)
            .ToList();

        // Find most recent by pump clock time
        DeviceStatus? pumpStatus = null;
        long bestClockMills = 0;

        foreach (var status in filtered)
        {
            var clockMills = status.Pump?.Clock is not null
                ? ParseDateTime(status.Pump.Clock)?.ToUnixTimeMilliseconds() ?? status.Mills
                : status.Mills;

            if (pumpStatus is null || clockMills > bestClockMills)
            {
                pumpStatus = status;
                bestClockMills = clockMills;
            }
        }

        return PrepareData(pumpStatus, preferences, currentTime, profileService, treatments);
    }

    /// <inheritdoc />
    public NotificationBase? CheckNotifications(
        PumpStatusResult status,
        PumpPreferences preferences,
        long currentTime,
        IProfileService profileService,
        IEnumerable<Treatment>? treatments = null
    )
    {
        if (!preferences.EnableAlerts)
            return null;

        if (status.Level >= PumpAlertLevel.Warn)
        {
            return new NotificationBase
            {
                Level = (int)status.Level,
                Title = status.Title,
                Message = status.Message ?? string.Empty,
                Group = "Pump",
                Plugin = "pump",
                Timestamp = currentTime,
                Persistent = false,
            };
        }

        return null;
    }

    /// <inheritdoc />
    public PumpVisualizationData GenerateVisualizationData(
        PumpStatusResult status,
        PumpPreferences preferences,
        bool isRetroMode,
        long currentTime,
        IProfileService profileService,
        IEnumerable<Treatment>? treatments = null
    )
    {
        var values = new List<string>();
        var info = new List<PumpInfoItem>();

        var selectedFields = isRetroMode ? preferences.RetroFields : preferences.Fields;

        foreach (var fieldName in AllStatusFields)
        {
            var field = GetFieldByName(status, fieldName);
            if (field is null)
                continue;

            var isSelected = selectedFields.Contains(fieldName, StringComparer.OrdinalIgnoreCase);
            if (isSelected)
            {
                values.Add(field.Display);
            }
            else
            {
                info.Add(new PumpInfoItem { Label = field.Label, Value = field.Display });
            }
        }

        // Add extended data
        if (status.Extended is not null)
        {
            info.Add(new PumpInfoItem { Label = "------------", Value = "" });
            foreach (var kvp in status.Extended)
            {
                info.Add(new PumpInfoItem { Label = kvp.Key, Value = kvp.Value?.ToString() ?? "" });
            }
        }

        return new PumpVisualizationData
        {
            Value = string.Join(" ", values),
            Info = info,
            Label = "Pump",
            PillClass = GetStatusClass(status.Level),
        };
    }

    /// <inheritdoc />
    public (string title, string response) HandleVirtualAssistantReservoir(PumpStatusResult status)
    {
        const string title = "Insulin Remaining";

        if (status.Reservoir?.Value is double reservoir)
        {
            return (title, $"You have {reservoir} units remaining");
        }

        return (title, "Unknown");
    }

    /// <inheritdoc />
    public (string title, string response) HandleVirtualAssistantBattery(PumpStatusResult status)
    {
        const string title = "Pump Battery";

        if (status.Battery is not null)
        {
            var value = status.Battery.Value;
            var unit = status.Battery.Unit ?? "volts";
            return (title, $"Your pump battery is at {value} {unit}");
        }

        return (title, "Unknown");
    }

    #region Private Methods

    private PumpStatusResult PrepareData(
        DeviceStatus? prop,
        PumpPreferences preferences,
        long currentTime,
        IProfileService profileService,
        IEnumerable<Treatment>? treatments
    )
    {
        var pump = prop?.Pump;
        var batteryWarn = ShouldWarnBattery(preferences, currentTime, profileService);

        var result = new PumpStatusResult
        {
            Level = PumpAlertLevel.None,
            Title = "Pump Status",
            Manufacturer = pump?.Manufacturer,
            Model = pump?.Model,
            Extended = pump?.Extended,
            SourceDeviceStatus = prop,
        };

        // Clock
        if (pump?.Clock is not null)
        {
            var clockTime = ParseDateTime(pump.Clock);
            if (clockTime.HasValue)
            {
                result.Clock = new PumpFieldStatus
                {
                    Value = clockTime.Value,
                    Label = "Last Clock",
                    Display = FormatTimeAgo(clockTime.Value, currentTime),
                };
                UpdateClockLevel(result.Clock, preferences, currentTime);
            }
        }

        // Reservoir
        if (pump?.Reservoir is not null || pump?.Reservoir == 0)
        {
            result.Reservoir = new PumpFieldStatus { Value = pump.Reservoir, Label = "Reservoir" };
            UpdateReservoirLevel(result.Reservoir, preferences, pump);
        }
        else if (pump?.Manufacturer == "Insulet")
        {
            // Omnipod default display
            result.Reservoir = new PumpFieldStatus
            {
                Label = "Reservoir",
                Display = "50+ U",
                Level = PumpAlertLevel.None,
            };
        }

        // Battery
        if (pump?.Battery?.Percent is not null)
        {
            result.Battery = new PumpFieldStatus
            {
                Value = pump.Battery.Percent,
                Unit = "percent",
                Label = "Battery",
            };
            UpdateBatteryLevel(result.Battery, "%", preferences, batteryWarn);
        }
        else if (pump?.Battery?.Voltage is not null)
        {
            result.Battery = new PumpFieldStatus
            {
                Value = pump.Battery.Voltage,
                Unit = "volts",
                Label = "Battery",
            };
            UpdateBatteryLevel(result.Battery, "v", preferences, batteryWarn);
        }

        // Status
        if (pump?.Status is not null)
        {
            var statusValue = pump.Status.Status ?? "normal";
            if (pump.Status.Bolusing == true)
            {
                statusValue = "bolusing";
            }
            else if (pump.Status.Suspended == true)
            {
                statusValue = "suspended";
            }

            result.Status = new PumpFieldStatus
            {
                Value = statusValue,
                Display = statusValue,
                Label = "Status",
                Level = PumpAlertLevel.None,
            };

            if (preferences.WarnOnSuspend && pump.Status.Suspended == true)
            {
                result.Status.Level = PumpAlertLevel.Warn;
                result.Status.Message = "Pump Suspended";
            }
        }

        // Device
        if (prop?.Device is not null)
        {
            result.Device = new PumpFieldStatus
            {
                Value = prop.Device,
                Display = prop.Device,
                Label = "Device",
            };
        }

        // Calculate overall level (respecting offline marker)
        result.Level = PumpAlertLevel.None;
        result.Title = "Pump Status";

        var offlineMarker = treatments is not null
            ? _openApsService.FindOfflineMarker(
                treatments,
                DateTimeOffset.FromUnixTimeMilliseconds(currentTime).UtcDateTime
            )
            : null;

        if (offlineMarker is not null)
        {
            _logger.LogDebug("OpenAPS known offline, not checking for alerts");
        }
        else
        {
            foreach (var fieldName in AllStatusFields)
            {
                var field = GetFieldByName(result, fieldName);
                if (field is not null && field.Level > result.Level)
                {
                    result.Level = field.Level;
                    result.Title = field.Message ?? result.Title;
                }
            }
        }

        // Build message
        BuildMessage(result);

        return result;
    }

    private void UpdateClockLevel(
        PumpFieldStatus clock,
        PumpPreferences preferences,
        long currentTime
    )
    {
        if (clock.Value is not DateTimeOffset clockTime)
            return;

        var clockMills = clockTime.ToUnixTimeMilliseconds();
        var urgentMills = currentTime - (preferences.UrgentClock * 60 * 1000);
        var warnMills = currentTime - (preferences.WarnClock * 60 * 1000);

        if (clockMills < urgentMills)
        {
            clock.Level = PumpAlertLevel.Urgent;
            clock.Message = "URGENT: Pump data stale";
        }
        else if (clockMills < warnMills)
        {
            clock.Level = PumpAlertLevel.Warn;
            clock.Message = "Warning, Pump data stale";
        }
        else
        {
            clock.Level = PumpAlertLevel.None;
        }
    }

    private static void UpdateReservoirLevel(
        PumpFieldStatus reservoir,
        PumpPreferences preferences,
        PumpStatus pump
    )
    {
        var value = pump.Reservoir ?? 0;
        reservoir.Display = $"{value:F1}U";

        if (value < preferences.UrgentRes)
        {
            reservoir.Level = PumpAlertLevel.Urgent;
            reservoir.Message = "URGENT: Pump Reservoir Low";
        }
        else if (value < preferences.WarnRes)
        {
            reservoir.Level = PumpAlertLevel.Warn;
            reservoir.Message = "Warning, Pump Reservoir Low";
        }
        else
        {
            reservoir.Level = PumpAlertLevel.None;
        }

        // Apply overrides
        if (!string.IsNullOrEmpty(pump.ReservoirDisplayOverride))
        {
            reservoir.Display = pump.ReservoirDisplayOverride;
        }

        if (pump.ReservoirLevelOverride.HasValue)
        {
            reservoir.Level = pump.ReservoirLevelOverride.Value;
        }
    }

    private static void UpdateBatteryLevel(
        PumpFieldStatus battery,
        string type,
        PumpPreferences preferences,
        bool batteryWarn
    )
    {
        var value = Convert.ToDouble(battery.Value);
        battery.Display = $"{value}{type}";

        var urgent = type == "v" ? preferences.UrgentBattV : preferences.UrgentBattP;
        var warn = type == "v" ? preferences.WarnBattV : preferences.WarnBattP;

        if (value < urgent && batteryWarn)
        {
            battery.Level = PumpAlertLevel.Urgent;
            battery.Message = "URGENT: Pump Battery Low";
        }
        else if (value < warn && batteryWarn)
        {
            battery.Level = PumpAlertLevel.Warn;
            battery.Message = "Warning, Pump Battery Low";
        }
        else
        {
            battery.Level = PumpAlertLevel.None;
        }
    }

    private static void BuildMessage(PumpStatusResult result)
    {
        if (result.Level <= PumpAlertLevel.None)
            return;

        var messages = new List<string>();

        if (result.Battery is not null)
        {
            messages.Add($"Pump Battery: {result.Battery.Display}");
        }

        if (result.Reservoir is not null)
        {
            messages.Add($"Pump Reservoir: {result.Reservoir.Display}");
        }

        result.Message = string.Join("\n", messages);
    }

    private bool ShouldWarnBattery(
        PumpPreferences preferences,
        long currentTime,
        IProfileService profileService
    )
    {
        if (!preferences.WarnBattQuietNight)
            return true;

        try
        {
            var timezone = profileService.GetTimezone();
            if (string.IsNullOrEmpty(timezone))
                return true;

            var tz = TimeZoneInfo.FindSystemTimeZoneById(timezone);
            var localTime = TimeZoneInfo.ConvertTimeFromUtc(
                DateTimeOffset.FromUnixTimeMilliseconds(currentTime).UtcDateTime,
                tz
            );

            var now = localTime.Hour + localTime.Minute / 60.0 + localTime.Second / 3600.0;

            // During night (before dayStart or after dayEnd), suppress battery warnings
            var isNight = now < preferences.DayStart || now > preferences.DayEnd;
            return !isNight;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to determine quiet night status, enabling battery warnings"
            );
            return true;
        }
    }

    private static PumpFieldStatus? GetFieldByName(PumpStatusResult result, string fieldName)
    {
        return fieldName.ToLowerInvariant() switch
        {
            "reservoir" => result.Reservoir,
            "battery" => result.Battery,
            "clock" => result.Clock,
            "status" => result.Status,
            "device" => result.Device,
            _ => null,
        };
    }

    private static string GetStatusClass(PumpAlertLevel level)
    {
        return level switch
        {
            PumpAlertLevel.Warn => "warn",
            PumpAlertLevel.Urgent => "urgent",
            _ => "current",
        };
    }

    private static string[] ParseFieldList(string value)
    {
        return Uri.UnescapeDataString(value)
            .ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static int GetIntSetting(
        Dictionary<string, object?> settings,
        string key,
        int defaultValue
    )
    {
        if (settings.TryGetValue(key, out var value))
        {
            return value switch
            {
                int i => i,
                long l => (int)l,
                double d => (int)d,
                string s when int.TryParse(s, out var parsed) => parsed,
                _ => defaultValue,
            };
        }
        return defaultValue;
    }

    private static double GetDoubleSetting(
        Dictionary<string, object?> settings,
        string key,
        double defaultValue
    )
    {
        if (settings.TryGetValue(key, out var value))
        {
            return value switch
            {
                double d => d,
                int i => i,
                long l => l,
                string s when double.TryParse(s, out var parsed) => parsed,
                _ => defaultValue,
            };
        }
        return defaultValue;
    }

    private static bool GetBoolSetting(
        Dictionary<string, object?> settings,
        string key,
        bool defaultValue
    )
    {
        if (settings.TryGetValue(key, out var value))
        {
            return value switch
            {
                bool b => b,
                string s => s.Equals("true", StringComparison.OrdinalIgnoreCase),
                _ => defaultValue,
            };
        }
        return defaultValue;
    }

    private static DateTimeOffset? ParseDateTime(string? dateString)
    {
        if (string.IsNullOrEmpty(dateString))
            return null;

        if (DateTimeOffset.TryParse(dateString, out var result))
            return result;

        return null;
    }

    private static string FormatTimeAgo(DateTimeOffset time, long currentMills)
    {
        var currentTime = DateTimeOffset.FromUnixTimeMilliseconds(currentMills);
        var diff = currentTime - time;

        if (diff.TotalMinutes < 1)
            return "now";
        if (diff.TotalMinutes < 60)
            return $"{(int)diff.TotalMinutes}m ago";
        if (diff.TotalHours < 24)
            return $"{(int)diff.TotalHours}h ago";

        return time.ToString("g");
    }

    #endregion
}
