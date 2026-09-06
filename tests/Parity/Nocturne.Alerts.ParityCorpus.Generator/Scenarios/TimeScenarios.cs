using Nocturne.Alerts.ParityCorpus.Generator.Harness;
using static Nocturne.Alerts.ParityCorpus.Generator.Scenarios.B;

namespace Nocturne.Alerts.ParityCorpus.Generator.Scenarios;

/// <summary>
/// time_of_day and day_of_week: window boundaries, midnight wrap, timezone resolution
/// order (per-rule → tenant → UTC), failure modes, and DST transitions.
/// </summary>
public static class TimeScenarios
{
    public static IEnumerable<ScenarioFile> All()
    {
        // T0 is Monday 2026-01-05 12:00 UTC.

        yield return Scenario(
            "time-of-day-half-open-window",
            "[from, to) in UTC: current == from is inside, current == to is outside",
            [Rule(1, "time_of_day", """{"from": "12:00", "to": "13:00", "timezone": null}""")],
            [
                Tick(T0, Ctx(T0)),                       // 12:00 — inside (>= from)
                Tick(T(30), Ctx(T(30))),                 // 12:30 — inside
                Tick(T(60), Ctx(T(60))),                 // 13:00 — outside (< to fails)
                Tick(T(-1), Ctx(T(-1))),                 // 11:59 — outside
            ]);

        yield return Scenario(
            "time-of-day-midnight-wrap",
            "from > to wraps midnight: 22:00-06:00 covers both legs, excludes the daytime gap",
            [Rule(1, "time_of_day", """{"from": "22:00", "to": "06:00", "timezone": null}""")],
            [
                Tick(T0.AddHours(11), Ctx(T0.AddHours(11))),   // 23:00 — inside (first leg)
                Tick(T0.AddHours(17), Ctx(T0.AddHours(17))),   // 05:00 next day — inside (second leg)
                Tick(T0.AddHours(18), Ctx(T0.AddHours(18))),   // 06:00 — outside (== to)
                Tick(T0, Ctx(T0)),                             // 12:00 — outside
            ]);

        yield return Scenario(
            "time-of-day-timezone-resolution",
            "per-rule tz wins over tenant tz; absent per-rule tz falls back to tenant; both absent means UTC",
            [
                Rule(1, "time_of_day", """{"from": "22:00", "to": "23:59", "timezone": "Pacific/Auckland"}"""),
                Rule(2, "time_of_day", """{"from": "22:00", "to": "23:59", "timezone": null}"""),
                Rule(3, "time_of_day", """{"from": "12:00", "to": "13:00", "timezone": null}"""),
            ],
            [
                // 12:00 UTC on Jan 5 = 01:00 Jan 6 in Auckland (UTC+13, NZDT).
                // Rule 1 (Auckland window 22:00-23:59): outside.
                // Rule 2 with tenant tz Auckland: outside. Rule 3 with tenant tz Auckland: outside (01:00 local).
                Tick(T0, Ctx(T0) with { TenantTimeZoneId = "Pacific/Auckland" }),
                // 09:30 UTC = 22:30 Auckland: rules 1 and 2 inside; rule 3 outside.
                Tick(T0.AddHours(-2).AddMinutes(-30), Ctx(T0.AddHours(-2).AddMinutes(-30)) with { TenantTimeZoneId = "Pacific/Auckland" }),
                // No tenant tz: rule 3 (12:00-13:00 against UTC) inside at 12:00 UTC.
                Tick(T0, Ctx(T0)),
            ]);

        yield return Scenario(
            "time-of-day-bad-timezones",
            "an unknown per-rule timezone fails closed (false); an unknown tenant timezone falls back to UTC",
            [
                Rule(1, "time_of_day", """{"from": "12:00", "to": "13:00", "timezone": "Mars/Olympus_Mons"}"""),
                Rule(2, "time_of_day", """{"from": "12:00", "to": "13:00", "timezone": null}"""),
            ],
            [Tick(T0, Ctx(T0) with { TenantTimeZoneId = "Atlantis/Lost_City" })]);

        yield return Scenario(
            "time-of-day-malformed-times",
            "unparseable from/to strings (not exact HH:mm) evaluate false",
            [
                Rule(1, "time_of_day", """{"from": "12", "to": "13:00", "timezone": null}"""),
                Rule(2, "time_of_day", """{"from": "12:00:00", "to": "13:00", "timezone": null}"""),
                Rule(3, "time_of_day", """{"from": "noon", "to": "13:00", "timezone": null}"""),
            ],
            [Tick(T(30), Ctx(T(30)))]);

        // Europe/London springs forward 2026-03-29 01:00 GMT -> 02:00 BST.
        yield return Scenario(
            "time-of-day-dst-spring-forward",
            "Europe/London on 2026-03-29: 01:30 local does not exist; 00:30 UTC is 01:30 GMT pre-jump? No — at 00:30 UTC it is 00:30 GMT (window outside); at 01:30 UTC it is 02:30 BST (inside the 02:00-03:00 window even though only 30 utc-minutes after the 01:00-02:00 check)",
            [
                Rule(1, "time_of_day", """{"from": "01:00", "to": "02:00", "timezone": "Europe/London"}"""),
                Rule(2, "time_of_day", """{"from": "02:00", "to": "03:00", "timezone": "Europe/London"}"""),
            ],
            [
                Tick(new DateTime(2026, 3, 29, 0, 30, 0, DateTimeKind.Utc), Ctx(new DateTime(2026, 3, 29, 0, 30, 0, DateTimeKind.Utc))),
                Tick(new DateTime(2026, 3, 29, 1, 30, 0, DateTimeKind.Utc), Ctx(new DateTime(2026, 3, 29, 1, 30, 0, DateTimeKind.Utc))),
            ]);

        // Europe/London falls back 2026-10-25 02:00 BST -> 01:00 GMT (01:00-02:00 local repeats).
        yield return Scenario(
            "time-of-day-dst-fall-back",
            "Europe/London on 2026-10-25: the 01:00-02:00 local window is hit twice (00:30 UTC = 01:30 BST, 01:30 UTC = 01:30 GMT)",
            [Rule(1, "time_of_day", """{"from": "01:00", "to": "02:00", "timezone": "Europe/London"}""")],
            [
                Tick(new DateTime(2026, 10, 25, 0, 30, 0, DateTimeKind.Utc), Ctx(new DateTime(2026, 10, 25, 0, 30, 0, DateTimeKind.Utc))),
                Tick(new DateTime(2026, 10, 25, 1, 30, 0, DateTimeKind.Utc), Ctx(new DateTime(2026, 10, 25, 1, 30, 0, DateTimeKind.Utc))),
                Tick(new DateTime(2026, 10, 25, 2, 30, 0, DateTimeKind.Utc), Ctx(new DateTime(2026, 10, 25, 2, 30, 0, DateTimeKind.Utc))),
            ]);

        // ---- day_of_week -------------------------------------------------------------
        yield return Scenario(
            "day-of-week-basic",
            "Monday matches [monday]; mismatch on [tuesday]; empty days list is false",
            [
                Rule(1, "day_of_week", """{"days": ["monday"]}"""),
                Rule(2, "day_of_week", """{"days": ["tuesday"]}"""),
                Rule(3, "day_of_week", """{"days": []}"""),
            ],
            [Tick(T0, Ctx(T0))]);

        yield return Scenario(
            "day-of-week-integer-wire-form",
            "days accept integer indices (0=Sunday): [1] matches Monday",
            [Rule(1, "day_of_week", """{"days": [1]}""")],
            [Tick(T0, Ctx(T0))]);

        yield return Scenario(
            "day-of-week-timezone-day-flip",
            "Monday 12:00 UTC is already Tuesday 01:00 in Auckland; unknown tenant tz falls back to UTC (Monday)",
            [
                Rule(1, "day_of_week", """{"days": ["tuesday"]}"""),
                Rule(2, "day_of_week", """{"days": ["monday"]}"""),
            ],
            [
                Tick(T0, Ctx(T0) with { TenantTimeZoneId = "Pacific/Auckland" }),
                Tick(T0, Ctx(T0) with { TenantTimeZoneId = "Narnia/Lamp_Post" }),
            ]);
    }
}
