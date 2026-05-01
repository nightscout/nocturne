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
/// </remarks>
public interface ISensorContextEnricher
{
    /// <summary>
    /// Returns a <see cref="SensorContext"/> derived from <paramref name="baseContext"/> with
    /// only the optional fields required by <paramref name="rules"/> populated.
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
}
