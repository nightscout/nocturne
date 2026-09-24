using Nocturne.Alerts.ParityCorpus.Generator.Harness;
using static Nocturne.Alerts.ParityCorpus.Generator.Scenarios.B;

namespace Nocturne.Alerts.ParityCorpus.Generator.Scenarios;

/// <summary>
/// The replay driver (docs/alerts/engine-semantics.md §8): evaluation order, replay-local firing
/// edges, timer resets on close, suppression, and the no-reading clamp.
/// </summary>
public static class ReplayScenarios
{
    private const string Low70 = """{"direction": "below", "value": 70}""";

    private static string FiringOf(int rule, int? forMinutes = null) =>
        forMinutes is { } m
            ? $$"""{"alert_id": "{{RId(rule)}}", "state": "firing", "for_minutes": {{m}}}"""
            : $$"""{"alert_id": "{{RId(rule)}}", "state": "firing"}""";

    private static ReplayScenarioFile Replay(
        string name, string description, List<ScenarioRule> rules, List<ReplayScenarioTick> ticks) =>
        new() { Name = name, Description = description, Rules = rules, Ticks = ticks };

    private static ReplayScenarioTick At(int minutes, ScenarioContext ctx, params int[] suppressed) =>
        new()
        {
            At = T(minutes),
            Context = ctx,
            SuppressedRuleIds = suppressed.Length == 0 ? null : suppressed.Select(RId).ToList(),
        };

    /// <summary>A fresh reading of <paramref name="glucose"/> at the tick.</summary>
    private static ReplayScenarioTick Reading(int minutes, decimal glucose, params int[] suppressed) =>
        At(minutes, Ctx(T(minutes), glucose), suppressed);

