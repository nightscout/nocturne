using Nocturne.API.Services.Alerts.Engines;
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
    bool NeedsSleepSession,
    IReadOnlySet<PumpModeState> ReferencedPumpStates,
    IReadOnlySet<(StateSpanCategory Category, string? State)> ReferencedStateSpans,
    IReadOnlySet<Guid> ReferencedTrackerDefinitions)
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
            false, false, false, false,
            new HashSet<PumpModeState>(),
            new HashSet<(StateSpanCategory, string?)>(),
            new HashSet<Guid>());
}

/// <summary>
/// Reads a batch of <see cref="AlertRuleSnapshot"/> records and reports which optional
/// <see cref="SensorContext"/> fields need to be populated to evaluate them.
/// </summary>
/// <remarks>
/// Each rule's leaves come from the engine (<see cref="RuleTrees"/>): its body, and its
/// auto-resolve tree while auto-resolve is enabled, read as evaluation reads them. A rule the
/// engine cannot describe contributes no needs rather than failing the pass for every other rule.
/// </remarks>
public static class RuleDataNeeds
{
    /// <summary>
    /// Reads <paramref name="rules"/> and returns a <see cref="DataNeedsSet"/> with a flag
    /// set for every kind of optional context any rule depends on.
    /// </summary>
    public static DataNeedsSet Walk(IEnumerable<AlertRuleSnapshot> rules, ILogger? logger = null)
    {
        var b = new NeedsBuilder();
        foreach (var rule in rules)
        {
            IReadOnlyList<ConditionLeaf> leaves;
            try
            {
                leaves = RuleTrees.Leaves(rule, includeAutoResolve: true);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Could not read the condition tree of alert rule {AlertRuleId}", rule.Id);
                continue;
            }
            foreach (var leaf in leaves)
                VisitLeaf(leaf, b);
        }
        return b.Build();
    }

    private static void VisitLeaf(ConditionLeaf leaf, NeedsBuilder b)
    {
        if (leaf.Kind is not { } kind) return;
        ApplyLeaf(kind, b);

        // pump_state, state_span_active and tracker_age select which mode, span or tracker the
        // enricher fetches.
        switch (kind)
        {
            case AlertConditionType.PumpState when leaf.EnumParam<PumpModeState>("mode") is { } mode:
                b.PumpStates.Add(mode);
                break;
            case AlertConditionType.StateSpanActive when leaf.EnumParam<StateSpanCategory>("category") is { } category:
                b.StateSpans.Add((category, leaf.StringParam("state")));
                break;
            case AlertConditionType.TrackerAge when leaf.GuidParam("tracker_definition_id") is { } definitionId:
                b.TrackerDefinitions.Add(definitionId);
                break;
        }
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
            case AlertConditionType.GlucoseBucket:
                b.GlucoseBucket = true;
                // Glucose-bucket resolution maps a UTC `at` into the tenant's local
                // wall-clock to pick the right schedule entry, so the enricher must fetch
                // the tenant timezone. Without this co-fetch the resolver silently falls
                // back to UTC and picks the wrong target-range entry.
                b.TenantTimeZone = true;
                break;
            // Carb/bolus times are derived from the same treatment fetch; one flag covers both.
            case AlertConditionType.TimeSinceLastCarb: b.Treatments = true; break;
            case AlertConditionType.TimeSinceLastBolus: b.Treatments = true; break;
            case AlertConditionType.DayOfWeek: b.TenantTimeZone = true; break;
            // TimeOfDay falls back to the tenant timezone when the rule's own Timezone is
            // null (the default the UI saves), so the enricher must populate it. Without
            // this co-fetch the engine would silently fall back to UTC.
            case AlertConditionType.TimeOfDay: b.TenantTimeZone = true; break;
            case AlertConditionType.SleepSessionActive: b.SleepSession = true; break;
            case AlertConditionType.PumpState: /* selection read in VisitLeaf */ break;
            case AlertConditionType.StateSpanActive: /* selection read in VisitLeaf */ break;
            case AlertConditionType.TrackerAge: /* selection read in VisitLeaf */ break;
            // DoNotDisturb deliberately not handled here — see DataNeedsSet docs above.
            // Threshold, RateOfChange, SignalLoss, Staleness, Composite, Not, Sustained
            // require no extra context: the base SensorContext carries what they read.
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
        public bool SleepSession;
        public readonly HashSet<PumpModeState> PumpStates = new();
        public readonly HashSet<(StateSpanCategory Category, string? State)> StateSpans = new();
        public readonly HashSet<Guid> TrackerDefinitions = new();

        public DataNeedsSet Build() =>
            new(Iob, Cob, Predicted, Reservoir, SiteAge, SensorAge, Trend, ActiveAlerts,
                LastApsCycle, LastApsEnacted, PumpStatus, TempBasal, UploaderStatus, Override, SensitivityRatio,
                GlucoseBucket, Treatments, TenantTimeZone, SleepSession, PumpStates, StateSpans, TrackerDefinitions);
    }
}
