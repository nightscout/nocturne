using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Controllers.V4.Analytics;

/// <summary>
/// Usage analytics collection for the tenant. Callers can read the collection status and metrics,
/// check the pending data, change the settings, read the privacy information and record an event.
/// Deleting the data removes all collected analytics data.
/// </summary>
/// <remarks>
/// All analytics data is stored locally and never transmitted externally.
/// Collection is opt-in and disabled by default. Users can call <c>GET /privacy</c>
/// to review the full data-collection policy, or <c>DELETE /data</c> to wipe collected data.
/// Configuration changes are persisted via <see cref="IAnalyticsService.UpdateAnalyticsConfigAsync"/>.
/// </remarks>
/// <seealso cref="IAnalyticsService"/>
[Authorize]
[ApiController]
[System.ComponentModel.DisplayName("Usage Analytics")]
[Tags("Analytics")]
[Route("api/v4/[controller]")]
[Produces("application/json")]
// Usage telemetry and its collection switch are a tenant-wide operational setting, not a data
// category: nothing here is per-subject and the payloads carry no PHI, so the gate is the same
// permission TenantSettingsController checks rather than any health-data scope.
[RequireScope(Scope.TenantSettings)]
public class AnalyticsController : ControllerBase
{
    private readonly IAnalyticsService _analyticsService;
    private readonly ILogger<AnalyticsController> _logger;

    public AnalyticsController(
        IAnalyticsService analyticsService,
        ILogger<AnalyticsController> logger
    )
    {
        _analyticsService = analyticsService;
        _logger = logger;
    }

