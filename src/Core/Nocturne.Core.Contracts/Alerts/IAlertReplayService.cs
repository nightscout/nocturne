using System.Collections.Immutable;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.Core.Contracts.Alerts;

/// <summary>
/// Replays a tenant's alert rules against historical glucose readings to show what alerts
/// <em>would</em> have fired had the current rule set been active. Used by the rule editor
/// to give the user feedback on rule sensitivity before committing.
/// </summary>
/// <remarks>
/// Replay is approximate by design. The live engine consumes IOB, COB, predictions, treatments,
/// pump events, and active-alert snapshots — most of which are not reconstructable retroactively
/// without large historical joins. Replay covers the common cases (threshold, sustained, trend,
/// time-of-day, staleness, alert_state-on-already-fired-rules) and surfaces the omissions in
/// <see cref="AlertReplayResult.Limitations"/> so callers can show a banner to the user.
/// </remarks>
public interface IAlertReplayService
{
    /// <summary>
    /// Replay enabled rules over a window. When <paramref name="localDate"/> is null, the
    /// window is the rolling last 24 hours from "now" in the requested timezone (or UTC if
    /// none provided). When set, the window is that calendar day, midnight-to-midnight in
    /// the same zone.
    /// </summary>
    Task<AlertReplayResult> ReplayAsync(
        DateOnly? localDate,
        string? timezone,
        CancellationToken ct);

    /// <summary>
    /// Replay variant for the rule editor. Runs the same simulation as
    /// <see cref="ReplayAsync"/> but with a single user-provided rule override layered in.
    /// When <paramref name="ruleOverride"/>'s <c>Id</c> matches an existing tenant rule, the
    /// override replaces it for the duration of the replay; when null/empty the override is
    /// appended (so authors can preview a rule before saving). Tenant DB state is never
    /// modified — the override lives in memory for one call.
    /// </summary>
    Task<AlertReplayResult> ReplayDryRunAsync(
        DateOnly? localDate,
        string? timezone,
        ReplayRuleOverride ruleOverride,
        CancellationToken ct);
}

/// <summary>
/// In-memory rule definition layered into a dry-run replay. Mirrors the editor's pre-save
/// shape. <see cref="Id"/> is optional: when present and matching an existing rule it
/// replaces it for the replay; otherwise the override is appended to the rule list.
/// </summary>
public record ReplayRuleOverride(
    Guid? Id,
    string Name,
    Nocturne.Core.Models.Alerts.AlertConditionType ConditionType,
    string ConditionParams,
    Nocturne.Core.Models.Alerts.AlertRuleSeverity Severity,
    bool AllowThroughDnd,
    bool AutoResolveEnabled,
    string? AutoResolveParams);

/// <summary>
/// A single point at which a rule transitioned from "not firing" to "firing" during replay.
/// Continuous-fire periods produce one event at the leading edge — re-fires after a clear
/// produce a second event. The replay does not attempt to model excursion close (hysteresis
/// is dropped from the new rule shape).
/// </summary>
public record AlertReplayEvent(
    DateTime At,
    Guid RuleId,
    string RuleName,
    AlertRuleSeverity Severity);

/// <summary>
/// Result of <see cref="IAlertReplayService.ReplayAsync"/>. Window timestamps are UTC; the
/// caller localises for display.
/// </summary>
public record AlertReplayResult(
    DateTime WindowStart,
    DateTime WindowEnd,
    IReadOnlyList<AlertReplayEvent> Events,
    string Limitations)
{
    /// <summary>
    /// Per-rule, per-leaf truth transition log captured during replay. Keyed by rule id;
    /// each <see cref="LeafTransitionLog"/> covers one leaf identified by the sequential
    /// id assigned via <see cref="LeafIdentity.AssignLeafIds"/>. Only transitions are
    /// stored — the first tick emits a baseline point so the FE can render the starting
    /// state without scanning. Empty by default for backward compatibility.
    /// </summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<LeafTransitionLog>> LeafTransitionsByRule { get; init; }
        = ImmutableDictionary<Guid, IReadOnlyList<LeafTransitionLog>>.Empty;
}
