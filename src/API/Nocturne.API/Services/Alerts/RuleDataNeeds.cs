using System.Text.Json;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// The set of optional <see cref="SensorContext"/> fields any rule in a batch references,
/// computed by walking each rule's condition tree once before evaluation begins.
/// </summary>
/// <remarks>
/// Used by <see cref="ISensorContextEnricher"/> implementations to skip fetches whose result
/// no rule will read this pass — e.g. a tenant whose only enabled rule is a glucose threshold
/// avoids loading treatments, predictions, device events, and active alert snapshots.
/// </remarks>
public sealed record DataNeedsSet(
    bool NeedsIob,
    bool NeedsCob,
    bool NeedsPredicted,
    bool NeedsReservoir,
    bool NeedsSiteAge,
    bool NeedsSensorAge,
    bool NeedsTrendBucket,
    bool NeedsActiveAlerts,
    bool NeedsLastApsCycle,
    bool NeedsLastApsEnacted,
    bool NeedsPumpStatus,
    bool NeedsTempBasal,
    bool NeedsUploaderStatus,
    bool NeedsOverride,
    bool NeedsSensitivityRatio,
    bool NeedsGlucoseBucket,
    bool NeedsTreatments,
    bool NeedsTenantTimeZone,
    IReadOnlySet<PumpModeState> ReferencedPumpStates,
    IReadOnlySet<(StateSpanCategory Category, string? State)> ReferencedStateSpans)
// Note: there is intentionally no `NeedsDoNotDisturb` here. DND state must be available for
// every evaluation pass because engine-level suppression applies to every rule regardless of
// whether the rule's condition tree references the `do_not_disturb` fact. Gating fetch on a
// per-rule walker flag would silently exempt typical glucose/threshold rules from suppression.
// The unconditional fetch in `SensorContextEnricher` is by design.
{
    /// <summary>An empty needs set with all flags false.</summary>
    public static DataNeedsSet None { get; } =
        new(false, false, false, false, false, false, false, false,
            false, false, false, false, false, false, false,
            false, false, false,
            new HashSet<PumpModeState>(),
            new HashSet<(StateSpanCategory, string?)>());
}

/// <summary>
/// Walks a batch of <see cref="AlertRuleSnapshot"/> records and reports which optional
/// <see cref="SensorContext"/> fields need to be populated to evaluate them.
/// </summary>
/// <remarks>
/// Each rule's <see cref="AlertRuleSnapshot.ConditionType"/> drives the top-level entry; for
/// the recursive wrappers (<c>composite</c>, <c>not</c>, <c>sustained</c>) the JSON payload is
/// deserialised once per rule and the resulting <see cref="ConditionNode"/> tree is walked via
/// <see cref="ConditionPath.Walk"/>. Malformed JSON is treated as "no needs" — rule evaluation
/// will fail later with the same silent fail-mode used by the leaf evaluators, so the enricher
/// must not re-throw here.
/// </remarks>
public static class RuleDataNeeds
{
    /// <summary>
    /// Walks <paramref name="rules"/> and returns a <see cref="DataNeedsSet"/> with a flag
    /// set for every kind of optional context any rule depends on.
    /// </summary>
    public static DataNeedsSet Walk(IEnumerable<AlertRuleSnapshot> rules)
    {
        var b = new NeedsBuilder();
        foreach (var rule in rules)
        {
            VisitTopLevel(rule, b);
        }
        return b.Build();
    }

    private static void VisitTopLevel(AlertRuleSnapshot rule, NeedsBuilder b)
    {
        switch (rule.ConditionType)
        {
            case AlertConditionType.Composite:
                {
                    var composite = TryDeserialize<CompositeCondition>(rule.ConditionParams);
                    if (composite is null) return;
                    foreach (var child in composite.Conditions)
                    {
                        VisitNode(child, b);
                    }
                    return;
                }
            case AlertConditionType.Not:
                {
                    var not = TryDeserialize<NotCondition>(rule.ConditionParams);
                    if (not is null) return;
                    VisitNode(not.Child, b);
                    return;
                }
            case AlertConditionType.Sustained:
                {
                    var sustained = TryDeserialize<SustainedCondition>(rule.ConditionParams);
                    if (sustained is null) return;
                    VisitNode(sustained.Child, b);
                    return;
                }
            case AlertConditionType.PumpState:
                {
                    ApplyLeaf(rule.ConditionType, b);
                    var typed = TryDeserialize<PumpStateCondition>(rule.ConditionParams);
                    if (typed is not null) b.PumpStates.Add(typed.Mode);
                    return;
                }
            case AlertConditionType.StateSpanActive:
                {
                    ApplyLeaf(rule.ConditionType, b);
                    var typed = TryDeserialize<StateSpanActiveCondition>(rule.ConditionParams);
                    if (typed is not null) b.StateSpans.Add((typed.Category, typed.State));
                    return;
                }
            default:
                ApplyLeaf(rule.ConditionType, b);
                return;
        }
    }

