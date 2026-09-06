using Nocturne.Alerts.ParityCorpus.Generator.Harness;
using static Nocturne.Alerts.ParityCorpus.Generator.Scenarios.B;

namespace Nocturne.Alerts.ParityCorpus.Generator.Scenarios;

/// <summary>
/// alert_state (cross-rule references) and the time_since_last_carb / time_since_last_bolus
/// leaves with their +infinity cold-start convention.
/// </summary>
public static class AlertStateAndTimeSinceScenarios
{
    public static IEnumerable<ScenarioFile> All()
    {
        var parent = RId(900);

        yield return Scenario(
            "alert-state-state-matching",
            "firing matches state == firing; unacknowledged additionally requires a null AcknowledgedAt; acknowledged only requires a non-null AcknowledgedAt; missing snapshot and unknown state are false",
            [
                Rule(1, "alert_state", $$"""{"alert_id": "{{parent}}", "state": "firing", "for_minutes": null}"""),
                Rule(2, "alert_state", $$"""{"alert_id": "{{parent}}", "state": "unacknowledged", "for_minutes": null}"""),
                Rule(3, "alert_state", $$"""{"alert_id": "{{parent}}", "state": "acknowledged", "for_minutes": null}"""),
                Rule(4, "alert_state", $$"""{"alert_id": "{{parent}}", "state": "escalated", "for_minutes": null}"""),
            ],
            [
                Tick(T(0), Ctx(T(0)) with
                {
                    ActiveAlerts = [new(parent, "firing", T(-10), null)],
                }),
                Tick(T(5), Ctx(T(5)) with
                {
                    ActiveAlerts = [new(parent, "firing", T(-15), T(-2))],
                }),
                Tick(T(10), Ctx(T(10))),
            ]);

        yield return Scenario(
            "alert-state-for-minutes-anchors",
            "for_minutes measures from TriggeredAt for firing/unacknowledged and from AcknowledgedAt for acknowledged, inclusive at the boundary",
            [
                Rule(1, "alert_state", $$"""{"alert_id": "{{parent}}", "state": "firing", "for_minutes": 20}"""),
                Rule(2, "alert_state", $$"""{"alert_id": "{{parent}}", "state": "acknowledged", "for_minutes": 20}"""),
            ],
            [
                // Triggered exactly 20m ago, acknowledged 5m ago: r1 true (anchor TriggeredAt), r2 false (anchor AcknowledgedAt).
                Tick(T(0), Ctx(T(0)) with
                {
                    ActiveAlerts = [new(parent, "firing", T(-20), T(-5))],
                }),
                // Acknowledged exactly 20m ago: r2 true.
                Tick(T(5), Ctx(T(5)) with
                {
                    ActiveAlerts = [new(parent, "firing", T(-40), T(-15))],
                }),
                // Triggered 19m ago: r1 false.
                Tick(T(10), Ctx(T(10)) with
                {
                    ActiveAlerts = [new(parent, "firing", T(-9), null)],
                }),
            ]);

        yield return Scenario(
            "time-since-last-carb-cold-start-infinity",
            "a missing carb anchor reads as +infinity minutes: > and >= fire on cold start, <, <= and == never do",
            [
                Rule(1, "time_since_last_carb", """{"operator": ">", "minutes": 120}"""),
                Rule(2, "time_since_last_carb", """{"operator": ">=", "minutes": 120}"""),
                Rule(3, "time_since_last_carb", """{"operator": "<", "minutes": 120}"""),
                Rule(4, "time_since_last_carb", """{"operator": "<=", "minutes": 120}"""),
                Rule(5, "time_since_last_carb", """{"operator": "==", "minutes": 120}"""),
            ],
            [
                Tick(T(0), Ctx(T(0))),                                          // no anchor: infinity
                Tick(T(5), Ctx(T(5)) with { LastCarbAt = T(5).AddMinutes(-120) }),  // exactly 120m
                Tick(T(10), Ctx(T(10)) with { LastCarbAt = T(10).AddMinutes(-30) }), // 30m
            ]);

        yield return Scenario(
            "time-since-last-bolus-boundaries",
            "same comparator as carbs against the bolus anchor",
            [
                Rule(1, "time_since_last_bolus", """{"operator": ">=", "minutes": 180}"""),
                Rule(2, "time_since_last_bolus", """{"operator": "<", "minutes": 180}"""),
            ],
            [
                Tick(T(0), Ctx(T(0))),                                              // infinity: r1 true, r2 false
                Tick(T(5), Ctx(T(5)) with { LastBolusAt = T(5).AddMinutes(-180) }),  // boundary: r1 true, r2 false
                Tick(T(10), Ctx(T(10)) with { LastBolusAt = T(10).AddMinutes(-10) }), // r1 false, r2 true
            ]);
    }
}
