using System.Collections.Immutable;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
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
/// time-of-day, staleness, alert_state-on-already-fired-rules); the FE owns the user-facing
/// limitations banner copy.
/// </remarks>
public interface IAlertReplayService
{
    /// <summary>
    /// Replay enabled rules over a window. When <paramref name="fromUtc"/>/<paramref name="toUtc"/>
    /// are both set, the window is exactly that absolute UTC range. Otherwise, when
    /// <paramref name="localDate"/> is null, the window is the rolling last 24 hours from
    /// "now"; when set, the window is that calendar day midnight-to-midnight in
    /// <paramref name="timezone"/> (UTC if none provided).
    /// </summary>
    Task<AlertReplayResult> ReplayAsync(
        DateOnly? localDate,
        string? timezone,
        DateTime? fromUtc,
        DateTime? toUtc,
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
        DateTime? fromUtc,
        DateTime? toUtc,
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
/// Discriminator for the per-rule timeline events surfaced by replay. Defaults to
/// <see cref="Fired"/> so existing callers reading this enum from a value-less event keep
/// their semantics. <see cref="AutoResolved"/> mirrors the live engine's
/// <c>resolution_reason="auto_resolve"</c> path; <see cref="SuppressedByDnd"/> mirrors the
/// "instance created, dispatch skipped" outcome from <c>HandleExcursionOpened</c>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AlertReplayEventKind>))]
public enum AlertReplayEventKind
{
    [EnumMember(Value = "fired"), JsonStringEnumMemberName("fired")]
    Fired = 0,

    [EnumMember(Value = "auto_resolved"), JsonStringEnumMemberName("auto_resolved")]
    AutoResolved = 1,

    [EnumMember(Value = "suppressed_by_dnd"), JsonStringEnumMemberName("suppressed_by_dnd")]
    SuppressedByDnd = 2,
}

/// <summary>
/// A single point on a rule's replay timeline. <see cref="Kind"/> distinguishes the leading
/// edge of firing, an auto-resolve close, and a fire that the live engine would have
/// suppressed under DND. Continuous-fire periods still produce one <see cref="Kind.Fired"/>
/// (or <see cref="Kind.SuppressedByDnd"/>) at the leading edge — re-fires after either a
/// natural clear or an auto-resolve produce a second event.
/// </summary>
public record AlertReplayEvent(
    DateTime At,
    Guid RuleId,
    string RuleName,
    AlertRuleSeverity Severity,
    AlertReplayEventKind Kind = AlertReplayEventKind.Fired);

/// <summary>
/// Result of <see cref="IAlertReplayService.ReplayAsync"/>. Window timestamps are UTC; the
/// caller localises for display.
/// </summary>
public record AlertReplayResult(
    DateTime WindowStart,
    DateTime WindowEnd,
    IReadOnlyList<AlertReplayEvent> Events)
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

    /// <summary>
    /// Per-tick numeric fact snapshots captured from <see cref="SensorContext"/> alongside
    /// rule evaluation. Keyed by snake_case fact name (see <see cref="ReplayFactKeys"/>);
    /// values are in the fact's natural unit so the FE can render them directly next to the
    /// matching condition leaf at the playhead (e.g. "Site age &lt; 3d · 1.2d"). Compressed
    /// the same way as the leaf log: only points where the rounded display value changed
    /// are emitted, with a baseline at first observation. Empty by default for backward
    /// compatibility.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<FactSnapshotPoint>> FactTimelines { get; init; }
        = ImmutableDictionary<string, IReadOnlyList<FactSnapshotPoint>>.Empty;
}
