using Nocturne.Core.Models.Widget;

namespace Nocturne.Core.Contracts;

/// <summary>
/// Service interface for widget-friendly summary data aggregation.
/// Provides essential diabetes management data optimized for mobile widgets, watch faces, and other constrained displays.
/// </summary>
public interface IWidgetSummaryService
{
    /// <summary>
    /// Gets a widget-friendly summary containing current glucose, history, IOB, COB, trackers, alarm state, and optionally predictions.
    /// </summary>
    /// <param name="userId">The user ID for tracker filtering</param>
    /// <param name="hours">Number of hours of history to include (0 for current reading only)</param>
    /// <param name="includePredictions">Whether to include predicted glucose values</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A summary response containing aggregated widget data</returns>
    Task<V4SummaryResponse> GetSummaryAsync(
        string userId,
        int hours = 0,
        bool includePredictions = false,
        CancellationToken cancellationToken = default
    );
}
