using Nocturne.Widget.Contracts;

namespace Nocturne.Desktop.Tray.Models;

/// <summary>
/// Persistent settings for the tray application.
/// </summary>
public sealed class TraySettings
{
    public string? ServerUrl { get; set; }
    public GlucoseUnit Unit { get; set; } = GlucoseUnit.MgDl;
    public int LowThreshold { get; set; } = 70;
    public int HighThreshold { get; set; } = 180;
    public int UrgentLowThreshold { get; set; } = 55;
    public int UrgentHighThreshold { get; set; } = 250;
    /// <summary>
    /// Alarm types the user has opted out of. Empty = all enabled.
    /// Values match the server's AlertType enum names.
    /// </summary>
    public HashSet<string> DisabledAlarmTypes { get; set; } = [];

    /// <summary>
    /// All known alarm types for UI display.
    /// </summary>
    public static readonly (string Type, string Label)[] KnownAlarmTypes =
    [
        ("UrgentLow", "Urgent Low"),
        ("Low", "Low"),
        ("High", "High"),
        ("UrgentHigh", "Urgent High"),
        ("ForecastLow", "Forecast Low"),
        ("DeviceWarning", "Device Warning"),
    ];
    public bool LaunchAtStartup { get; set; }
    public int PollingIntervalSeconds { get; set; } = 60;
    public int ChartHours { get; set; } = 3;
}
