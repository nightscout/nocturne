using Nocturne.Alerts.ParityCorpus.Generator.Harness;
using static Nocturne.Alerts.ParityCorpus.Generator.Scenarios.B;

namespace Nocturne.Alerts.ParityCorpus.Generator.Scenarios;

/// <summary>
/// Sustained-condition timer lifecycle and the (rule_id, path) key grammar the
/// alert_condition_timers table depends on.
/// </summary>
public static class SustainedScenarios
{
    public static IEnumerable<ScenarioFile> All()
    {
        yield return Scenario(
            "sustained-basic-window",
            "child true sets the timer and returns false; the window fires inclusively at (now - first) >= minutes; child false clears the timer and restarts",
            [Rule(1, "sustained", """
                {"minutes": 10, "child": {"type": "threshold", "threshold": {"direction": "below", "value": 70}}}
                """)],
            [
                Tick(T(0), Ctx(T(0), glucose: 65m)),    // set timer at T0, false
                Tick(T(5), Ctx(T(5), glucose: 66m)),    // 5m elapsed, false
                Tick(T(10), Ctx(T(10), glucose: 64m)),  // exactly 10m, true
                Tick(T(15), Ctx(T(15), glucose: 80m)),  // child false: clear, false
                Tick(T(20), Ctx(T(20), glucose: 65m)),  // restart: set at T20, false
                Tick(T(30), Ctx(T(30), glucose: 65m)),  // 10m again, true
            ]);

        yield return Scenario(
            "sustained-zero-and-negative-minutes",
            "minutes <= 0 disables the wrapper entirely (false, no timer activity)",
            [
                Rule(1, "sustained", """
                    {"minutes": 0, "child": {"type": "threshold", "threshold": {"direction": "below", "value": 70}}}
                    """),
                Rule(2, "sustained", """
                    {"minutes": -5, "child": {"type": "threshold", "threshold": {"direction": "below", "value": 70}}}
                    """),
            ],
            [Tick(T(0), Ctx(T(0), glucose: 65m))]);

        yield return Scenario(
            "sustained-missing-child",
            "a sustained payload without a child is false and never touches the timer store",
            [Rule(1, "sustained", """{"minutes": 10}""")],
            [Tick(T(0), Ctx(T(0), glucose: 65m))]);

        yield return Scenario(
            "sustained-sibling-paths",
            "two sustained siblings own distinct timer rows: composite[0].sustained vs composite[1].sustained",
            [Rule(1, "composite", """
                {"operator": "or", "conditions": [
                    {"type": "sustained", "sustained": {"minutes": 10, "child":
                        {"type": "threshold", "threshold": {"direction": "below", "value": 70}}}},
                    {"type": "sustained", "sustained": {"minutes": 20, "child":
                        {"type": "iob", "iob": {"operator": ">=", "value": 3}}}}
                ]}
                """)],
            [
                // Both children true: first sustained sets composite[0].sustained; because OR
                // short-circuits only on TRUE and the first sustained returns false on its set
                // tick, the second sustained also runs and sets composite[1].sustained.
                Tick(T(0), Ctx(T(0), glucose: 65m) with { IobUnits = 4m }),
                // 10 minutes later the first window has elapsed -> root true (short-circuit
                // now skips the second sustained, whose timer survives untouched).
                Tick(T(10), Ctx(T(10), glucose: 65m) with { IobUnits = 4m }),
                // Glucose recovers: first sustained clears; second sustained (20m elapsed) fires.
                Tick(T(20), Ctx(T(20), glucose: 100m) with { IobUnits = 4m }),
            ]);

        yield return Scenario(
            "sustained-inside-not",
            "a sustained nested under not keys its timer at not[0].sustained and the inversion applies to the windowed result",
            [Rule(1, "not", """
                {"child": {"type": "sustained", "sustained": {"minutes": 10, "child":
                    {"type": "threshold", "threshold": {"direction": "above", "value": 180}}}}}
                """)],
            [
                Tick(T(0), Ctx(T(0), glucose: 200m)),   // sustained sets timer, returns false -> not true
                Tick(T(10), Ctx(T(10), glucose: 200m)), // sustained true -> not false
                Tick(T(15), Ctx(T(15), glucose: 100m)), // child false, clear -> not true
            ]);

        yield return Scenario(
            "sustained-root-path",
            "a sustained at the rule root keys its timer under the bare root path 'sustained'",
            [Rule(1, "sustained", """
                {"minutes": 5, "child": {"type": "trend", "trend": {"bucket": "rising_fast"}}}
                """)],
            [
                Tick(T(0), Ctx(T(0)) with { TrendBucket = "rising_fast" }),
                Tick(T(5), Ctx(T(5)) with { TrendBucket = "rising_fast" }),
            ]);

        yield return Scenario(
            "sustained-path-preserves-json-casing",
            "path segments use the verbatim type string from the stored JSON: a child spelled 'Sustained' keys composite[0].Sustained",
            [Rule(1, "composite", """
                {"operator": "and", "conditions": [
                    {"type": "Sustained", "sustained": {"minutes": 5, "child":
                        {"type": "threshold", "threshold": {"direction": "below", "value": 70}}}}
                ]}
                """)],
            [
                Tick(T(0), Ctx(T(0), glucose: 65m)),
                Tick(T(5), Ctx(T(5), glucose: 65m)),
            ]);

        yield return Scenario(
            "sustained-nested-in-sustained",
            "sustained inside sustained: the outer window only starts once the inner one holds; paths nest as sustained[0].sustained",
            [Rule(1, "sustained", """
                {"minutes": 5, "child": {"type": "sustained", "sustained": {"minutes": 5, "child":
                    {"type": "threshold", "threshold": {"direction": "below", "value": 70}}}}}
                """)],
            [
                Tick(T(0), Ctx(T(0), glucose: 65m)),    // inner sets, inner false -> outer clears nothing (no timer), outer false
                Tick(T(5), Ctx(T(5), glucose: 65m)),    // inner true (5m); outer sets at T5, false
                Tick(T(10), Ctx(T(10), glucose: 65m)),  // outer 5m elapsed -> true
                Tick(T(15), Ctx(T(15), glucose: 90m)),  // child chain false: inner clears, outer clears
            ]);
    }
}