    private static void VisitNode(ConditionNode node, NeedsBuilder b)
    {
        // ConditionPath.Walk recurses through composite/not/sustained wrappers and visits every
        // node; we only need the node's Type to update flags. The builder closes over the call
        // directly — no ref-bool plumbing required.
        ConditionPath.Walk<object>(node, (visited, _) =>
        {
            var kind = AlertConditionTypeNames.FromWireString(visited.Type);
            if (kind is not null)
            {
                ApplyLeaf(kind.Value, b);

                // pump_state and state_span_active carry per-rule "which mode/category/state"
                // selections that the enricher needs to know to fetch the right StateSpans.
                if (kind == AlertConditionType.PumpState && visited.PumpState is not null)
                    b.PumpStates.Add(visited.PumpState.Mode);
                else if (kind == AlertConditionType.StateSpanActive && visited.StateSpanActive is not null)
                    b.StateSpans.Add((visited.StateSpanActive.Category, visited.StateSpanActive.State));
            }
            return null;
        });
    }

    private static void ApplyLeaf(AlertConditionType type, NeedsBuilder b)
    {
        switch (type)
        {
            case AlertConditionType.Iob: b.Iob = true; break;
            case AlertConditionType.Cob: b.Cob = true; break;
            case AlertConditionType.Predicted: b.Predicted = true; break;
            case AlertConditionType.Reservoir: b.Reservoir = true; break;
            case AlertConditionType.SiteAge: b.SiteAge = true; break;
            case AlertConditionType.SensorAge: b.SensorAge = true; break;
            case AlertConditionType.Trend: b.Trend = true; break;
            case AlertConditionType.AlertState: b.ActiveAlerts = true; break;
            case AlertConditionType.LoopStale: b.LastApsCycle = true; break;
            case AlertConditionType.LoopEnactionStale:
                b.LastApsEnacted = true;
                // Co-fetch LastApsCycle so HasEverApsCycled is populated. The evaluator's
                // cold-start guard reads HasEverApsCycled (there is no separate
                // HasEverApsEnacted flag); without this co-fetch a tenant whose only
                // enabled rule is LoopEnactionStale would never fire.
                b.LastApsCycle = true;
                break;
            case AlertConditionType.PumpSuspended: b.PumpStatus = true; break;
            case AlertConditionType.PumpBattery: b.PumpStatus = true; break;
            case AlertConditionType.TempBasal: b.TempBasal = true; break;
            case AlertConditionType.UploaderBattery: b.UploaderStatus = true; break;
            case AlertConditionType.OverrideActive: b.Override = true; break;
            case AlertConditionType.SensitivityRatio: b.SensitivityRatio = true; break;
            case AlertConditionType.GlucoseBucket: b.GlucoseBucket = true; break;
            // Carb/bolus times are derived from the same treatment fetch; one flag covers both.
            case AlertConditionType.TimeSinceLastCarb: b.Treatments = true; break;
            case AlertConditionType.TimeSinceLastBolus: b.Treatments = true; break;
            case AlertConditionType.DayOfWeek: b.TenantTimeZone = true; break;
            case AlertConditionType.PumpState: /* handled in VisitTopLevel/VisitNode */ break;
            case AlertConditionType.StateSpanActive: /* handled in VisitTopLevel/VisitNode */ break;
            // DoNotDisturb deliberately not handled here — see DataNeedsSet docs above.
            // Threshold, RateOfChange, SignalLoss, Staleness, TimeOfDay, Composite, Not, Sustained
            // require no extra context — handled by base SensorContext or recursed by VisitNode.
        }
    }

    private static T? TryDeserialize<T>(string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, EvaluatorJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Mutable accumulator used by <see cref="Walk"/>. Avoids passing each flag through
    /// the recursion as a <c>ref bool</c>.
    /// </summary>
    private sealed class NeedsBuilder
    {
        public bool Iob;
        public bool Cob;
        public bool Predicted;
        public bool Reservoir;
        public bool SiteAge;
        public bool SensorAge;
        public bool Trend;
        public bool ActiveAlerts;
        public bool LastApsCycle;
        public bool LastApsEnacted;
        public bool PumpStatus; // PumpSuspended OR PumpBattery — both share a PumpSnapshot fetch.
        public bool TempBasal;
        public bool UploaderStatus;
        public bool Override;
        public bool SensitivityRatio;
        public bool GlucoseBucket;
        public bool Treatments;
        public bool TenantTimeZone;
        public readonly HashSet<PumpModeState> PumpStates = new();
        public readonly HashSet<(StateSpanCategory Category, string? State)> StateSpans = new();

        public DataNeedsSet Build() =>
            new(Iob, Cob, Predicted, Reservoir, SiteAge, SensorAge, Trend, ActiveAlerts,
                LastApsCycle, LastApsEnacted, PumpStatus, TempBasal, UploaderStatus, Override, SensitivityRatio,
                GlucoseBucket, Treatments, TenantTimeZone, PumpStates, StateSpans);
    }
}
