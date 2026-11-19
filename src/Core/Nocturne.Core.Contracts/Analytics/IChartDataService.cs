using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Analytics;

/// <summary>
/// Service for computing pre-calculated dashboard chart data.
/// Orchestrates profile loading, data fetching, IOB/COB calculation,
/// basal series construction, treatment categorization, and state span mapping.
/// </summary>
/// <seealso cref="IEntryService"/>
/// <seealso cref="ITreatmentService"/>
/// <seealso cref="Treatments.IIobCalculator"/>
public interface IChartDataService
{
    /// <summary>
    /// Build the complete dashboard chart data for a given time range.
    /// </summary>
    /// <param name="startTime">Start of the time range in Unix milliseconds</param>
    /// <param name="endTime">End of the time range in Unix milliseconds</param>
    /// <param name="intervalMinutes">Interval for IOB/COB series sampling (1-60)</param>
    /// <param name="includeHealthSeries">Whether to load the heart-rate and step series; when false both are empty</param>
    /// <returns>Fully populated dashboard chart data DTO</returns>
    Task<DashboardChartData> GetDashboardChartDataAsync(
        long startTime,
        long endTime,
        int intervalMinutes,
        bool includeHealthSeries,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Build the basal delivery series for a given time range without running the
    /// full IOB/COB compute pipeline.
    /// </summary>
    /// <param name="startTime">Start of the time range in Unix milliseconds</param>
    /// <param name="endTime">End of the time range in Unix milliseconds</param>
    /// <returns>Basal delivery points covering the requested window</returns>
    Task<List<BasalPoint>> GetBasalSeriesAsync(
        long startTime,
        long endTime,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Build the basal delivery spans overlapping a time range, as the dashboard chart draws them,
    /// without running the full IOB/COB compute pipeline.
    /// </summary>
    /// <param name="startTime">Start of the time range in Unix milliseconds</param>
    /// <param name="endTime">End of the time range in Unix milliseconds</param>
    /// <returns>Unclipped delivery spans: one that began before the range is returned whole</returns>
    Task<List<BasalDeliverySpanDto>> GetBasalDeliverySpansAsync(
        long startTime,
        long endTime,
        CancellationToken cancellationToken = default
    );
}
