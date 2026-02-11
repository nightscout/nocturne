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
    public bool EnableAlarmToasts { get; set; } = true;
    public bool EnableUrgentAlarmToasts { get; set; } = true;
    public bool LaunchAtStartup { get; set; }
    public int PollingIntervalSeconds { get; set; } = 60;
    public int ChartHours { get; set; } = 3;
}
