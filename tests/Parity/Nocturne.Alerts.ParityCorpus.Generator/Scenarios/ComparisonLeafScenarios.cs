using Nocturne.Alerts.ParityCorpus.Generator.Harness;
using static Nocturne.Alerts.ParityCorpus.Generator.Scenarios.B;

namespace Nocturne.Alerts.ParityCorpus.Generator.Scenarios;

/// <summary>
/// Generated operator-boundary matrix for the simple ComparisonOps leaves
/// (iob, cob, reservoir, pump_battery, uploader_battery, sensitivity_ratio) plus the
/// elapsed-time comparison leaves (site_age in hours, sensor_age in days) and the
/// cold-start guard cases for the HasEver*-guarded kinds.
/// </summary>
public static class ComparisonLeafScenarios
{
    private static readonly string[] Operators = ["<", "<=", ">", ">=", "=="];

    private sealed record Kind(
        string Wire,
        decimal Threshold,
        // Builds a context carrying the given fact value (null = fact absent), with any
        // HasEver* guard satisfied.
        Func<DateTime, decimal?, ScenarioContext> With,
        // Builds a context where the fact value is present but the cold-start guard is
        // NOT satisfied; null for unguarded kinds.
        Func<DateTime, decimal, ScenarioContext>? GuardedColdStart);

    private static readonly Kind[] Kinds =
    [
        new("iob", 5m,
            (at, v) => Ctx(at) with { IobUnits = v }, null),
        new("cob", 30m,
            (at, v) => Ctx(at) with { CobGrams = v }, null),
        new("reservoir", 20m,
            (at, v) => Ctx(at) with { ReservoirUnits = v }, null),
        new("pump_battery", 25m,
            (at, v) => Ctx(at) with { PumpBatteryPercent = v, HasEverPumpSnapshot = true },
            (at, v) => Ctx(at) with { PumpBatteryPercent = v, HasEverPumpSnapshot = false }),
        new("uploader_battery", 25m,
            (at, v) => Ctx(at) with { UploaderBatteryPercent = v, HasEverUploaderSnapshot = true },
            (at, v) => Ctx(at) with { UploaderBatteryPercent = v, HasEverUploaderSnapshot = false }),
        new("sensitivity_ratio", 1.2m,
            (at, v) => Ctx(at) with { SensitivityRatio = v, HasEverApsSensitivity = true },
            (at, v) => Ctx(at) with { SensitivityRatio = v, HasEverApsSensitivity = false }),
    ];

    public static IEnumerable<ScenarioFile> All()
    {
        foreach (var kind in Kinds)
        {
            foreach (var op in Operators)
            {
                var below = kind.Threshold - 0.1m;
                var above = kind.Threshold + 0.1m;
                yield return Scenario(
                    $"{kind.Wire.Replace('_', '-')}-op-{OpSlug(op)}",
                    $"{kind.Wire} with operator '{op}' at value below/at/above the {kind.Threshold} threshold, then with the fact absent",
                    [Rule(1, kind.Wire, $$"""{"operator": "{{op}}", "value": {{kind.Threshold}}}""")],
                    [
                        Tick(T(0), kind.With(T(0), below)),
                        Tick(T(5), kind.With(T(5), kind.Threshold)),
                        Tick(T(10), kind.With(T(10), above)),
                        Tick(T(15), kind.With(T(15), null)),
                    ]);
            }

            if (kind.GuardedColdStart is { } guarded)
            {
                yield return Scenario(
                    $"{kind.Wire.Replace('_', '-')}-cold-start-guard",
                    $"{kind.Wire} returns false when its HasEver* guard is unset even though the fact value would match",
                    [Rule(1, kind.Wire, $$"""{"operator": ">=", "value": 0}""")],
                    [Tick(T(0), guarded(T(0), kind.Threshold))]);
            }
        }

        // A lower-bound reservoir reading ("50+") only proves the reservoir holds at
        // least that much: > / >= compare against the bound; < / <= / == are false.
        yield return Scenario(
            "reservoir-lower-bound",
            "reservoir_is_lower_bound gates < / <= / == to false while > and >= compare against the bound; exact readings are unaffected",
            [
                Rule(1, "reservoir", """{"operator": "<", "value": 60}"""),
                Rule(2, "reservoir", """{"operator": "<=", "value": 60}"""),
                Rule(3, "reservoir", """{"operator": "==", "value": 50}"""),
                Rule(4, "reservoir", """{"operator": ">", "value": 40}"""),
                Rule(5, "reservoir", """{"operator": ">=", "value": 50}"""),
                Rule(6, "reservoir", """{"operator": ">", "value": 50}"""),
            ],
            [
                Tick(T(0), Ctx(T(0)) with { ReservoirUnits = 50m, ReservoirIsLowerBound = true }),
                Tick(T(5), Ctx(T(5)) with { ReservoirUnits = 42m }),
            ]);

        // site_age compares HOURS since LastSiteChangeAt.
        yield return Scenario(
            "site-age-hours-boundaries",
            "site_age compares hours since last site change: 72h-old site at threshold 72 for >=, >, <; null anchor false",
            [
                Rule(1, "site_age", """{"operator": ">=", "value": 72}"""),
                Rule(2, "site_age", """{"operator": ">", "value": 72}"""),
                Rule(3, "site_age", """{"operator": "<", "value": 72}"""),
            ],
            [
                Tick(T(0), Ctx(T(0)) with { LastSiteChangeAt = T(0).AddHours(-72) }),
                Tick(T(5), Ctx(T(5)) with { LastSiteChangeAt = T(5).AddHours(-71) }),
                Tick(T(10), Ctx(T(10))),
            ]);

        // sensor_age compares DAYS since LastSensorStartAt.
        yield return Scenario(
            "sensor-age-days-boundaries",
            "sensor_age compares days since sensor start: exactly 10 days at threshold 10; fractional day below; null anchor false",
            [
                Rule(1, "sensor_age", """{"operator": ">=", "value": 10}"""),
                Rule(2, "sensor_age", """{"operator": "<", "value": 10}"""),
            ],
            [
                Tick(T(0), Ctx(T(0)) with { LastSensorStartAt = T(0).AddDays(-10) }),
                Tick(T(5), Ctx(T(5)) with { LastSensorStartAt = T(5).AddDays(-9).AddHours(-12) }),
                Tick(T(10), Ctx(T(10))),
            ]);
    }

    private static string OpSlug(string op) => op switch
    {
        "<" => "lt",
        "<=" => "lte",
        ">" => "gt",
        ">=" => "gte",
        "==" => "eq",
        _ => "unknown",
    };
}
