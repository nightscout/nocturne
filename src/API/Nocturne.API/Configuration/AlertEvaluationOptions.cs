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

    /// <summary>
    /// Longest window a single replay call will cover. Replay re-evaluates every enabled rule
    /// on a 5-minute tick and re-enriches the tick context from the historical repositories, so
    /// both the work and the returned transition log grow linearly with the span. A request
    /// beyond this is rejected rather than truncated. The widest window a first-party caller
    /// asks for is a DST-stretched calendar day (25 h); the default leaves roughly double that
    /// headroom, and a deployment that wants longer debug replays raises it here.
    /// </summary>
    public TimeSpan MaxReplayWindow { get; set; } = TimeSpan.FromHours(48);

    /// <summary>
    /// Rows per page of the historical glucose fetch that feeds replay. The fetch is keyset-paged
    /// and streamed through the tick loop, so the resident reading set is bounded by this plus
    /// the rows of one held-back 5-minute bucket rather than by the window's total row count.
    /// A single bucket holding more rows than one page (bulk backfill, duplicate import) is
    /// flushed per page instead of held whole, trading exact bucket-winner resolution for the
    /// memory bound on a shape the live engine never produces. Values below 1 are treated as 1.
    /// </summary>
    public int ReplayGlucosePageSize { get; set; } = 2000;
}
