using Nocturne.Core.Models.Widget;
using Nocturne.Core.Contracts.Entries;

namespace Nocturne.Core.Contracts.Analytics;

/// <summary>
/// Service interface for widget-friendly summary data aggregation.
/// Provides essential diabetes management data optimized for mobile widgets, watch faces, and other constrained displays.
/// </summary>
/// <seealso cref="ISummaryService"/>
/// <seealso cref="IEntryService"/>
public interface IWidgetSummaryService
{
    /// <summary>
    /// Gets a widget-friendly summary containing current glucose, history, IOB, COB, trackers, alarm state, and optionally predictions.
    /// </summary>
    /// <param name="hours">Number of hours of history to include (0 for current reading only)</param>
    /// <param name="includePredictions">Whether to include predicted glucose values</param>
    /// <returns>A summary response containing aggregated widget data</returns>
    Task<V4SummaryResponse> GetSummaryAsync(
        int hours = 0,
        bool includePredictions = false,
        CancellationToken cancellationToken = default
    );
}
