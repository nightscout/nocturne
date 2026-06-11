using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Models.Analytics;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.Analytics;

/// <summary>
/// Detects clusters of physiologically-implausible ("noisy") CGM readings and the hypoglycemia
/// events they may have driven, over a tenant's data for a time window.
/// </summary>
/// <remarks>
/// Read-only, post-hoc analysis: it computes on the existing deduplicated glucose and insulin
/// streams and persists nothing. See <see cref="ISensorIntegrityService"/> and
/// <see cref="SensorIntegrityDetector"/>.
/// </remarks>
/// <seealso cref="ISensorIntegrityService"/>
[ApiController]
[Tags("Analytics")]
[Route("api/v4/sensor-integrity")]
[Produces("application/json")]
[Authorize]
public class SensorIntegrityController : ControllerBase
{
    private readonly ISensorIntegrityService _sensorIntegrityService;

    public SensorIntegrityController(ISensorIntegrityService sensorIntegrityService)
    {
        _sensorIntegrityService = sensorIntegrityService;
    }

    /// <summary>
    /// Analyze a UTC window for sensor-integrity clusters and cluster-linked hypo events.
    /// </summary>
    /// <param name="startDate">Inclusive UTC start of the window.</param>
    /// <param name="endDate">Exclusive UTC end of the window.</param>
    /// <param name="source">Optional data source filter; omitted analyzes the combined stream.</param>
    /// <param name="bySource">When <c>true</c>, also return a per-data-source breakdown.</param>
    /// <param name="minConfidence">Minimum cluster confidence for a hypo event to be reported.</param>
    /// <param name="requireInsulin">When <c>true</c>, only report hypo events with insulin dosed during the cluster.</param>
    /// <param name="hypoThresholdMgdl">Glucose level (mg/dL) below which a reading counts as hypo.</param>
    /// <param name="windowHours">Hours after a cluster to search for a hypo nadir.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The sensor-integrity report for the window.</returns>
    [HttpGet]
    [RemoteQuery]
    [ResponseCache(Duration = 60, VaryByQueryKeys = new[] { "*" })]
    public async Task<ActionResult<SensorIntegrityReport>> Analyze(
        [FromQuery] DateTime startDate,
        [FromQuery] DateTime endDate,
        [FromQuery] string? source = null,
        [FromQuery] bool bySource = false,
        [FromQuery] ClusterConfidence minConfidence = ClusterConfidence.Medium,
        [FromQuery] bool requireInsulin = false,
        [FromQuery] double hypoThresholdMgdl = 70.0,
        [FromQuery] double windowHours = 3.0,
        CancellationToken cancellationToken = default)
    {
        // Both bounds are required: an omitted DateTime binds to default(DateTime), which would
        // otherwise scan the tenant's entire history.
        if (startDate == default || endDate == default)
        {
            return BadRequest(new { error = "startDate and endDate are required." });
        }

        if (endDate <= startDate)
        {
            return BadRequest(new { error = "endDate must be after startDate." });
        }

        var hypoOptions = new HypoEventOptions
        {
            MinConfidence = minConfidence,
            RequireInsulin = requireInsulin,
            HypoThresholdMgdl = hypoThresholdMgdl,
            WindowHours = windowHours,
        };

        var report = await _sensorIntegrityService.AnalyzeAsync(
            startDate, endDate, source, bySource, hypoOptions, config: null, cancellationToken);

        return Ok(report);
    }
}
