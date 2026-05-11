using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Models.Services;

namespace Nocturne.API.Controllers.V4.Analytics;

/// <summary>
/// Aggregated data overview for heatmap visualization.
/// Provides year-level availability, day-level record counts, average glucose, and monthly GRI scores.
/// </summary>
/// <remarks>
/// Responses are cached (300s for years and GRI timeline; 180s for daily summary)
/// to reduce database load when the heatmap re-renders.
/// </remarks>
/// <seealso cref="IDataOverviewService"/>
/// <seealso cref="DataOverviewYearsResponse"/>
/// <seealso cref="DailySummaryResponse"/>
/// <seealso cref="GriTimelineResponse"/>
[ApiController]
[Tags("Analytics")]
[Route("api/v4/year-overview")]
[Produces("application/json")]
[ClientPropertyName("dataOverview")]
public class DataOverviewController : ControllerBase
{
    private readonly IDataOverviewService _dataOverviewService;
    private readonly ILogger<DataOverviewController> _logger;

    public DataOverviewController(
        IDataOverviewService dataOverviewService,
        ILogger<DataOverviewController> logger
    )
    {
        _dataOverviewService = dataOverviewService;
        _logger = logger;
    }

    /// <summary>
    /// Gets the list of calendar years that contain glucose data and the available data sources.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="DataOverviewYearsResponse"/> with available years and data source names.</returns>
    [HttpGet("years")]
    [RemoteQuery]
    [ResponseCache(Duration = 300)]
    [ProducesResponseType(typeof(DataOverviewYearsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<DataOverviewYearsResponse>> GetAvailableYears(
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var result = await _dataOverviewService.GetAvailableYearsAsync(cancellationToken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting available years for data overview");
            return Problem(detail: "Internal server error", statusCode: 500, title: "Internal Server Error");
        }
    }

    /// <summary>
    /// Get day-level aggregated counts and average glucose for a given year
    /// </summary>
    /// <param name="year">The year to aggregate</param>
    /// <param name="dataSources">Optional data source filters (multiple allowed)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    [HttpGet("daily-summary")]
    [RemoteQuery]
    [ResponseCache(Duration = 180, VaryByQueryKeys = new[] { "*" })]
    [ProducesResponseType(typeof(DailySummaryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<DailySummaryResponse>> GetDailySummary(
        [FromQuery] int year,
        [FromQuery] string[]? dataSources = null,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (year < 1970 || year > 2100)
                return Problem(detail: "Year must be between 1970 and 2100", statusCode: 400, title: "Bad Request");

            // Filter out empty strings
            var cleanSources = dataSources?.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
            if (cleanSources is { Length: 0 })
                cleanSources = null;

            var result = await _dataOverviewService.GetDailySummaryAsync(
                year,
                cleanSources,
                cancellationToken
            );
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting daily summary for year {Year}", year);
            return Problem(detail: "Internal server error", statusCode: 500, title: "Internal Server Error");
        }
    }

    /// <summary>
    /// Get monthly GRI (Glycemic Risk Index) scores for a given year
    /// </summary>
    /// <param name="year">The year to compute GRI timeline for</param>
    /// <param name="dataSources">Optional data source filters (multiple allowed)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    [HttpGet("gri-timeline")]
    [RemoteQuery]
    [ResponseCache(Duration = 300, VaryByQueryKeys = new[] { "*" })]
    [ProducesResponseType(typeof(GriTimelineResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<GriTimelineResponse>> GetGriTimeline(
        [FromQuery] int year,
        [FromQuery] string[]? dataSources = null,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (year < 1970 || year > 2100)
                return Problem(detail: "Year must be between 1970 and 2100", statusCode: 400, title: "Bad Request");

            // Filter out empty strings
            var cleanSources = dataSources?.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
            if (cleanSources is { Length: 0 })
                cleanSources = null;

            var result = await _dataOverviewService.GetGriTimelineAsync(
                year,
                cleanSources,
                cancellationToken
            );
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting GRI timeline for year {Year}", year);
            return Problem(detail: "Internal server error", statusCode: 500, title: "Internal Server Error");
        }
    }
}
