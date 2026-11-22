using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.Core.Contracts.Alerts;

/// <summary>
/// Engine seam for replay (docs/alerts/engine-semantics.md §8): steps a rule set over a series of
/// pre-enriched ticks through the live driver sequence. Replay owns no persistent state: every
/// call starts from a fresh timer store and excursion tracker, and nothing it evaluates is
/// written anywhere.
/// </summary>
public interface IAlertReplayEngine
{
    /// <summary>Replays <see cref="AlertReplayInput.Rules"/> over <see cref="AlertReplayInput.Ticks"/> in order.</summary>
    /// <exception cref="ArgumentException">Two rules share an id.</exception>
    /// <exception cref="ReplayStepLimitException">A rule changes too often between two ticks.</exception>
    Task<AlertReplayRun> ReplayAsync(AlertReplayInput input, CancellationToken ct);
}

/// <param name="Rules">The rule set, in any order.</param>
/// <param name="Ticks">The ticks, in time order.</param>
/// <param name="IncludeTicks">Whether <see cref="AlertReplayRun.Ticks"/> reports every evaluation instant.</param>
/// <param name="Until">
/// The end of the window: the rules are stepped on past the last tick up to, not including, it.
/// </param>
public sealed record AlertReplayInput(
    IReadOnlyList<AlertRuleSnapshot> Rules,
    IReadOnlyList<AlertReplayTick> Ticks,
    bool IncludeTicks = false,
    DateTime? Until = null);

/// <param name="At">The tick instant, UTC.</param>
/// <param name="Context">
/// The context enriched as of <paramref name="At"/>. Its <see cref="SensorContext.ActiveAlerts"/>
/// are replaced by the replay's own.
/// </param>
/// <param name="SuppressedRuleIds">
/// The rules a fire opening on this tick, or on a step before the next tick, is recorded for as
/// suppressed by Do Not Disturb.
/// </param>
/// <param name="Reading">
/// A glucose reading arrived at <paramref name="At"/>, so every rule is evaluated; otherwise the
/// tick samples the other facts between readings.
/// </param>
public sealed record AlertReplayTick(
    DateTime At, SensorContext Context, IReadOnlySet<Guid> SuppressedRuleIds, bool Reading = true);

/// <summary>An excursion tracker transition (docs/alerts/engine-semantics.md §6).</summary>
public enum AlertReplayTransition
{
    Fired,
    SuppressedByDnd,
    HysteresisStarted,
    HysteresisResumed,

    /// <summary>The excursion closed when its hysteresis window ran out.</summary>
    Closed,
    AutoResolved,
}

public sealed record AlertReplayRunEvent(DateTime At, Guid RuleId, AlertReplayTransition Kind);

/// <summary>A rule's state after one evaluation.</summary>
/// <param name="Skipped">The body cannot be evaluated; <see cref="Met"/> and <see cref="Firing"/> are false.</param>
/// <param name="Unknown">The root was unknown; <see cref="Met"/> is then false.</param>
public sealed record AlertReplayRuleTick(Guid RuleId, bool Skipped, bool Met, bool Firing, bool Unknown = false);

/// <param name="Rules">In evaluation order.</param>
public sealed record AlertReplayTickOutcome(DateTime At, IReadOnlyList<AlertReplayRuleTick> Rules);

/// <param name="Order">Rule ids in evaluation order.</param>
/// <param name="Events">By instant, then evaluation order.</param>
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
