using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenApi.Remote.Attributes;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Glucose;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Controllers.V4.Analytics;

/// <summary>
/// Controller for glucose forecast predictions.
/// Supports multiple prediction sources: <c>DeviceStatus</c> (AAPS/Trio/Loop loop data)
/// or <c>OrefWasm</c> (server-side oref calculation).
/// </summary>
/// <remarks>
/// The active prediction source is determined at startup from <c>Predictions:Source</c>
/// in application configuration. If the source is <c>None</c> or no
/// <see cref="IPredictionService"/> is registered, the endpoint returns <c>404 Not Found</c>
/// with an actionable error message.
///
/// </remarks>
/// <seealso cref="IPredictionService"/>
/// <seealso cref="GlucosePredictionResponse"/>
/// <seealso cref="PredictionStatusResponse"/>
[ApiController]
[Tags("Analytics")]
[Route("api/v4/predictions")]
[Produces("application/json")]
[ClientPropertyName("predictions")]
public class PredictionController : ControllerBase
{
    private readonly IPredictionService? _predictionService;
    private readonly IProfileSnapshotService _profileSnapshotService;
    private readonly PredictionSource _source;
    private readonly ILogger<PredictionController> _logger;

    public PredictionController(
        ILogger<PredictionController> logger,
        IConfiguration configuration,
        IProfileSnapshotService profileSnapshotService,
        IPredictionService? predictionService = null)
    {
        _predictionService = predictionService;
        _profileSnapshotService = profileSnapshotService;
        _source = PredictionOptions.ResolveSource(configuration);
        _logger = logger;
    }

    /// <summary>
    /// Get glucose predictions based on current data.
    /// Returns predicted glucose values for the next 4 hours in 5-minute intervals.
    /// </summary>
    /// <param name="profileId">Optional profile ID to use for predictions</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Glucose predictions including IOB, UAM, COB, and zero-temp curves</returns>
    [HttpGet]
    [RemoteQuery]
    [RequireScope(Scope.GlucoseRead, Scope.TreatmentsRead)]
    [ProducesResponseType(typeof(GlucosePredictionResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<ActionResult<GlucosePredictionResponse>> GetPredictions(
        [FromQuery] string? profileId = null,
        CancellationToken cancellationToken = default)
    {
        if (_predictionService == null || _source == PredictionSource.None)
        {
            return Problem(detail: "Predictions are not configured. Set Predictions:Source to DeviceStatus or OrefWasm.", statusCode: 404, title: "Not Found");
        }

        _logger.LogDebug("Getting glucose predictions (source: {Source}) for profile: {ProfileId}",
            _source, profileId ?? "default");

        try
        {
            var result = await _predictionService.GetPredictionsAsync(profileId, asOf: null, cancellationToken);
            return Ok(PredictionReadScopeGuard.Redact(result, HttpContext.GetGrantedScopes()));
        }
        catch (InvalidOperationException ex)
        {
            // No recent device-status prediction data — the normal state for care-portal-only
            // tenants now that DeviceStatus is the default source, so not a client fault.
            _logger.LogWarning(ex, "No prediction data available");
            return Problem(detail: ex.Message, statusCode: 404, title: "Not Found");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting predictions");
            return Problem(detail: "Failed to calculate predictions", statusCode: 500, title: "Internal Server Error");
        }
    }

    /// <summary>
    /// Check the status of the prediction service.
    /// </summary>
    /// <returns>Status of the prediction service including configured source</returns>
    [HttpGet("status")]
    [RemoteQuery]
    [RequireScope(Scope.GlucoseRead, Scope.TreatmentsRead)]
    [ProducesResponseType(typeof(PredictionStatusResponse), 200)]
    public ActionResult<PredictionStatusResponse> GetStatus()
    {
        return Ok(new PredictionStatusResponse
        {
            Available = _predictionService != null && _source != PredictionSource.None,
            Source = _source.ToString(),
        });
    }

    /// <summary>
    /// Get the pre-resolved therapy profile for the next 24 hours, flattened into contiguous
    /// absolute-time segments, for an offline on-device <c>oref</c> prediction run.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="GetPredictions"/>, this action is intentionally available regardless of
    /// <c>Predictions:Source</c>: the client runs oref on-device and needs its therapy profile
    /// precisely when server-side prediction is off (the offline case). It depends only on the
    /// unconditionally-registered profile resolvers, not on <see cref="IPredictionService"/>, so it
    /// carries no <c>_predictionService</c>/<c>_source</c> guard.
    ///
    /// Clients must additionally apply the fixed oref constants <c>max_iob=10</c>, <c>max_basal=4</c>,
    /// <c>max_daily_basal=2</c> (which <c>PredictionService.GetProfileAsync</c> hardcodes) to match a
    /// server-side oref run; they are not part of this payload.
    /// </remarks>
    /// <param name="profileId">Optional profile name. The device omits it (resolves the active profile).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("profile-snapshot")]
    [RemoteQuery]
    [RequireScope(Scope.TherapyRead)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(ProfileSnapshotResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<ActionResult<ProfileSnapshotResponse>> GetProfileSnapshot(
        [FromQuery] string? profileId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _profileSnapshotService.BuildAsync(profileId, cancellationToken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building profile snapshot for profile: {ProfileId}", profileId ?? "default");
            return Problem(detail: "Failed to build profile snapshot", statusCode: 500, title: "Internal Server Error");
        }
    }
}

/// <summary>
/// Status of the prediction service.
/// </summary>
public class PredictionStatusResponse
{
    /// <summary>Whether a prediction service is available</summary>
    public bool Available { get; set; }

    /// <summary>Configured prediction source (None, DeviceStatus, OrefWasm)</summary>
    public string Source { get; set; } = "None";
}