    public static IEnumerable<ReplayScenarioFile> All()
    {
        yield return Replay(
            "replay-alert-state-chain",
            "rules run after the rules their alert_state leaves reference, whatever order they are listed in, so a chain fires and clears within one tick; for_minutes holds from the parent's fire",
            [
                Rule(3, "alert_state", FiringOf(2, forMinutes: 10)),
                Rule(2, "alert_state", FiringOf(1)),
                Rule(1, "threshold", Low70),
            ],
            [
                Reading(0, 100m),
                Reading(5, 65m),   // 1 fires, then 2 on the same tick; 3 has held 0 of 10 minutes
                Reading(10, 65m),
                Reading(15, 65m),  // 3 fires: 2 has been firing for 10 minutes
                Reading(20, 100m), // 1 clears, so 2 and then 3 clear on the same tick
                Reading(25, 65m),  // 1 and 2 fire again; 3's hold starts over
            ]);

        yield return Replay(
            "replay-sustained-across-ticks",
            "a sustained body fires once its hold elapses, stays one fire while it holds, and after a clear must hold again from scratch",
            [Rule(1, "sustained", $$$"""{"minutes": 15, "child": {"type": "threshold", "threshold": {{{Low70}}}}}""")],
            [
                Reading(0, 65m),   // timer set
                Reading(5, 65m),
                Reading(10, 65m),
                Reading(15, 65m),  // 15 minutes held: fires
                Reading(20, 65m),  // continues, no second fire
                Reading(25, 100m), // clears
                Reading(30, 65m),  // timer set again
                Reading(35, 65m),
                Reading(40, 65m),
                Reading(45, 65m),  // fires again
            ]);

        yield return Replay(
            "replay-silent-clear-resets-timers",
            "a clear resets every timer of the rule, the auto-resolve tree's included, so after a re-fire the resolve hold starts over",
            [Rule(1, "threshold", Low70, autoResolveParams: """
                {"type": "sustained", "sustained": {"minutes": 10, "child":
                    {"type": "threshold", "threshold": {"direction": "above", "value": 60}}}}
                """)],
            [
                Reading(0, 65m),  // fires; the resolve child holds, its timer is set
                Reading(5, 66m),  // resolve has held 5 of 10 minutes
                Reading(10, 90m), // clears: the resolve timer is reset with the rest
                Reading(15, 65m), // fires again; the resolve timer is set anew, not 15 minutes old
                Reading(20, 65m),
                Reading(25, 65m), // resolve has held 10 minutes: auto-resolved
                Reading(30, 65m), // fires again; resolve holds 0 minutes
            ]);

        yield return Replay(
            "replay-auto-resolve",
            "auto-resolve is evaluated only while firing, including on the tick that fires, and a body still true on the next tick fires again",
            [Rule(1, "threshold", Low70, autoResolveParams: """
                {"type": "iob", "iob": {"operator": "<=", "value": 0.5}}
                """)],
            [
                At(0, Ctx(T(0), 100m) with { IobUnits = 0.2m }), // not firing: resolve is not evaluated
                At(5, Ctx(T(5), 65m) with { IobUnits = 2m }),    // fires
                At(10, Ctx(T(10), 65m) with { IobUnits = 1m }),
                At(15, Ctx(T(15), 65m) with { IobUnits = 0.4m }), // auto-resolved
                At(20, Ctx(T(20), 65m) with { IobUnits = 0.3m }), // fires again and resolves on the same tick
            ]);

        yield return Replay(
            "replay-dnd-suppression",
            "a fire on a tick whose suppression names the rule is recorded as suppressed, still makes the rule firing, and its alert_state children fire",
            [
                Rule(1, "threshold", Low70),
                Rule(2, "alert_state", FiringOf(1)),
            ],
            [
                Reading(0, 65m, 1),             // 1 suppressed, 2 fired
                Reading(5, 65m, 1),             // continues: suppression only labels the opening edge
                Reading(10, 100m),
                Reading(15, 65m),               // unsuppressed: fired
            ]);

        yield return Replay(
            "replay-signal-loss-gap",
            "ticks before the first reading read as fresh, so staleness and signal_loss stay quiet there; a gap after a reading reads as stale and fires both",
            [
                Rule(1, "signal_loss", """{"timeout_minutes": 15}"""),
                Rule(2, "staleness", """{"operator": ">=", "value": 10}"""),
            ],
            [
                At(0, Empty()),               // no reading yet: last reading reads as now
                At(5, Empty()),
                Reading(10, 100m),
                At(15, Ctx(T(10), 100m)),     // the 12:10 reading is the latest
                At(20, Ctx(T(10), 100m)),     // 10 minutes stale: staleness fires
                At(25, Ctx(T(10), 100m)),     // 15 minutes: signal_loss fires
                Reading(30, 100m),            // a reading: both clear
            ]);

        yield return Replay(
            "replay-unevaluable-rule-skipped",
            "a rule whose tree cannot be evaluated is skipped on every tick, even where the evaluation order would not reach the fault, so its alert_state children never see it fire",
            [
                Rule(1, "composite", $$$"""
                    {"operator": "or", "conditions": [
                        {"type": "threshold", "threshold": {{{Low70}}}},
                        {"type": "threshold", "threshold": {"value": 70}}]}
                    """),
                Rule(2, "alert_state", FiringOf(1)),
                Rule(3, "threshold", Low70),
            ],
            [Reading(0, 65m), Reading(5, 65m)]);

        yield return Replay(
            "replay-reference-cycle-keeps-given-order",
            "a cycle of alert_state references keeps the rules in the order given, so a child listed before its parent sees the parent's fire a tick late",
            [
                Rule(4, "alert_state", FiringOf(3)),
                Rule(1, "alert_state", FiringOf(2)),
                Rule(2, "alert_state", FiringOf(1)),
                Rule(3, "threshold", Low70),
            ],
            [Reading(0, 65m), Reading(5, 65m), Reading(10, 100m), Reading(15, 100m)]);

        yield return Replay(
            "replay-leaf-log",
            "every leaf is logged at its first observation and each flip, evaluated alone even where the composite short-circuits",
            [Rule(1, "composite", """
                {"operator": "and", "conditions": [
                    {"type": "threshold", "threshold": {"direction": "below", "value": 70}},
                    {"type": "iob", "iob": {"operator": ">=", "value": 1}},
                    {"type": "not", "not": {"child": {"type": "trend", "trend": {"bucket": "flat"}}}}]}
                """)],
            [
                At(0, Ctx(T(0), 100m) with { IobUnits = 2m, TrendBucket = "flat" }),
                At(5, Ctx(T(5), 65m) with { IobUnits = 2m, TrendBucket = "falling" }), // all true: fires
                At(10, Ctx(T(10), 65m) with { IobUnits = 0.5m, TrendBucket = "falling" }),
                At(15, Ctx(T(15), 100m) with { IobUnits = 0.5m, TrendBucket = "flat" }),
            ]);
    }
}
