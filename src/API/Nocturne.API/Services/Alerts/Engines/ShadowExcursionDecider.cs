using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Engines;

/// <summary>
/// Shadow-mode <see cref="IExcursionDecider"/>: the managed decision is returned and persisted;
/// the Rust decision is made from the same state and only compared. Divergences are logged as
/// <c>AlertEngineDivergence</c>, and a Rust failure as <c>AlertEngineShadowError</c>, which
/// never reaches the caller.
/// </summary>
/// <param name="managed">The authoritative decider.</param>
/// <param name="rust">The decider compared against it.</param>
/// <param name="logger">Where divergences and failures are logged.</param>
internal sealed class ShadowExcursionDecider(
    IExcursionDecider managed,
    IExcursionDecider rust,
    ILogger<ShadowExcursionDecider> logger)
    : IExcursionDecider
{
    private const string Engine = "rust";

    public TrackerDecision Process(
        Guid ruleId, AlertTrackerState? state, TrackerConfig config, bool conditionMet, bool autoResolveMet,
        DateTime now) =>
        Shadow(ruleId, "tracker_process",
            managed.Process(ruleId, state, config, conditionMet, autoResolveMet, now),
            () => rust.Process(ruleId, state, config, conditionMet, autoResolveMet, now));

    public TrackerDecision ForceClose(
        Guid ruleId, AlertTrackerState? state, ExcursionCloseReason reason, DateTime now) =>
        Shadow(ruleId, "tracker_force_close",
            managed.ForceClose(ruleId, state, reason, now),
            () => rust.ForceClose(ruleId, state, reason, now));

    public TrackerDecision CloseElapsedHysteresis(
        Guid ruleId, AlertTrackerState? state, TrackerConfig config, DateTime now) =>
        Shadow(ruleId, "tracker_close_elapsed_hysteresis",
            managed.CloseElapsedHysteresis(ruleId, state, config, now),
            () => rust.CloseElapsedHysteresis(ruleId, state, config, now));

    private TrackerDecision Shadow(Guid ruleId, string operation, TrackerDecision decision, Func<TrackerDecision> shadow)
    {
        try
        {
            new ShadowComparison(logger, Engine, ruleId).Decisions(operation, decision, shadow());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "AlertEngineShadowError rule={RuleId} engine={Engine} stage={Stage}", ruleId, Engine, operation);
        }
        return decision;
    }
}
