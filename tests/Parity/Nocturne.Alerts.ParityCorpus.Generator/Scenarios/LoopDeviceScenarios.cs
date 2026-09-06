using Nocturne.Alerts.ParityCorpus.Generator.Harness;
using static Nocturne.Alerts.ParityCorpus.Generator.Scenarios.B;

namespace Nocturne.Alerts.ParityCorpus.Generator.Scenarios;

/// <summary>
/// Looping/device leaves: loop_stale, loop_enaction_stale, pump_suspended, temp_basal,
/// override_active, do_not_disturb, pump_state, state_span_active.
/// </summary>
public static class LoopDeviceScenarios
{
    public static IEnumerable<ScenarioFile> All()
    {
        yield return Scenario(
            "loop-stale-guard-and-boundaries",
            "loop_stale is false without HasEverApsCycled, false with a null cycle timestamp, then compares minutes since the cycle",
            [Rule(1, "loop_stale", """{"operator": ">=", "value": 15}""")],
            [
                Tick(T(0), Ctx(T(0)) with { LastApsCycleAt = T(-20) }),                          // guard unset
                Tick(T(5), Ctx(T(5)) with { HasEverApsCycled = true }),                          // guard set, no timestamp
                Tick(T(10), Ctx(T(10)) with { HasEverApsCycled = true, LastApsCycleAt = T(-5) }), // exactly 15m -> >= fires
                Tick(T(15), Ctx(T(15)) with { HasEverApsCycled = true, LastApsCycleAt = T(5) }),  // 10m -> false
            ]);

        yield return Scenario(
            "loop-enaction-stale-shared-guard",
            "loop_enaction_stale uses the HasEverApsCycled guard (not an enaction-specific flag) and measures from LastApsEnactedAt",
            [Rule(1, "loop_enaction_stale", """{"operator": ">", "value": 30}""")],
            [
                Tick(T(0), Ctx(T(0)) with { LastApsEnactedAt = T(-60) }),                            // guard unset
                Tick(T(5), Ctx(T(5)) with { HasEverApsCycled = true, LastApsEnactedAt = T(-60) }),   // 65m -> fires
                Tick(T(10), Ctx(T(10)) with { HasEverApsCycled = true, LastApsEnactedAt = T(-10) }), // 20m -> false
            ]);

        yield return Scenario(
            "pump-suspended-states",
            "is_active matches against suspension presence under the HasEverPumpSnapshot guard; for_minutes anchors at StartedAt and is a no-op on the inactive side",
            [
                Rule(1, "pump_suspended", """{"is_active": true, "for_minutes": null}"""),
                Rule(2, "pump_suspended", """{"is_active": true, "for_minutes": 30}"""),
                Rule(3, "pump_suspended", """{"is_active": false, "for_minutes": 30}"""),
            ],
            [
                Tick(T(0), Ctx(T(0)) with { ActivePumpSuspension = new(T(-10)) }),                          // guard unset: all false
                Tick(T(5), Ctx(T(5)) with { HasEverPumpSnapshot = true, ActivePumpSuspension = new(T(-10)) }),  // active 15m: r1 true, r2 false, r3 false
                Tick(T(10), Ctx(T(10)) with { HasEverPumpSnapshot = true, ActivePumpSuspension = new(T(-20)) }), // active 30m: r2 true (>=)
                Tick(T(15), Ctx(T(15)) with { HasEverPumpSnapshot = true }),                                  // not suspended: r3 true despite for_minutes
            ]);

        yield return Scenario(
            "temp-basal-metrics",
            "rate metric compares U/hr; percent_of_scheduled computes rate/scheduled*100; scheduled null or zero is false; no active temp is false (no guard)",
            [
                Rule(1, "temp_basal", """{"metric": "rate", "operator": ">=", "value": 2}"""),
                Rule(2, "temp_basal", """{"metric": "percent_of_scheduled", "operator": "<=", "value": 50}"""),
            ],
            [
                Tick(T(0), Ctx(T(0)) with { ActiveTempBasal = new(2m, 1m, T(-5)) }),       // rate 2 >= 2 true; 200% <= 50 false
                Tick(T(5), Ctx(T(5)) with { ActiveTempBasal = new(0.5m, 1m, T(-5)) }),     // rate false; 50% <= 50 true
                Tick(T(10), Ctx(T(10)) with { ActiveTempBasal = new(2m, null, T(-5)) }),   // percent: scheduled null -> false
                Tick(T(15), Ctx(T(15)) with { ActiveTempBasal = new(2m, 0m, T(-5)) }),     // percent: scheduled zero -> false
                Tick(T(20), Ctx(T(20))),                                                   // no temp -> both false
            ]);

        yield return Scenario(
            "override-active-no-guard",
            "override_active has no HasEver guard: absence of an override legitimately satisfies is_active=false",
            [
                Rule(1, "override_active", """{"is_active": true, "for_minutes": 60}"""),
                Rule(2, "override_active", """{"is_active": false, "for_minutes": null}"""),
            ],
            [
                Tick(T(0), Ctx(T(0))),                                              // r1 false, r2 true
                Tick(T(5), Ctx(T(5)) with { ActiveOverride = new(T(-60)) }),        // exactly 60m: r1 true (>=), r2 false
                Tick(T(10), Ctx(T(10)) with { ActiveOverride = new(T(-30)) }),      // 40m: r1 false
            ]);

        yield return Scenario(
            "do-not-disturb-leaf",
            "do_not_disturb mirrors the is_active/for_minutes shape against the DND snapshot",
            [
                Rule(1, "do_not_disturb", """{"is_active": true, "for_minutes": 15}"""),
                Rule(2, "do_not_disturb", """{"is_active": false, "for_minutes": 99}"""),
            ],
            [
                Tick(T(0), Ctx(T(0)) with { ActiveDoNotDisturb = new(T(-15), "manual") }),  // 15m: r1 true; r2 false
                Tick(T(5), Ctx(T(5)) with { ActiveDoNotDisturb = new(T(0), "scheduled") }), // 5m: r1 false
                Tick(T(10), Ctx(T(10))),                                                    // off: r2 true (for_minutes no-op)
            ]);

        yield return Scenario(
            "pump-state-modes",
            "is_active=true requires the exact mode (for_minutes anchored at span start); is_active=false is true whenever the active mode differs, including no span at all",
            [
                Rule(1, "pump_state", """{"mode": "Automatic", "is_active": true, "for_minutes": null}"""),
                Rule(2, "pump_state", """{"mode": "Automatic", "is_active": true, "for_minutes": 30}"""),
                Rule(3, "pump_state", """{"mode": "Automatic", "is_active": false, "for_minutes": null}"""),
            ],
            [
                Tick(T(0), Ctx(T(0)) with { ActivePumpState = new("Automatic", T(-10)) }),  // r1 true, r2 false (10m), r3 false
                Tick(T(5), Ctx(T(5)) with { ActivePumpState = new("Automatic", T(-25)) }),  // r2 exactly 30m -> true
                Tick(T(10), Ctx(T(10)) with { ActivePumpState = new("Manual", T(-10)) }),   // r1/r2 false, r3 true
                Tick(T(15), Ctx(T(15))),                                                    // no span: r3 true
            ]);

        yield return Scenario(
            "state-span-active-lookup",
            "lookup is by exact (category, state) key; null state means the any-of-category key; is_active=false asserts key absence; PumpMode category is always false",
            [
                Rule(1, "state_span_active", """{"category": "Illness", "state": null, "is_active": true, "for_minutes": null}"""),
                Rule(2, "state_span_active", """{"category": "Exercise", "state": "running", "is_active": true, "for_minutes": 20}"""),
                Rule(3, "state_span_active", """{"category": "Illness", "state": null, "is_active": false, "for_minutes": null}"""),
                Rule(4, "state_span_active", """{"category": "PumpMode", "state": null, "is_active": true, "for_minutes": null}"""),
            ],
            [
                Tick(T(0), Ctx(T(0)) with
                {
                    ActiveStateSpans =
                    [
                        new("Illness", null, T(-40)),
                        new("Exercise", "running", T(-20)),
                        new("PumpMode", null, T(-40)),
                    ],
                }),
                Tick(T(5), Ctx(T(5)) with
                {
                    ActiveStateSpans = [new("Exercise", "running", T(-10))],
                }),
                Tick(T(10), Ctx(T(10))),
            ]);
    }
}
