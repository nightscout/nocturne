using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Pump monitoring preferences matching legacy Nightscout pump.getPrefs()
/// </summary>
public class PumpPreferences
{
    /// <summary>
    /// Fields to display in normal view (default: ["reservoir"])
    /// </summary>
    public string[] Fields { get; set; } = ["reservoir"];

    /// <summary>
    /// Fields to display in retro mode (default: ["reservoir", "battery"])
    /// </summary>
    public string[] RetroFields { get; set; } = ["reservoir", "battery"];

    /// <summary>
    /// Minutes before pump clock is considered stale (warn level)
    /// </summary>
    public int WarnClock { get; set; } = 30;

    /// <summary>
    /// Minutes before pump clock is considered stale (urgent level)
    /// </summary>
    public int UrgentClock { get; set; } = 60;

    /// <summary>
    /// Reservoir units threshold for warning alert
    /// </summary>
    public double WarnRes { get; set; } = 10;

    /// <summary>
    /// Reservoir units threshold for urgent alert
    /// </summary>
    public double UrgentRes { get; set; } = 5;

    /// <summary>
    /// Battery voltage threshold for warning (for voltage-based pumps like Medtronic)
    /// </summary>
    public double WarnBattV { get; set; } = 1.35;

    /// <summary>
    /// Battery voltage threshold for urgent (for voltage-based pumps like Medtronic)
    /// </summary>
    public double UrgentBattV { get; set; } = 1.3;

    /// <summary>
    /// Battery percentage threshold for warning
    /// </summary>
    public int WarnBattP { get; set; } = 30;

    /// <summary>
    /// Battery percentage threshold for urgent
    /// </summary>
    public int UrgentBattP { get; set; } = 20;

    /// <summary>
    /// Whether to generate warning when pump is suspended
    /// </summary>
    public bool WarnOnSuspend { get; set; } = false;

    /// <summary>
    /// Whether pump alerts are enabled
    /// </summary>
    public bool EnableAlerts { get; set; } = false;

    /// <summary>
    /// Whether to suppress battery warnings during night hours
    /// </summary>
    public bool WarnBattQuietNight { get; set; } = false;

    /// <summary>
    /// Hour (24h format, decimal) when "day" starts for quiet night calculation
    /// Default: 7.0 (7:00 AM)
    /// </summary>
    public double DayStart { get; set; } = 7.0;

    /// <summary>
    /// Hour (24h format, decimal) when "day" ends for quiet night calculation
    /// Default: 21.0 (9:00 PM)
    /// </summary>
    public double DayEnd { get; set; } = 21.0;
}

/// <summary>
/// Result of pump status analysis with alert levels
/// </summary>
public class PumpStatusResult
{
    /// <summary>
    /// Overall alert level
    /// </summary>
    public PumpAlertLevel Level { get; set; }

    /// <summary>
    /// Title for the alert (e.g., "URGENT: Pump Reservoir Low")
    /// </summary>
    public string Title { get; set; } = "Pump Status";

    /// <summary>
    /// Detailed message for the alert containing field values
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Clock/last communication status
    /// </summary>
    public PumpFieldStatus? Clock { get; set; }

    /// <summary>
    /// Reservoir status with level and display value
    /// </summary>
    public PumpFieldStatus? Reservoir { get; set; }

    /// <summary>
    /// Battery status with level and display value
    /// </summary>
    public PumpFieldStatus? Battery { get; set; }

    /// <summary>
    /// Pump operational status (normal, suspended, bolusing)
    /// </summary>
    public PumpFieldStatus? Status { get; set; }

    /// <summary>
    /// Device identifier
    /// </summary>
    public PumpFieldStatus? Device { get; set; }

    /// <summary>
    /// Pump manufacturer
    /// </summary>
    public string? Manufacturer { get; set; }

    /// <summary>
    /// Pump model
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// Extended pump data (arbitrary key-value pairs)
    /// </summary>
    public Dictionary<string, object>? Extended { get; set; }

    /// <summary>
    /// The source device status entry used for this result
    /// </summary>
    [JsonIgnore]
    public DeviceStatus? SourceDeviceStatus { get; set; }
}

/// <summary>
/// Individual pump field status with value, display, and alert level
/// </summary>
public class PumpFieldStatus
{
    /// <summary>
    /// Raw value of the field
    /// </summary>
    public object? Value { get; set; }

    /// <summary>
    /// Formatted display string (e.g., "86.4U", "1.52v")
    /// </summary>
    public string Display { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable label for the field
    /// </summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Unit of measurement (e.g., "volts", "percent", "U")
    /// </summary>
    public string? Unit { get; set; }

    /// <summary>
    /// Alert level for this field
    /// </summary>
    public PumpAlertLevel Level { get; set; }

    /// <summary>
    /// Alert message if level is WARN or URGENT
    /// </summary>
    public string? Message { get; set; }
}

/// <summary>
/// Alert levels matching legacy Nightscout levels.js
/// </summary>
public enum PumpAlertLevel
{
    /// <summary>
    /// No alert
    /// </summary>
    None = 0,

    /// <summary>
    /// Informational level
    /// </summary>
    Info = 1,

    /// <summary>
    /// Warning level
    /// </summary>
    Warn = 2,

    /// <summary>
    /// Urgent/critical level
    /// </summary>
    Urgent = 3
}

