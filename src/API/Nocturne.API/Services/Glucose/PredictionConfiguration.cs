using Microsoft.Extensions.Configuration;

namespace Nocturne.API.Services.Glucose;

/// <summary>
/// Configuration for <see cref="IPredictionService"/>. Bound from the
/// <c>Predictions</c> configuration section.
/// </summary>
public class PredictionOptions
{
    public const string SectionName = "Predictions";

    /// <summary>
    /// The source for glucose predictions. Defaults to <see cref="PredictionSource.DeviceStatus"/>
    /// so prediction curves uploaded by an AID system render without per-deployment configuration.
    /// Tenants with no recent uploaded curves get a 404 from the predictions endpoint (which the
    /// web client renders as no curve) and an empty forecast in alert evaluation.
    /// </summary>
    public PredictionSource Source { get; set; } = DefaultSource;

    /// <summary>
    /// The source used when <c>Predictions:Source</c> is absent from configuration.
    /// </summary>
    public const PredictionSource DefaultSource = PredictionSource.DeviceStatus;

    /// <summary>
    /// Reads <c>Predictions:Source</c>, falling back to <see cref="DefaultSource"/>.
    /// </summary>
    /// <remarks>
    /// Service registration and <c>PredictionController</c> both need the resolved source and must
    /// agree: registration decides whether an <see cref="IPredictionService"/> exists at all, and a
    /// disagreement would leave the controller returning 404 with a service registered, or vice versa.
    /// </remarks>
    public static PredictionSource ResolveSource(IConfiguration configuration) =>
        configuration.GetValue($"{SectionName}:{nameof(Source)}", DefaultSource);
}

/// <summary>
/// Determines where <see cref="IPredictionService"/> reads glucose predictions from.
/// </summary>
public enum PredictionSource
{
    /// <summary>
    /// Predictions are disabled. The endpoint will return a 404.
    /// </summary>
    None,

    /// <summary>
    /// Read predictions from the most recent DeviceStatus (AAPS, Trio, Loop).
    /// The AID system calculates predictions on-device and uploads them.
    /// </summary>
    DeviceStatus,

    /// <summary>
    /// Run the oref algorithm server-side via the external WASM module.
    /// Requires oref.wasm from nightscout/nocturne-heuristics-wasm.
    /// Only useful for MDI or non-opensource AID systems.
    /// </summary>
    OrefWasm,
}
