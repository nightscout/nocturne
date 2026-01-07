using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts;

/// <summary>
/// Service for monitoring pump status and generating alerts
/// Implements legacy Nightscout pump.js plugin functionality with 1:1 API compatibility
/// </summary>
public interface IPumpAlertService
{
    /// <summary>
    /// Gets pump preferences from extended settings
    /// Implements legacy pump.getPrefs()
    /// </summary>
    /// <param name="extendedSettings">Extended settings containing pump configuration</param>
    /// <param name="dayStart">Hour when day starts (for quiet night calculation)</param>
    /// <param name="dayEnd">Hour when day ends (for quiet night calculation)</param>
    /// <returns>Pump preferences with thresholds and display settings</returns>
    PumpPreferences GetPreferences(
        Dictionary<string, object?>? extendedSettings,
        double? dayStart = null,
        double? dayEnd = null
    );

    /// <summary>
    /// Analyzes device status to build comprehensive pump status result
    /// Implements legacy pump.setProperties() + prepareData()
    /// </summary>
    /// <param name="deviceStatuses">Recent device status entries</param>
    /// <param name="currentTime">Current timestamp in milliseconds for analysis</param>
    /// <param name="preferences">Pump preferences</param>
    /// <param name="profileService">Profile service for timezone</param>
    /// <param name="treatments">Recent treatments to check for offline markers</param>
    /// <returns>Pump status result with alert levels</returns>
    PumpStatusResult BuildPumpStatus(
        IEnumerable<DeviceStatus> deviceStatuses,
        long currentTime,
        PumpPreferences preferences,
        IProfileService profileService,
        IEnumerable<Treatment>? treatments = null
    );

    /// <summary>
    /// Checks if any pump alerts should be generated
    /// Implements legacy pump.checkNotifications()
    /// </summary>
    /// <param name="status">Current pump status result</param>
    /// <param name="preferences">Pump preferences</param>
    /// <param name="currentTime">Current timestamp in milliseconds</param>
    /// <param name="profileService">Profile service for timezone</param>
    /// <param name="treatments">Recent treatments to check for offline markers</param>
    /// <returns>Notification if alert threshold met, null otherwise</returns>
    NotificationBase? CheckNotifications(
        PumpStatusResult status,
        PumpPreferences preferences,
        long currentTime,
        IProfileService profileService,
        IEnumerable<Treatment>? treatments = null
    );

    /// <summary>
    /// Generates visualization data for pump pill display
    /// Implements legacy pump.updateVisualisation()
    /// </summary>
    /// <param name="status">Current pump status result</param>
    /// <param name="preferences">Pump preferences</param>
    /// <param name="isRetroMode">Whether in retro mode (affects which fields to show)</param>
    /// <param name="currentTime">Current timestamp in milliseconds</param>
    /// <param name="profileService">Profile service for timezone</param>
    /// <param name="treatments">Recent treatments to check for offline markers</param>
    /// <returns>Visualization data with values, info, label, and pillClass</returns>
    PumpVisualizationData GenerateVisualizationData(
        PumpStatusResult status,
        PumpPreferences preferences,
        bool isRetroMode,
        long currentTime,
        IProfileService profileService,
        IEnumerable<Treatment>? treatments = null
    );

    /// <summary>
    /// Handles virtual assistant "insulin remaining" request
    /// Implements legacy virtAsstReservoirHandler
    /// </summary>
    /// <param name="status">Current pump status result</param>
    /// <returns>Tuple of (title, response) for virtual assistant</returns>
    (string title, string response) HandleVirtualAssistantReservoir(PumpStatusResult status);

    /// <summary>
    /// Handles virtual assistant "pump battery" request
    /// Implements legacy virtAsstBatteryHandler
    /// </summary>
    /// <param name="status">Current pump status result</param>
    /// <returns>Tuple of (title, response) for virtual assistant</returns>
    (string title, string response) HandleVirtualAssistantBattery(PumpStatusResult status);
}

/// <summary>
/// Visualization data for pump pill display
/// </summary>
public class PumpVisualizationData
{
    /// <summary>
    /// Formatted value string to display in pill
    /// </summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// Additional info items to show in tooltip/popup
    /// </summary>
    public List<PumpInfoItem> Info { get; set; } = [];

    /// <summary>
    /// Label for the pill (e.g., "Pump")
    /// </summary>
    public string Label { get; set; } = "Pump";

    /// <summary>
    /// CSS class for pill styling (current, warn, urgent)
    /// </summary>
    public string PillClass { get; set; } = "current";
}

/// <summary>
/// Info item for pump visualization tooltip
/// </summary>
public class PumpInfoItem
{
    /// <summary>
    /// Label for the info item
    /// </summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Value for the info item
    /// </summary>
    public string Value { get; set; } = string.Empty;
}
