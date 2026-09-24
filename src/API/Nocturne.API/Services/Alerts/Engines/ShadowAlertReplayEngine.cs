using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Engines;

/// <summary>
/// Shadow-mode <see cref="IAlertReplayEngine"/>: the managed replay answers, then the Rust replay
/// runs over the same input and every difference is logged as an <c>AlertEngineDivergence</c>.
/// A Rust failure is logged and never reaches the caller.
/// </summary>
internal sealed class ShadowAlertReplayEngine(
    IAlertReplayEngine managed,
    IAlertReplayEngine shadow,
    ILogger<ShadowAlertReplayEngine> logger) : IAlertReplayEngine
{
    private const string Engine = "rust";

    public async Task<AlertReplayRun> ReplayAsync(AlertReplayInput input, CancellationToken ct)
    {
        var managedRun = await managed.ReplayAsync(input, ct);

        AlertReplayRun shadowRun;
        try
        {
            shadowRun = await shadow.ReplayAsync(input, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "AlertEngineShadowError replay engine={Engine}", Engine);
            return managedRun;
        }

        Compare(managedRun, shadowRun);
        return managedRun;
    }

    private void Compare(AlertReplayRun managedRun, AlertReplayRun shadowRun)
    {
        if (!managedRun.Order.SequenceEqual(shadowRun.Order))
            Diverged(ruleId: null, "replay.order", string.Join(',', managedRun.Order), string.Join(',', shadowRun.Order));

        var ruleIds = managedRun.Order.Union(shadowRun.Order);
        var managedEvents = EventsByRule(managedRun);
        var shadowEvents = EventsByRule(shadowRun);
        var managedLeaves = managedRun.LeafTransitions.ToDictionary(l => l.RuleId, l => Render(l.Leaves));
        var shadowLeaves = shadowRun.LeafTransitions.ToDictionary(l => l.RuleId, l => Render(l.Leaves));

        foreach (var ruleId in ruleIds)
        {
            var m = managedEvents.GetValueOrDefault(ruleId, "(none)");
            var s = shadowEvents.GetValueOrDefault(ruleId, "(none)");
            if (m != s)
                Diverged(ruleId, "replay.events", m, s);

            m = managedLeaves.GetValueOrDefault(ruleId, "(none)");
            s = shadowLeaves.GetValueOrDefault(ruleId, "(none)");
            if (m != s)
                Diverged(ruleId, "replay.leaf_transitions", m, s);
        }
    }

    private void Diverged(Guid? ruleId, string field, string managedValue, string shadowValue) =>
        logger.LogWarning(
            "AlertEngineDivergence rule={RuleId} engine={Engine} field={Field} managed={Managed} rust={Rust}",
            ruleId, Engine, field, managedValue, shadowValue);

    private static Dictionary<Guid, string> EventsByRule(AlertReplayRun run) =>
        run.Events
            .GroupBy(e => e.RuleId)
            .ToDictionary(g => g.Key, g => string.Join(',', g.Select(e => $"{e.At:O} {e.Kind}")));

    private static string Render(IReadOnlyList<LeafTransitionLog> leaves) =>
        string.Join(';', leaves.Select(l =>
            $"{l.LeafId}:" + string.Join(',', l.Points.Select(p => $"{p.AtMs}={p.Value}"))));
}
