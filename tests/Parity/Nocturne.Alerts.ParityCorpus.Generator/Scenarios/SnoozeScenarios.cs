using Nocturne.Alerts.ParityCorpus.Generator.Harness;
using static Nocturne.Alerts.ParityCorpus.Generator.Scenarios.B;

namespace Nocturne.Alerts.ParityCorpus.Generator.Scenarios;

/// <summary>
/// Smart-snooze conditions: the auxiliary evaluation scope the sweep runs under the
/// reserved <c>snooze</c> path root, with the configured conditions wrapped as
/// <c>composite{and, conditions}</c>. Only the predicate is pinned here — the extend/clear
/// policy around it (max counts, extend minutes, the trend heuristic used when no
/// conditions are configured) is host-side.
/// </summary>
public static class SnoozeScenarios
{
    private const string Low70 = """{"direction": "below", "value": 70}""";

    public static IEnumerable<ScenarioFile> All()
    {
        yield return Scenario(
            "snooze-sustained-separate-timer",
            "a sustained in the snooze conditions keys its timer under the snooze path root, so an identically shaped sustained in the rule body cannot share its row",
            [Rule(1, "sustained", """
                    {"minutes": 10, "child": {"type": "threshold", "threshold": {"direction": "below", "value": 70}}}
                    """,
                snoozeConditions: [
                    """
                    {"type": "sustained", "sustained": {"minutes": 10, "child":
                        {"type": "threshold", "threshold": {"direction": "below", "value": 70}}}}
                    """,
                ])],
            [
                // Both trees see the same true child and both set a timer: two set ops, at
                // 'sustained' (the body's root) and 'snooze[0].sustained'. One op here would
                // mean the roots collided.
                Tick(T(0), Ctx(T(0), glucose: 65m)),
                Tick(T(5), Ctx(T(5), glucose: 65m)),   // 5m of 10m on both: still false
                Tick(T(10), Ctx(T(10), glucose: 65m)), // 10m elapsed: body opens, snooze extends
                Tick(T(15), Ctx(T(15), glucose: 100m)), // child false: both timers cleared
            ]);

        yield return Scenario(
            "snooze-composite-and-short-circuit",
            "the conditions array is wrapped in composite{and}, so a false leading condition short-circuits the rest — a skipped sustained neither sets nor clears its timer",
            [Rule(1, "threshold", Low70, snoozeConditions: [
                """{"type": "threshold", "threshold": {"direction": "above", "value": 100}}""",
                """
                {"type": "sustained", "sustained": {"minutes": 5, "child":
                    {"type": "threshold", "threshold": {"direction": "above", "value": 200}}}}
                """,
            ])],
            [
                Tick(T(0), Ctx(T(0), glucose: 65m)),    // first condition false -> sustained never evaluated
                Tick(T(5), Ctx(T(5), glucose: 150m)),   // first true; sustained child false -> clear (no timer, no op)
                Tick(T(10), Ctx(T(10), glucose: 250m)), // sustained child true -> sets 'snooze[1].sustained'
                Tick(T(15), Ctx(T(15), glucose: 250m)), // 5m elapsed -> both conditions true -> extend
                Tick(T(20), Ctx(T(20), glucose: 50m)),  // short-circuit: the sustained timer survives untouched
                Tick(T(25), Ctx(T(25), glucose: 250m)), // still timing from T10, not T25 -> extend again
            ]);

        yield return Scenario(
            "snooze-unknown-kinds",
            "silent-false and the not-over-unknown inversion hold under the snooze root exactly as they do in a rule body",
            [
                Rule(1, "threshold", Low70, name: "unknown-child", snoozeConditions: [
                    """{"type": "nope"}""",
                ]),
                Rule(2, "threshold", Low70, name: "not-over-unknown", snoozeConditions: [
                    """{"type": "not", "not": {"child": {"type": "nope"}}}""",
                ]),
            ],
            [
                Tick(T(0), Ctx(T(0), glucose: 65m)),
                Tick(T(5), Ctx(T(5), glucose: 65m)),
            ]);

        // Caveat for anyone reading this as a worked example rather than an engine pin:
        // AlertSweepService.BuildSnoozeContextAsync sets LatestValue = null and the enricher
        // never fills it in, so the threshold half of a predicate like this is always false
        // in production today. The rate_of_change half is the only part that can fire. The
        // scenario still pins what the engines must do with a populated context, which is
        // what the corpus is for — and it is the regression test if that gap is closed.
        yield return Scenario(
            "snooze-trend-recovering-predicate",
            "a multi-leaf snooze predicate over trend and glucose: extend only while glucose is climbing AND has cleared a floor, so a low that is rising but still deep re-fires",
            [Rule(1, "threshold", Low70, snoozeConditions: [
                """{"type": "rate_of_change", "rate_of_change": {"direction": "rising", "rate": 0.5}}""",
                """{"type": "threshold", "threshold": {"direction": "above", "value": 60}}""",
            ])],
            [
                Tick(T(0), Ctx(T(0), glucose: 65m, trendRate: 0m)),    // flat: not recovering
                Tick(T(5), Ctx(T(5), glucose: 62m, trendRate: -1m)),   // still falling
                Tick(T(10), Ctx(T(10), glucose: 64m, trendRate: 0.8m)), // rising and above the floor: extend
                Tick(T(15), Ctx(T(15), glucose: 58m, trendRate: 1m)),  // rising but below the floor: re-fire
            ]);
    }
}
