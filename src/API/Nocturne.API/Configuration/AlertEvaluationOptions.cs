namespace Nocturne.API.Configuration;

/// <summary>
/// Tunables for the alert evaluation pipeline. Bound to the <c>AlertEvaluation</c>
/// configuration section so deployments with non-standard upload cadences can override
/// freshness thresholds without recompiling.
/// </summary>
public sealed class AlertEvaluationOptions
{
    /// <summary>
    /// Configuration section name in appsettings.json.
    /// </summary>
    public const string SectionName = "AlertEvaluation";

    /// <summary>
    /// Maximum age (since now) of the latest <c>PumpSnapshot</c> before the
    /// active-pump-suspension projection is treated as unknown rather than current.
    /// Defaults to twice the typical AID upload cadence so a brief upload gap does not
    /// suppress a real suspension, but a sustained outage cannot latch the projection.
    /// </summary>
    public TimeSpan PumpFreshnessThreshold { get; set; } = TimeSpan.FromMinutes(10);
}
