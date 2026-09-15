using Nocturne.Core.Models.Services;

namespace Nocturne.Core.Contracts.Analytics;

/// <summary>
/// Service for aggregating data overview statistics across all data types
/// </summary>
public interface IDataOverviewService
{
    /// <summary>
    /// Get the list of years that contain data and available data sources
    /// </summary>
    Task<DataOverviewYearsResponse> GetAvailableYearsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get day-level aggregated counts and average glucose for a given year
    /// </summary>
    /// <param name="year">The year to aggregate</param>
    /// <param name="dataSources">Optional data source filters</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<DailySummaryResponse> GetDailySummaryAsync(int year, string[]? dataSources = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get monthly GRI (Glycemic Risk Index) scores for a given year
    /// </summary>
    /// <param name="year">The year to compute GRI timeline for</param>
    /// <param name="dataSources">Optional data source filters</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<GriTimelineResponse> GetGriTimelineAsync(int year, string[]? dataSources = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the estimated-HbA1c timeline for a given year: one point per day whose trailing
    /// 90-day glucose window has enough readings, each day's contribution weighted by recency
    /// so the estimate tracks how a lab HbA1c reflects glucose exposure.
    /// </summary>
    /// <param name="year">The year to compute the eHbA1c timeline for</param>
    /// <param name="dataSources">Optional data source filters</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<EHbA1cTimelineResponse> GetEHbA1cTimelineAsync(int year, string[]? dataSources = null, CancellationToken cancellationToken = default);
}