    /// <summary>
    /// Gets the current analytics configuration and collection status.
    /// </summary>
    /// <returns>An object containing whether collection is enabled, the current <see cref="AnalyticsCollectionConfig"/>,
    /// system info, and a human-readable privacy note.</returns>
    /// <exception cref="Exception">Returns HTTP 500 if the analytics service throws unexpectedly.</exception>
    [HttpGet("status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    [ErrorEnvelope]
    public ActionResult<object> GetAnalyticsStatus()
    {
        var config = _analyticsService.GetAnalyticsConfig();
        var systemInfo = _analyticsService.GetSystemInfo();

        return Ok(
            new
            {
                enabled = _analyticsService.IsAnalyticsEnabled(),
                configuration = config,
                system_info = systemInfo,
                message = "Analytics collects local usage data for system monitoring. No data is transmitted externally.",
            }
        );
    }

    /// <summary>
    /// Gets current system performance metrics such as request latencies and memory usage.
    /// </summary>
    /// <returns>A <see cref="PerformanceMetrics"/> snapshot.</returns>
    /// <exception cref="Exception">Returns HTTP 500 if metrics cannot be retrieved.</exception>
    [HttpGet("metrics/performance")]
    [ProducesResponseType(typeof(PerformanceMetrics), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    [ErrorEnvelope]
    public ActionResult<PerformanceMetrics> GetPerformanceMetrics()
    {
        var metrics = _analyticsService.GetPerformanceMetrics();
        return Ok(metrics);
    }

    /// <summary>
    /// Gets current usage statistics such as endpoint hit counts and feature usage.
    /// </summary>
    /// <returns>A <see cref="UsageStatistics"/> snapshot.</returns>
    /// <exception cref="Exception">Returns HTTP 500 if statistics cannot be retrieved.</exception>
    [HttpGet("metrics/usage")]
    [ProducesResponseType(typeof(UsageStatistics), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    [ErrorEnvelope]
    public ActionResult<UsageStatistics> GetUsageStatistics()
    {
        var stats = _analyticsService.GetUsageStatistics();
        return Ok(stats);
    }

    /// <summary>
    /// Gets any pending analytics data queued for collection, returned for transparency.
    /// Since data is stored locally only, this is informational.
    /// </summary>
    /// <returns>An <see cref="AnalyticsBatch"/> if there is pending data, or a no-data message if none.</returns>
    /// <exception cref="Exception">Returns HTTP 500 if the data cannot be retrieved.</exception>
    [HttpGet("data/pending")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    [ErrorEnvelope]
    public async Task<ActionResult<AnalyticsBatch?>> GetPendingAnalyticsData()
    {
        var pendingData = await _analyticsService.GetPendingAnalyticsDataAsync();

        if (pendingData == null)
        {
            return Ok(new { message = "No pending analytics data", data = (object?)null });
        }

        return Ok(new { message = "Pending analytics data", data = pendingData });
    }

    /// <summary>
    /// Updates analytics collection configuration.
    /// </summary>
    /// <param name="config">The new <see cref="AnalyticsCollectionConfig"/> to persist.</param>
    /// <returns>The updated configuration and a confirmation message.</returns>
    /// <exception cref="Exception">Returns HTTP 500 if the update fails.</exception>
    [HttpPut("config")]
    [ProducesResponseType(typeof(AnalyticsCollectionConfig), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    [ErrorEnvelope]
    public async Task<ActionResult<AnalyticsCollectionConfig>> UpdateAnalyticsConfig(
        [FromBody] AnalyticsCollectionConfig config
    )
    {
        await _analyticsService.UpdateAnalyticsConfigAsync(config);

        _logger.LogInformation("Analytics configuration updated");

        return Ok(
            new
            {
                message = "Analytics configuration updated successfully",
                configuration = config,
            }
        );
    }

    /// <summary>
    /// Clears all locally stored analytics data. This action cannot be undone.
    /// </summary>
    /// <returns>A confirmation message on success.</returns>
    /// <exception cref="Exception">Returns HTTP 500 if the clear operation fails.</exception>
    [HttpDelete("data")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    [ErrorEnvelope]
    public async Task<ActionResult> ClearAnalyticsData()
    {
        await _analyticsService.ClearAnalyticsDataAsync();

        _logger.LogInformation("Analytics data cleared by user request");

        return Ok(new { message = "All analytics data has been cleared" });
    }

    /// <summary>
    /// Get information about what data is collected and privacy policy
    /// </summary>
    /// <returns>Privacy and data collection information</returns>
    [HttpGet("privacy")]
    public ActionResult GetPrivacyInformation()
    {
        return Ok(
            new
            {
                data_collection_policy = new
                {
                    purpose = "Local usage analytics to monitor system performance",
                    data_collected = new[]
                    {
                        "API endpoint usage patterns (anonymized)",
                        "Feature usage statistics",
                        "System performance metrics",
                        "Error rates and system health",
                        "Browser/device type (anonymized)",
                        "Page navigation patterns",
                    },
                    data_never_collected = new[]
                    {
                        "Medical data (glucose values, insulin doses, treatments)",
                        "Personal identifiers or usernames",
                        "API secrets or credentials",
                        "IP addresses or location data",
                        "Specific timestamps that could identify individuals",
                        "Any data that could be used to identify specific users",
                    },
                    retention = "Data is stored locally only. Not transmitted externally.",
                    opt_in = "Analytics is opt-in and disabled by default",
                    storage = "All data remains on your local system",
                    transparency = "You can view collected data at any time through the API",
                },
                endpoints = new
                {
                    view_status = "/api/analytics/status",
                    view_pending_data = "/api/analytics/data/pending",
                    configure_collection = "/api/analytics/config",
                    clear_data = "/api/analytics/data",
                    disable_collection = "Set 'Analytics.Enabled: false' in configuration",
                },
            }
        );
    }

    /// <summary>
    /// Tracks a custom <see cref="AnalyticsEvent"/>. Useful for integration testing or manually
    /// recording discrete events. Returns <c>400 Bad Request</c> if analytics collection is disabled.
    /// </summary>
    /// <param name="eventData">The custom <see cref="AnalyticsEvent"/> to record.</param>
    /// <returns>A confirmation message on success.</returns>
    /// <exception cref="Exception">Returns HTTP 500 if the event cannot be tracked.</exception>
    [HttpPost("events")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    [ErrorEnvelope]
    public async Task<ActionResult> TrackCustomEvent([FromBody] AnalyticsEvent eventData)
    {
        if (!_analyticsService.IsAnalyticsEnabled())
        {
            return Problem(detail: "Analytics collection is disabled", statusCode: 400, title: "Bad Request");
        }

        await _analyticsService.TrackCustomEventAsync(eventData);

        return Ok(new { message = "Custom event tracked successfully" });
    }
}
