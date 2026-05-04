using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Devices;

/// <summary>
/// Service interface for OpenAPS operations with 1:1 legacy JavaScript compatibility
/// Handles OpenAPS loop data analysis, visualization, and notifications
/// Based on the legacy openaps.js implementation
/// </summary>
public interface IOpenApsService
{
    /// <summary>
    /// Gets OpenAPS preferences from extended settings
    /// Implements the legacy getPrefs() functionality with 1:1 compatibility
    /// </summary>
    /// <param name="extendedSettings">Extended settings containing OpenAPS configuration</param>
    /// <returns>OpenAPS preferences</returns>
    OpenApsPreferences GetPreferences(Dictionary<string, object?> extendedSettings);

    /// <summary>
    /// Analyzes APS snapshot data to determine OpenAPS loop status.
    /// Implements the legacy analyzeData() functionality with 1:1 compatibility.
    /// </summary>
    /// <param name="snapshots">Recent APS snapshots.</param>
    /// <param name="currentTime">Current timestamp for analysis.</param>
    /// <param name="preferences">OpenAPS preferences.</param>
    /// <returns>OpenAPS analysis result.</returns>
    OpenApsAnalysisResult AnalyzeData(
        IEnumerable<ApsSnapshot> snapshots,
        DateTime currentTime,
        OpenApsPreferences preferences
    );

    /// <summary>
    /// Finds active OpenAPS offline marker
    /// Implements the legacy findOfflineMarker() functionality with 1:1 compatibility
    /// </summary>
    /// <param name="treatments">Treatment entries to search</param>
    /// <param name="currentTime">Current timestamp</param>
    /// <returns>Active offline marker or null if none found</returns>
    Treatment? FindOfflineMarker(IEnumerable<Treatment> treatments, DateTime currentTime);

    /// <summary>
    /// Checks for OpenAPS notification conditions
    /// Implements the legacy checkNotifications() functionality with 1:1 compatibility
    /// </summary>
    /// <param name="analysisResult">OpenAPS analysis result</param>
    /// <param name="preferences">OpenAPS preferences</param>
    /// <param name="currentTime">Current timestamp</param>
    /// <param name="offlineMarker">Active offline marker if any</param>
    /// <returns>Notification level (NONE, WARN, URGENT)</returns>
    int CheckNotifications(
        OpenApsAnalysisResult analysisResult,
        OpenApsPreferences preferences,
        DateTime currentTime,
        Treatment? offlineMarker
    );

    /// <summary>
    /// Gets event types for care portal integration
    /// Implements the legacy getEventTypes() functionality with 1:1 compatibility
    /// </summary>
    /// <param name="units">Blood glucose units (mg/dl or mmol)</param>
    /// <returns>List of supported event types</returns>
    List<object> GetEventTypes(string units);

    /// <summary>
    /// Generates visualization data for OpenAPS status
    /// Implements the legacy updateVisualisation() functionality with 1:1 compatibility
    /// </summary>
    /// <param name="analysisResult">OpenAPS analysis result</param>
    /// <param name="preferences">OpenAPS preferences</param>
    /// <param name="isRetroMode">Whether in retro mode</param>
    /// <param name="currentTime">Current timestamp</param>
    /// <returns>Visualization data</returns>
    object GenerateVisualizationData(
        OpenApsAnalysisResult analysisResult,
        OpenApsPreferences preferences,
        bool isRetroMode,
        DateTime currentTime
    );

    /// <summary>
    /// Generates forecast points for blood glucose prediction
    /// Implements the legacy getForecastPoints() functionality with 1:1 compatibility
    /// </summary>
    /// <param name="predictionData">OpenAPS prediction data</param>
    /// <param name="preferences">OpenAPS preferences for colors</param>
    /// <param name="currentTime">Current timestamp</param>
    /// <returns>List of forecast points for visualization</returns>
    List<OpenApsForecastPoint> GenerateForecastPoints(
        OpenApsPredBg? predictionData,
        OpenApsPreferences preferences,
        DateTime currentTime
    );

    /// <summary>
    /// Handles virtual assistant forecast request
    /// Implements the legacy virtAsstForecastHandler() functionality with 1:1 compatibility
    /// </summary>
    /// <param name="analysisResult">OpenAPS analysis result</param>
    /// <returns>Virtual assistant response</returns>
    (string title, string response) HandleVirtualAssistantForecast(
        OpenApsAnalysisResult analysisResult
    );

    /// <summary>
    /// Handles virtual assistant last loop request
    /// Implements the legacy virtAsstLastLoopHandler() functionality with 1:1 compatibility
    /// </summary>
    /// <param name="analysisResult">OpenAPS analysis result</param>
    /// <param name="currentTime">Current timestamp</param>
    /// <returns>Virtual assistant response</returns>
    (string title, string response) HandleVirtualAssistantLastLoop(
        OpenApsAnalysisResult analysisResult,
        DateTime currentTime
    );
}
