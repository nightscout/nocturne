using Nocturne.API.Controllers.V4;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.API.Services.Devices;

namespace Nocturne.API.Services.Glucose;

/// <summary>
/// Produces forward glucose predictions from current CGM and treatment data.
/// The active prediction source is configured via <see cref="PredictionOptions"/>:
/// <see cref="PredictionSource.DeviceStatus"/> reads AID-computed predictions from the latest
/// device status upload, while <see cref="PredictionSource.OrefWasm"/> runs the oref algorithm
/// server-side via <see cref="OrefWasmService"/>. <see cref="PredictionSource.None"/> causes
/// the endpoint to return a 404.
/// </summary>
/// <seealso cref="PredictionService"/>
public interface IPredictionService
{
    /// <summary>
    /// Returns glucose predictions based on the configured prediction source. When
    /// <paramref name="asOf"/> is set, every "now" reference inside the implementation —
    /// the response timestamp, the upper bound on glucose / treatments / device-status
    /// snapshots, the moment passed to the profile resolvers, and the time anchor handed
    /// to oref — is pinned to that instant. Used by <c>AlertReplayService</c> so a
    /// <c>predicted</c> rule can be evaluated at historical replay ticks against the
    /// state the user actually had at that tick. <c>null</c> preserves the live behavior
    /// (anchored at <see cref="DateTimeOffset.UtcNow"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when no glucose readings or device status data are available.</exception>
    Task<GlucosePredictionResponse> GetPredictionsAsync(
        string? profileId = null,
        DateTimeOffset? asOf = null,
        CancellationToken cancellationToken = default);
}
