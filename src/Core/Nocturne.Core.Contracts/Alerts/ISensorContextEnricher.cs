using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Alerts;

/// <summary>
/// Populates the optional fields of a <see cref="SensorContext"/> (IOB, COB, predictions,
/// reservoir, site/sensor age, trend bucket, and active-alert snapshots) before evaluation.
/// </summary>
/// <remarks>
/// The orchestrator hands the enricher the base context (latest reading, trend rate, last
/// reading time) along with the rules being evaluated this pass. The enricher walks the
/// rules' condition trees, decides which optional fields any rule actually needs, and then
/// fetches only those — IOB/COB/predictions/reservoir/site-age/sensor-age/active-alerts plus
/// the looping facts (APS cycle/enaction timestamps, pump/uploader status, active temp basal,
/// active override, sensitivity ratio) — from their respective sources. A rule set that only
/// consults BG and trend triggers no downstream fetches; the trend bucket is derived from the
/// existing <see cref="SensorContext.TrendRate"/>.
///
/// Two entry points: <see cref="EnrichAsync"/> populates state as-of "now" (live alert
/// engine path); <see cref="EnrichAsOfAsync"/> pins every fetch to a historical timestamp,
/// powering the replay walker. Live <see cref="EnrichAsync"/> simply delegates to
/// <see cref="EnrichAsOfAsync"/> with the current time.
/// </remarks>
public interface ISensorContextEnricher
{
    /// <summary>
    /// Returns a <see cref="SensorContext"/> derived from <paramref name="baseContext"/> with
    /// only the optional fields required by <paramref name="rules"/> populated, evaluated at
    /// the current time.
    /// </summary>
    /// <param name="baseContext">Base context already containing <see cref="SensorContext.LatestValue"/>,
    /// <see cref="SensorContext.LatestTimestamp"/>, <see cref="SensorContext.TrendRate"/>, and
    /// <see cref="SensorContext.LastReadingAt"/>.</param>
    /// <param name="rules">Enabled rules being evaluated this pass; their condition trees are walked
    /// to determine which optional fields to populate.</param>
    /// <param name="tenantId">Tenant identifier for tenant-scoped fetches (e.g. active alert snapshots).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<SensorContext> EnrichAsync(
        SensorContext baseContext,
        IEnumerable<AlertRuleSnapshot> rules,
        Guid tenantId,
        CancellationToken ct);

    /// <summary>
    /// Replay-aware variant: every fetch is pinned to <paramref name="asOf"/>. Used by
    /// <c>AlertReplayService</c> to reconstruct what the engine would have seen at a past tick.
    /// </summary>
    /// <remarks>
    /// Differences from <see cref="EnrichAsync"/>:
    /// <list type="bullet">
    /// <item>APS / pump / uploader / temp-basal / state-span fetches use their as-of overloads
    /// (filtering to records with timestamp ≤ <paramref name="asOf"/>).</item>
    /// <item>IOB/COB treatments are sliced to those ending at-or-before <paramref name="asOf"/>.</item>
    /// <item>Predictions are produced by re-running the prediction pipeline with
    /// <paramref name="asOf"/> threaded through every input (glucose readings ≤ asOf,
    /// 24h treatment window ending at asOf, profile resolved at asOf, oref's
    /// <c>currentTimeMillis</c> set to asOf). The forecast is the curve the user would have
    /// had at that tick.</item>
    /// <item>The active-alerts snapshot is taken from <see cref="SensorContext.ActiveAlerts"/>
    /// on <paramref name="baseContext"/>; the enricher does not call the alert repository.
    /// The replay walker computes its own running set across the replay window.</item>
    /// </list>
    /// </remarks>
    /// <param name="baseContext">Per-tick base context. <see cref="SensorContext.ActiveAlerts"/>,
    /// when set, is preserved through to the returned context.</param>
    /// <param name="rules">Rules being evaluated this tick.</param>
    /// <param name="tenantId">Tenant identifier (currently unused on the replay path because
    /// <c>ActiveAlerts</c> comes from <paramref name="baseContext"/>; reserved for future
    /// tenant-scoped fetches).</param>
    /// <param name="asOf">Replay timestamp; every fact in the returned context reflects state
    /// at this instant. UTC.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<SensorContext> EnrichAsOfAsync(
        SensorContext baseContext,
        IEnumerable<AlertRuleSnapshot> rules,
        Guid tenantId,
        DateTime asOf,
        CancellationToken ct);
}
