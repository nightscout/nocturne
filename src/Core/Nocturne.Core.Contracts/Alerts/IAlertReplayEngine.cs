using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.Core.Contracts.Alerts;

/// <summary>
/// Engine seam for replay (docs/alerts/engine-semantics.md §8): re-evaluates a rule set over a
/// series of pre-enriched ticks. Selected by the same <c>Alerts:Engine</c> flag as
/// <see cref="IAlertEvaluationEngine"/>. Replay owns no persistent state: every call starts from a
/// fresh timer store and nothing it evaluates is written anywhere.
/// </summary>
public interface IAlertReplayEngine
{
    /// <summary>Replays <see cref="AlertReplayInput.Rules"/> over <see cref="AlertReplayInput.Ticks"/> in order.</summary>
    /// <exception cref="ArgumentException">Two rules share an id.</exception>
    Task<AlertReplayRun> ReplayAsync(AlertReplayInput input, CancellationToken ct);
}

/// <param name="Rules">The rule set, in any order.</param>
/// <param name="Ticks">The ticks, in time order.</param>
/// <param name="IncludeTicks">Whether <see cref="AlertReplayRun.Ticks"/> reports every rule's state on every tick.</param>
public sealed record AlertReplayInput(
    IReadOnlyList<AlertRuleSnapshot> Rules,
    IReadOnlyList<AlertReplayTick> Ticks,
    bool IncludeTicks = false);

/// <param name="At">The tick instant, UTC.</param>
/// <param name="Context">
/// The context enriched as of <paramref name="At"/>. Its <see cref="SensorContext.ActiveAlerts"/>
/// are replaced by the replay's own.
/// </param>
/// <param name="SuppressedRuleIds">
/// The rules a fire opening on this tick is recorded for as suppressed by Do Not Disturb.
/// </param>
public sealed record AlertReplayTick(DateTime At, SensorContext Context, IReadOnlySet<Guid> SuppressedRuleIds);

/// <summary>An edge of a rule's replay-local firing state.</summary>
public enum AlertReplayTransition
{
    Fired,
    SuppressedByDnd,
    AutoResolved,

    /// <summary>The body went false while firing.</summary>
    Cleared,
}

public sealed record AlertReplayRunEvent(DateTime At, Guid RuleId, AlertReplayTransition Kind);

/// <summary>A rule's state after one tick.</summary>
/// <param name="Skipped">The body cannot be evaluated; <see cref="Met"/> and <see cref="Firing"/> are false.</param>
public sealed record AlertReplayRuleTick(Guid RuleId, bool Skipped, bool Met, bool Firing);

/// <param name="Rules">In evaluation order.</param>
public sealed record AlertReplayTickOutcome(DateTime At, IReadOnlyList<AlertReplayRuleTick> Rules);

/// <param name="Order">Rule ids in evaluation order.</param>
/// <param name="Events">By tick, then evaluation order.</param>
/// <param name="LeafTransitions">
/// In evaluation order, leaves ascending by id; a rule skipped on every tick has none.
/// </param>
/// <param name="Ticks">Present when <see cref="AlertReplayInput.IncludeTicks"/> was set.</param>
public sealed record AlertReplayRun(
    IReadOnlyList<Guid> Order,
    IReadOnlyList<AlertReplayRunEvent> Events,
    IReadOnlyList<AlertReplayRuleLeafLog> LeafTransitions,
    IReadOnlyList<AlertReplayTickOutcome>? Ticks);

public sealed record AlertReplayRuleLeafLog(Guid RuleId, IReadOnlyList<LeafTransitionLog> Leaves);
