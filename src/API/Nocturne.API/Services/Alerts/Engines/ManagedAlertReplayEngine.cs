using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Engines;

/// <summary>
/// The in-process C# <see cref="IAlertReplayEngine"/> (docs/alerts/engine-semantics.md §8). It
/// evaluates through a replay-local <see cref="ConditionEvaluatorRegistry"/> whose fake clock and
/// timer store live for one call, so the live tenant timer table is never touched.
/// </summary>
internal sealed class ManagedAlertReplayEngine(ILogger<ManagedAlertReplayEngine> logger) : IAlertReplayEngine
{
    public async Task<AlertReplayRun> ReplayAsync(AlertReplayInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var ordered = TopologicallySort(input.Rules);
        var bodies = ordered.Select(BuildEvaluableBody).ToList();
        var resolvers = ordered.Select(BuildAutoResolveNode).ToList();

        var fakeTime = new ReplayTimeProvider();
        var timerStore = new InMemoryConditionTimerStore();
        await using var replayServices = BuildReplayServices(timerStore, fakeTime);
        var registry = replayServices.GetRequiredService<ConditionEvaluatorRegistry>();
        var forceRunner = new ForceEvalRunner();

        var firing = new bool[ordered.Count];
        // Set by an auto-resolve and held while the body stays true, as the tracker's re-arm is
        // (docs/alerts/engine-semantics.md §6.3).
        var awaitingRearm = new bool[ordered.Count];
        var leafLogs = new Dictionary<int, (bool Last, List<LeafTransitionPoint> Points)>?[ordered.Count];
        // One map for the whole pass, so a parent's fire is visible to its children later in the
        // order on the same tick.
        var activeAlerts = new Dictionary<Guid, ActiveAlertSnapshot>();
        var events = new List<AlertReplayRunEvent>();
        var tickOutcomes = input.IncludeTicks ? new List<AlertReplayTickOutcome>(input.Ticks.Count) : null;

        foreach (var replayTick in input.Ticks)
        {
            ct.ThrowIfCancellationRequested();
            var tick = DateTime.SpecifyKind(replayTick.At, DateTimeKind.Utc);
            fakeTime.SetUtcNow(tick);

            var tickContext = replayTick.Context with { ActiveAlerts = activeAlerts };
            if (tickContext.LatestTimestamp is null && tickContext.LastReadingAt is null)
                tickContext = tickContext with { LastReadingAt = tick };

            var ruleTicks = new List<AlertReplayRuleTick>(ordered.Count);
            for (var i = 0; i < ordered.Count; i++)
            {
                var rule = ordered[i];
                var node = bodies[i];
                if (node is null)
                {
                    ruleTicks.Add(new AlertReplayRuleTick(rule.Id, Skipped: true, Met: false, Firing: false));
                    continue;
                }

                var ruleContext = tickContext with
                {
                    CurrentRuleId = rule.Id,
                    CurrentPath = AlertConditionTypeNames.ToWireString(rule.ConditionType),
                };

                bool met;
                try
                {
                    met = await registry.EvaluateNodeAsync(node, ruleContext, ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Replay evaluation failed for rule {RuleId} at {Tick}; treating as not-met",
                        rule.Id, tick);
                    met = false;
                }

                // Every leaf alone, with no short-circuit, for the leaf log.
                IReadOnlyDictionary<int, bool> leafValues;
                try
                {
                    leafValues = await forceRunner.EvaluateAllLeavesAsync(node, ruleContext, registry, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex,
                        "Replay force-eval failed for rule {RuleId} at {Tick}; skipping leaf log for this tick",
                        rule.Id, tick);
                    leafValues = new Dictionary<int, bool>();
                }
                RecordLeaves(ref leafLogs[i], leafValues, tick);

                awaitingRearm[i] &= met;
                var currentlyFiring = met && !awaitingRearm[i];
                if (currentlyFiring && !firing[i])
                {
                    var kind = replayTick.SuppressedRuleIds.Contains(rule.Id)
                        ? AlertReplayTransition.SuppressedByDnd
                        : AlertReplayTransition.Fired;
                    events.Add(new AlertReplayRunEvent(tick, rule.Id, kind));
                    activeAlerts[rule.Id] = new ActiveAlertSnapshot("firing", tick, null);
                }
                else if (!met && firing[i])
                {
                    activeAlerts.Remove(rule.Id);
                    await timerStore.ClearAllForRuleAsync(rule.Id, ct);
                    events.Add(new AlertReplayRunEvent(tick, rule.Id, AlertReplayTransition.Cleared));
                }

                // After the fire, so a resolve predicate already true on the opening tick yields a
                // fired and an auto-resolved event together.
                if (currentlyFiring && rule.AutoResolveEnabled && resolvers[i] is { } resolveNode)
                {
                    var resolveContext = ruleContext with
                    {
                        CurrentPath = AlertConditionTypeNames.AutoResolvePathRoot,
                    };
                    bool shouldResolve;
                    try
                    {
                        shouldResolve = await registry.EvaluateNodeAsync(resolveNode, resolveContext, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex,
                            "Replay auto-resolve evaluation failed for rule {RuleId} at {Tick}; treating as not-resolved",
                            rule.Id, tick);
                        shouldResolve = false;
                    }

                    if (shouldResolve)
                    {
                        events.Add(new AlertReplayRunEvent(tick, rule.Id, AlertReplayTransition.AutoResolved));
                        activeAlerts.Remove(rule.Id);
                        await timerStore.ClearAllForRuleAsync(rule.Id, ct);
                        currentlyFiring = false;
                        awaitingRearm[i] = true;
                    }
                }

                firing[i] = currentlyFiring;
                ruleTicks.Add(new AlertReplayRuleTick(rule.Id, Skipped: false, met, currentlyFiring));
            }

            tickOutcomes?.Add(new AlertReplayTickOutcome(tick, ruleTicks));
        }

        var leafTransitions = new List<AlertReplayRuleLeafLog>();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (leafLogs[i] is not { } log) continue;
            leafTransitions.Add(new AlertReplayRuleLeafLog(
                ordered[i].Id,
                log.OrderBy(kv => kv.Key).Select(kv => new LeafTransitionLog(kv.Key, kv.Value.Points)).ToList()));
        }

