using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenApi.Remote.Attributes;
using Nocturne.API.Services.Glucose;

namespace Nocturne.API.Controllers.V4.Analytics;

/// <summary>
/// Controller for glucose forecast predictions.
/// Supports multiple prediction sources: <c>DeviceStatus</c> (AAPS/Trio/Loop loop data)
/// or <c>OrefWasm</c> (server-side oref0 WASM calculation).
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
[Route("api/v4/predictions")]
[Produces("application/json")]
[ClientPropertyName("predictions")]
public class PredictionController : ControllerBase
{
    private readonly IPredictionService? _predictionService;
    private readonly PredictionSource _source;
    private readonly ILogger<PredictionController> _logger;

    public PredictionController(
        ILogger<PredictionController> logger,
        IConfiguration configuration,
        IPredictionService? predictionService = null)
    {
        _predictionService = predictionService;
        _source = configuration.GetValue<PredictionSource>("Predictions:Source", PredictionSource.None);
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
    [ProducesResponseType(typeof(GlucosePredictionResponse), 200)]
    [ProducesResponseType(typeof(PredictionErrorResponse), 400)]
    [ProducesResponseType(typeof(PredictionErrorResponse), 404)]
    [ProducesResponseType(typeof(PredictionErrorResponse), 500)]
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
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Invalid operation for predictions");
            return Problem(detail: ex.Message, statusCode: 400, title: "Bad Request");
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
    [ProducesResponseType(typeof(PredictionStatusResponse), 200)]
    public ActionResult<PredictionStatusResponse> GetStatus()
    {
        return Ok(new PredictionStatusResponse
        {
            Available = _predictionService != null && _source != PredictionSource.None,
            Source = _source.ToString(),
        });
    }
}

/// <summary>
/// Response containing glucose predictions.
/// </summary>
public class GlucosePredictionResponse
{
    /// <summary>Timestamp when predictions were calculated</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>Current blood glucose (mg/dL)</summary>
    public double CurrentBg { get; set; }

    /// <summary>Rate of glucose change (mg/dL per 5 min)</summary>
    public double Delta { get; set; }

    /// <summary>Eventual blood glucose if trend continues (mg/dL)</summary>
    public double EventualBg { get; set; }

    /// <summary>Current insulin on board (U)</summary>
    public double Iob { get; set; }

    /// <summary>Current carbs on board (g)</summary>
    public double Cob { get; set; }

    /// <summary>Sensitivity ratio used (1.0 = normal)</summary>
    public double? SensitivityRatio { get; set; }

    /// <summary>Prediction interval in minutes</summary>
    public int IntervalMinutes { get; set; } = 5;

    /// <summary>Prediction curves with different scenarios</summary>
    public PredictionCurves Predictions { get; set; } = new();
}

/// <summary>
/// Different prediction curves for visualization.
/// </summary>
public class PredictionCurves
{
    /// <summary>Main prediction curve (mg/dL values at 5-min intervals)</summary>
    public List<double>? Default { get; set; }

    /// <summary>IOB-only prediction (ignoring COB)</summary>
    public List<double>? IobOnly { get; set; }

    /// <summary>UAM (Unannounced Meal) prediction</summary>
    public List<double>? Uam { get; set; }

    /// <summary>COB-based prediction</summary>
    public List<double>? Cob { get; set; }

    /// <summary>Zero-temp prediction (what happens if basal stops)</summary>
    public List<double>? ZeroTemp { get; set; }
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

/// <summary>
/// Error response for prediction failures.
/// </summary>
public class PredictionErrorResponse
{
    /// <summary>Error message</summary>
    public string Error { get; set; } = string.Empty;
}
