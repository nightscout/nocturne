using Nocturne.Alerts.ParityCorpus.Generator.Harness;
using static Nocturne.Alerts.ParityCorpus.Generator.Scenarios.B;

namespace Nocturne.Alerts.ParityCorpus.Generator.Scenarios;

/// <summary>
/// tracker_age leaf: minutes since the active domain-tracker instance's reference
/// timestamp (start for Duration trackers, scheduled time for Event trackers), keyed by
/// tracker definition id. No active instance for the definition → false (fail closed,
/// deliberately opposite to time_since_last_* cold-start infinity).
/// </summary>
public static class TrackerAgeScenarios
{
    /// <summary>Fixed tracker-definition id used across these scenarios.</summary>
    private static readonly Guid Definition = Guid.Parse("00000000-0000-0000-0001-000000000001");

    private static string AgeParams(string op, int minutes) =>
        $$"""{"tracker_definition_id": "{{Definition}}", "operator": "{{op}}", "minutes": {{minutes}} }""";

    private static ScenarioContext WithTracker(DateTime at, DateTime referenceAt) =>
        Ctx(at) with
        {
            ActiveTrackers = [new ScenarioTrackerReference(Definition, referenceAt)],
        };

    public static IEnumerable<ScenarioFile> All()
    {
        // Duration tracker started 47h before T0; a 48h (2880 min) threshold crosses
        // between the second and third tick.
        var startedAt = T0.AddHours(-47);

        yield return Scenario(
            "tracker-age-crosses-threshold",
            "tracker_age >= 2880 opens once the active instance's age crosses 48h and continues while active",
            [Rule(1, "tracker_age", AgeParams(">=", 2880))],
            [
                Tick(T(0), WithTracker(T(0), startedAt)),     // 47h00 — false
                Tick(T(55), WithTracker(T(55), startedAt)),   // 47h55 — false
                Tick(T(65), WithTracker(T(65), startedAt)),   // 48h05 — opened
                Tick(T(70), WithTracker(T(70), startedAt)),   // continues
            ]);

        yield return Scenario(
            "tracker-age-instance-completed-closes",
            "completing the instance removes the definition from the context; the leaf goes false and the excursion closes",
            [Rule(1, "tracker_age", AgeParams(">=", 2880))],
            [
                Tick(T(0), WithTracker(T(0), T0.AddHours(-49))),  // opened
                Tick(T(5), Ctx(T(5))),                            // no active instance — hysteresis
                Tick(T(10), Ctx(T(10))),                          // hysteresis expiry — closed
            ]);

        yield return Scenario(
            "tracker-age-no-active-instance-false",
            "no active instance for the definition is false, not cold-start infinity",
            [Rule(1, "tracker_age", AgeParams(">=", 0))],
            [
                Tick(T(0), Ctx(T(0))),
                Tick(T(5), WithTracker(T(5), T0) with
                {
                    ActiveTrackers = [new ScenarioTrackerReference(
                        Guid.Parse("00000000-0000-0000-0001-000000000002"), T0.AddHours(-49))],
                }),
            ]);

        // Event tracker scheduled 12h after T0: elapsed is negative before the event, so
        // a negative minutes threshold expresses "within N minutes before the event".
        var scheduledAt = T0.AddHours(12);

        yield return Scenario(
            "tracker-age-negative-minutes-pre-event",
            "elapsed is negative before a scheduled event; >= -1440 is already true 12h out, >= -360 is not yet",
            [
                Rule(1, "tracker_age", AgeParams(">=", -1440)),
                Rule(2, "tracker_age", AgeParams(">=", -360)),
            ],
            [
                Tick(T(0), WithTracker(T(0), scheduledAt)),        // -720 min: rule1 true, rule2 false
                Tick(T(6 * 60 + 5), WithTracker(T(6 * 60 + 5), scheduledAt)), // -355 min: both true
            ]);

        yield return Scenario(
            "tracker-age-unknown-operator-false",
            "an unrecognised operator fails closed",
            [Rule(1, "tracker_age", AgeParams("~", 0))],
            [Tick(T(0), WithTracker(T(0), T0.AddHours(-49)))]);

        yield return Scenario(
            "tracker-age-inside-composite",
            "tracker_age composes like any leaf (and with time_of_day)",
            [
                Rule(1, "composite", $$"""
                    {"operator": "and", "conditions": [
                        {"type": "tracker_age", "tracker_age": {{AgeParams(">=", 2880)}} },
                        {"type": "time_of_day", "time_of_day": {"from": "10:00", "to": "14:00"} }
                    ]}
                    """),
            ],
            [
                Tick(T(0), WithTracker(T(0), T0.AddHours(-49))),  // 12:00 UTC — both true, opened
            ]);
    }
}
