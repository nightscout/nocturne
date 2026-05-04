using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Monitoring;

/// <summary>
/// Service for monitoring pump status and generating alerts
/// Implements legacy Nightscout pump.js plugin functionality with 1:1 API compatibility
/// </summary>
public interface IPumpAlertService
{
    /// <summary>
    /// Gets pump preferences from extended settings
    /// </summary>
    PumpPreferences GetPreferences(
        Dictionary<string, object?>? extendedSettings,
        double? dayStart = null,
        double? dayEnd = null
    );

    /// <summary>
    /// Analyzes a pump snapshot to build comprehensive pump status result
    /// </summary>
    PumpStatusResult BuildPumpStatus(
        PumpSnapshot? pumpSnapshot,
        long currentTime,
        PumpPreferences preferences,
        IEnumerable<Treatment>? treatments = null
    );

    /// <summary>
    /// Checks if any pump alerts should be generated
    /// </summary>
    NotificationBase? CheckNotifications(
        PumpStatusResult status,
        PumpPreferences preferences,
        long currentTime,
        IEnumerable<Treatment>? treatments = null
    );

    /// <summary>
    /// Generates visualization data for pump pill display
    /// </summary>
    PumpVisualizationData GenerateVisualizationData(
        PumpStatusResult status,
        PumpPreferences preferences,
        bool isRetroMode,
        long currentTime,
        IEnumerable<Treatment>? treatments = null
    );

    /// <summary>
    /// Handles virtual assistant "insulin remaining" request
    /// </summary>
    (string title, string response) HandleVirtualAssistantReservoir(PumpStatusResult status);

    /// <summary>
    /// Handles virtual assistant "pump battery" request
    /// </summary>
    (string title, string response) HandleVirtualAssistantBattery(PumpStatusResult status);
}

/// <summary>
/// Visualization data for pump pill display
/// </summary>
public class PumpVisualizationData
{
    public string Value { get; set; } = string.Empty;
    public List<PumpInfoItem> Info { get; set; } = [];
    public string Label { get; set; } = "Pump";
    public string PillClass { get; set; } = "current";
}

/// <summary>
/// Info item for pump visualization tooltip
/// </summary>
public class PumpInfoItem
{
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
