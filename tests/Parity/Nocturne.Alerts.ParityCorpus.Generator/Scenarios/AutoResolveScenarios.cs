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
            "auto-resolve-only-while-excursion-active",
            "the resolve tree is only consulted while the tracker holds an active excursion (active or hysteresis)",
            [Rule(1, "threshold", Low70, autoResolveParams: """
                {"type": "trend", "trend": {"bucket": "flat"}}
                """)],
            [
                Tick(T(0), Ctx(T(0), glucose: 100m) with { TrendBucket = "flat" }),  // idle: resolve pass does nothing
                Tick(T(5), Ctx(T(5), glucose: 65m) with { TrendBucket = "flat" }),   // opened, then resolve true -> closed same tick
                Tick(T(10), Ctx(T(10), glucose: 65m) with { TrendBucket = "flat" }), // re-opens and closes again
            ]);
    }
}
