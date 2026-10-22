using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Devices;

/// <summary>
/// Port of the legacy <c>openaps.js</c> plugin: loop analysis, visualisation and notifications.
/// </summary>
public interface IOpenApsService
{
    /// <summary>
    /// Port of legacy <c>getPrefs()</c>.
    /// </summary>
    /// <param name="extendedSettings">Extended settings containing OpenAPS configuration</param>
    OpenApsPreferences GetPreferences(Dictionary<string, object?> extendedSettings);

    /// <summary>
    /// Port of legacy <c>analyzeData()</c>.
    /// </summary>
    OpenApsAnalysisResult AnalyzeData(
        IEnumerable<ApsSnapshot> snapshots,
        DateTime currentTime,
        OpenApsPreferences preferences
    );

    /// <summary>
    /// Port of legacy <c>findOfflineMarker()</c>.
    /// </summary>
    /// <returns>Active offline marker or null if none found</returns>
    Treatment? FindOfflineMarker(IEnumerable<Treatment> treatments, DateTime currentTime);

    /// <summary>
    /// Port of legacy <c>checkNotifications()</c>.
    /// </summary>
    /// <param name="offlineMarker">Active offline marker if any</param>
    /// <returns>Notification level (NONE, WARN, URGENT)</returns>
    int CheckNotifications(
        OpenApsAnalysisResult analysisResult,
        OpenApsPreferences preferences,
        DateTime currentTime,
        Treatment? offlineMarker
    );

    /// <summary>
    /// Port of legacy <c>getEventTypes()</c>.
    /// </summary>
    /// <param name="units">Blood glucose units (mg/dl or mmol)</param>
    /// <returns>List of supported event types</returns>
    List<object> GetEventTypes(string units);

    /// <summary>
    /// Port of legacy <c>updateVisualisation()</c>.
    /// </summary>
    object GenerateVisualizationData(
        OpenApsAnalysisResult analysisResult,
        OpenApsPreferences preferences,
        bool isRetroMode,
        DateTime currentTime
    );

    /// <summary>
    /// Port of legacy <c>getForecastPoints()</c>.
    /// </summary>
    /// <param name="preferences">OpenAPS preferences for colors</param>
    /// <returns>List of forecast points for visualization</returns>
    List<OpenApsForecastPoint> GenerateForecastPoints(
        OpenApsPredBg? predictionData,
        OpenApsPreferences preferences,
        DateTime currentTime
    );

    /// <summary>
    /// Port of legacy <c>virtAsstForecastHandler()</c>.
    /// </summary>
    (string title, string response) HandleVirtualAssistantForecast(
        OpenApsAnalysisResult analysisResult
    );

    /// <summary>
    /// Port of legacy <c>virtAsstLastLoopHandler()</c>.
    /// </summary>
    (string title, string response) HandleVirtualAssistantLastLoop(
        OpenApsAnalysisResult analysisResult,
        DateTime currentTime
    );
}
