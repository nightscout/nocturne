using Nocturne.Core.Models.Health;

namespace Nocturne.Core.Models;

/// <summary>Public reference values for contextual help, without tenant data or credentials.</summary>
public sealed class HelpReferenceFacts
{
    public GlycemicThresholds ReportBands { get; set; } = new();
    public ChartThresholdsDto ChartBands { get; set; } = new();
    public double MgdlPerMmol { get; set; }
    public double MaximumPlausibleGlucose { get; set; }
    public string GoogleHealthCallbackPath { get; set; } = string.Empty;
    public int GoogleHealthSyncIntervalMinutes { get; set; }
    public int GoogleHealthDefaultHistoryDays { get; set; }
    public int GoogleHealthMinimumHistoryDays { get; set; }
    public int GoogleHealthMaximumHistoryDays { get; set; }
    public string[] GoogleHealthReadScopes { get; set; } = [];
    public GoogleHealthCapability[] GoogleHealthCapabilities { get; set; } = [];
}
