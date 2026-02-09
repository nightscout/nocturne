using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts;

/// <summary>
/// Service for computing pre-calculated dashboard chart data.
/// Orchestrates profile loading, data fetching, IOB/COB calculation,
/// basal series construction, treatment categorization, and state span mapping.
/// </summary>
public interface IChartDataService
{
    /// <summary>
    /// Build the complete dashboard chart data for a given time range.
    /// </summary>
    /// <param name="startTime">Start of the time range in Unix milliseconds</param>
    /// <param name="endTime">End of the time range in Unix milliseconds</param>
    /// <param name="intervalMinutes">Interval for IOB/COB series sampling (1-60)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Fully populated dashboard chart data DTO</returns>
    Task<DashboardChartData> GetDashboardChartDataAsync(
        long startTime,
        long endTime,
        int intervalMinutes,
        CancellationToken cancellationToken = default
    );
}