        return new AlertReplayRun(ordered.Select(r => r.Id).ToList(), events, leafTransitions, tickOutcomes);
    }

    /// <summary>A leaf's first observation and every flip; a tick with no leaf values records nothing.</summary>
    private static void RecordLeaves(
        ref Dictionary<int, (bool Last, List<LeafTransitionPoint> Points)>? log,
        IReadOnlyDictionary<int, bool> leafValues,
        DateTime tick)
    {
        if (leafValues.Count == 0) return;
        log ??= new Dictionary<int, (bool, List<LeafTransitionPoint>)>(leafValues.Count);
        var tickMs = new DateTimeOffset(tick).ToUnixTimeMilliseconds();
        foreach (var (leafId, value) in leafValues)
        {
            if (!log.TryGetValue(leafId, out var entry))
            {
                log[leafId] = (value, [new LeafTransitionPoint(tickMs, value)]);
            }
            else if (entry.Last != value)
            {
                entry.Points.Add(new LeafTransitionPoint(tickMs, value));
                log[leafId] = (value, entry.Points);
            }
        }
    }

    /// <summary>
    /// The rule body, or null when it cannot be evaluated (docs/alerts/engine-semantics.md §1.4):
    /// the rule is then skipped on every tick, as the live engine skips it.
    /// </summary>
    private ConditionNode? BuildEvaluableBody(AlertRuleSnapshot rule)
    {
        if (ConditionTreeFaults.InRule(rule.ConditionType, rule.ConditionParams) is { } fault)
        {
            logger.LogWarning(
                "Replay: conditions of rule {RuleId} cannot be evaluated ({Reason} at {Path}); skipping it",
                rule.Id, fault.Reason, fault.Path);
            return null;
        }
        return BuildNodeForRule(rule);
    }

    /// <summary>The auto-resolve tree, or null when it is off, malformed or cannot be evaluated.</summary>
    private ConditionNode? BuildAutoResolveNode(AlertRuleSnapshot rule)
    {
        if (!rule.AutoResolveEnabled || string.IsNullOrWhiteSpace(rule.AutoResolveParams))
            return null;
        ConditionNode? node;
        try
        {
            node = JsonSerializer.Deserialize<ConditionNode>(rule.AutoResolveParams, EvaluatorJson.Options);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex,
                "Replay: malformed AutoResolveParams for rule {RuleId}; skipping auto-resolve", rule.Id);
            return null;
        }
        if (node is not null
            && ConditionTreeFaults.InNode(node, AlertConditionTypeNames.AutoResolvePathRoot) is { } fault)
        {
            logger.LogWarning(
                "Replay: AutoResolveParams for rule {RuleId} cannot be evaluated ({Reason} at {Path}); skipping auto-resolve",
                rule.Id, fault.Reason, fault.Path);
            return null;
        }
        return node;
    }

    /// <summary>
    /// The full node <c>{"type": wire, wire: payload}</c> for a rule's stored type and payload, or
    /// null when the payload is not JSON: the rule is then skipped on every tick.
    /// </summary>
    private ConditionNode? BuildNodeForRule(AlertRuleSnapshot rule)
    {
        try
        {
            var wire = AlertConditionTypeNames.ToWireString(rule.ConditionType);
            var node = new JsonObject { ["type"] = wire, [wire] = JsonNode.Parse(rule.ConditionParams) };
            return node.Deserialize<ConditionNode>(EvaluatorJson.Options);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Replay: malformed ConditionParams for rule {RuleId}", rule.Id);
            return null;
        }
    }

    /// <summary>
    /// A self-contained <see cref="ServiceProvider"/> for one replay. It is the live
    /// <see cref="ServiceRegistrationExtensions.AddAlertEvaluators"/> registration, so replay has
    /// exactly the live evaluator set, with a replay-local <see cref="TimeProvider"/> and
    /// <see cref="IConditionTimerStore"/>. The caller disposes it.
    /// </summary>
    internal static ServiceProvider BuildReplayServices(IConditionTimerStore timerStore, TimeProvider time)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(time);
        services.AddSingleton(timerStore);
        services.AddAlertEvaluators();
        services.AddSingleton<ConditionEvaluatorRegistry>();
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Each rule after every rule it references through <c>alert_state</c>. A cycle (blocked at
    /// write time, but possible in stale data) keeps the given order.
    /// </summary>
    /// <exception cref="ArgumentException">Two rules share an id.</exception>
    private IReadOnlyList<AlertRuleSnapshot> TopologicallySort(IReadOnlyList<AlertRuleSnapshot> rules)
    {
        var byId = rules.ToDictionary(r => r.Id);
        var dependencies = new Dictionary<Guid, HashSet<Guid>>(rules.Count);
        foreach (var rule in rules)
        {
            var deps = new HashSet<Guid>();
            var node = BuildNodeForRule(rule);
            if (node is not null)
            {
                foreach (var refId in ConditionTreeWalker.AlertStateReferences(node))
                {
                    if (byId.ContainsKey(refId)) deps.Add(refId);
                }
            }
            dependencies[rule.Id] = deps;
        }

        var visited = new HashSet<Guid>();
        var result = new List<AlertRuleSnapshot>(rules.Count);

        bool Visit(Guid id, HashSet<Guid> stack)
        {
            if (visited.Contains(id)) return true;
            if (!stack.Add(id)) return false;
            foreach (var dep in dependencies[id])
            {
                if (!Visit(dep, stack)) return false;
            }
            stack.Remove(id);
            visited.Add(id);
            result.Add(byId[id]);
            return true;
        }

        foreach (var rule in rules)
        {
            if (!Visit(rule.Id, new HashSet<Guid>()))
            {
                logger.LogWarning("Replay topo-sort hit a cycle for tenant rules; falling back to insertion order");
                return rules;
            }
        }

        return result;
    }

    /// <summary>
    /// A <see cref="TimeProvider"/> each tick sets, without taking a dependency on
    /// <c>Microsoft.Extensions.TimeProvider.Testing</c> in production code.
    /// </summary>
    private sealed class ReplayTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void SetUtcNow(DateTime utc) => _now = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }
}
