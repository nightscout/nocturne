using Nocturne.Alerts.ParityCorpus.Generator.Harness;
using static Nocturne.Alerts.ParityCorpus.Generator.Scenarios.B;

namespace Nocturne.Alerts.ParityCorpus.Generator.Scenarios;

/// <summary>
/// Auto-resolve: the unconditional post-tracker pass under the auto_resolve path root.
/// </summary>
public static class AutoResolveScenarios
{
    private const string Low70 = """{"direction": "below", "value": 70}""";

    public static IEnumerable<ScenarioFile> All()
    {
        yield return Scenario(
            "auto-resolve-basic",
            "an active excursion force-closes (reason auto) once the auto-resolve tree evaluates true",
            [Rule(1, "threshold", Low70, hysteresis: 60, autoResolveParams: """
                {"type": "threshold", "threshold": {"direction": "above", "value": 100}}
                """)],
            [
                Tick(T(0), Ctx(T(0), glucose: 65m)),    // opened; resolve predicate (>100) false
                Tick(T(5), Ctx(T(5), glucose: 80m)),    // body false -> hysteresis_started (window 60m); resolve false (80 <= 100)
                Tick(T(10), Ctx(T(10), glucose: 120m)), // still in hysteresis; resolve true -> force-closed with reason auto
            ]);

        yield return Scenario(
            "auto-resolve-same-tick-as-open",
            "the auto-resolve pass runs unconditionally after the tracker, so a resolve predicate already true on the open tick produces opened + auto-resolved in one evaluation",
            [Rule(1, "iob", """{"operator": ">=", "value": 3}""", autoResolveParams: """
                {"type": "trend", "trend": {"bucket": "flat"}}
                """)],
            [
                Tick(T(0), Ctx(T(0)) with { IobUnits = 4m, TrendBucket = "flat" }),
            ]);

        yield return Scenario(
            "auto-resolve-sustained-separate-timer",
            "a sustained inside auto_resolve_params keys its timer under the auto_resolve path root, not the rule body's root",
            [Rule(1, "sustained", """
                    {"minutes": 5, "child": {"type": "threshold", "threshold": {"direction": "below", "value": 70}}}
                    """,
                hysteresis: 60,
                autoResolveParams: """
                    {"type": "sustained", "sustained": {"minutes": 10, "child":
                        {"type": "threshold", "threshold": {"direction": "above", "value": 90}}}}
                    """)],
            [
                Tick(T(0), Ctx(T(0), glucose: 65m)),    // body sustained sets the 'sustained' timer; no excursion yet -> auto-resolve pass skipped (no active excursion)
                Tick(T(5), Ctx(T(5), glucose: 65m)),    // body fires -> opened; resolve tree now evaluated: child false (65 <= 90)
                Tick(T(10), Ctx(T(10), glucose: 95m)),  // body false -> hysteresis (60m window); resolve child true -> sets the timer keyed at the bare 'auto_resolve' root, returns false
                Tick(T(15), Ctx(T(15), glucose: 95m)),  // resolve window 5m of 10m: false
                Tick(T(20), Ctx(T(20), glucose: 95m)),  // 10m elapsed -> resolve true -> closed (reason auto)
            ]);

        yield return Scenario(
            "auto-resolve-malformed-json",
            "malformed auto_resolve_params are skipped silently (the excursion stays active)",
            [Rule(1, "threshold", Low70, autoResolveParams: """
                {"type": "threshold", "threshold": "this-should-be-an-object"}
                """)],
            [
                Tick(T(0), Ctx(T(0), glucose: 65m)),    // opened
                Tick(T(5), Ctx(T(5), glucose: 64m)),    // continues; resolve eval false/again skipped, never closes
            ]);

        yield return Scenario(
            "auto-resolve-unknown-enum-name",
            "an enum name the C# enum does not declare makes the resolve tree unparseable, so it is skipped silently",
            [Rule(1, "threshold", Low70, autoResolveParams: """
                {"type": "state_span_active", "state_span_active": {"category": "Sleep", "state": null, "is_active": false}}
                """)],
            [
                Tick(T(0), Ctx(T(0), glucose: 65m)),    // opened; resolve tree unparseable -> stays active
                Tick(T(5), Ctx(T(5), glucose: 64m)),    // continues
            ]);

        yield return Scenario(
            "auto-resolve-only-while-excursion-active",
            "the resolve tree is only consulted while the tracker holds an active excursion (active or hysteresis)",
            [Rule(1, "threshold", Low70, autoResolveParams: """
                {"type": "trend", "trend": {"bucket": "flat"}}
                """)],
            [
                Tick(T(0), Ctx(T(0), glucose: 100m) with { TrendBucket = "flat" }),  // idle: resolve pass does nothing
                Tick(T(5), Ctx(T(5), glucose: 65m) with { TrendBucket = "flat" }),   // opened, then resolve true -> closed same tick
                Tick(T(10), Ctx(T(10), glucose: 65m) with { TrendBucket = "flat" }), // body and resolve still true: awaits re-arm, opens nothing
            ]);

        yield return Scenario(
            "auto-resolve-waits-for-rearm",
            "an auto-resolve that closes an active excursion leaves the rule idle awaiting re-arm: while the body and the resolve tree both hold it opens nothing, however often it is evaluated against the same reading, and an evaluation that finds the body false re-arms it",
            [Rule(1, "iob", """{"operator": ">=", "value": 3}""", autoResolveParams: """
                {"type": "trend", "trend": {"bucket": "flat"}}
                """)],
            [
                Tick(T(0), Ctx(T(0)) with { IobUnits = 4m, TrendBucket = "flat" }),     // opened, auto-resolved on the same tick
                Tick(T(1), Ctx(T(0)) with { IobUnits = 4m, TrendBucket = "flat" }),     // a wall-clock evaluation against the same reading: resolve read, none
                Tick(T(2), Ctx(T(0)) with { IobUnits = 4m, TrendBucket = "flat" }),     // none
                Tick(T(5), Ctx(T(5)) with { IobUnits = 2m, TrendBucket = "flat" }),     // body false: re-armed
                Tick(T(10), Ctx(T(10)) with { IobUnits = 4m, TrendBucket = "rising" }), // opens again; resolve false
                Tick(T(15), Ctx(T(15)) with { IobUnits = 2m, TrendBucket = "flat" }),   // hysteresis, then auto-resolved: body already false, so armed
                Tick(T(20), Ctx(T(20)) with { IobUnits = 4m, TrendBucket = "rising" }), // opens
            ]);

        yield return Scenario(
            "auto-resolve-relapse-reopens",
            "a rule awaiting re-arm evaluates its auto-resolve tree on every evaluation, under the same auto_resolve root and timers: a sustained resolve that keeps holding against the same reading opens nothing, and one that goes false while the body holds re-arms and opens in that evaluation",
            [Rule(1, "threshold", Low70, autoResolveParams: """
                {"type": "sustained", "sustained": {"minutes": 10, "child":
                    {"type": "threshold", "threshold": {"direction": "above", "value": 60}}}}
                """)],
            [
                Tick(T(0), Ctx(T(0), glucose: 65m)),   // opened; the resolve timer is set
                Tick(T(5), Ctx(T(5), glucose: 65m)),   // continues; resolve has held 5 of 10 minutes
                Tick(T(10), Ctx(T(10), glucose: 65m)), // resolve has held 10 minutes: auto-resolved, awaiting re-arm
                Tick(T(11), Ctx(T(10), glucose: 65m)), // wall-clock evaluations against the same reading: the resolve timer
                Tick(T(12), Ctx(T(10), glucose: 65m)), //   keeps running, so both trees hold and nothing opens
                Tick(T(15), Ctx(T(15), glucose: 66m)), // none
                Tick(T(20), Ctx(T(20), glucose: 45m)), // resolve false, body true: re-armed and opened in this evaluation
                Tick(T(25), Ctx(T(25), glucose: 45m)), // continues; resolve false
            ]);
    }
}
