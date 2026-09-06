using Nocturne.Alerts.ParityCorpus.Generator.Harness;
using static Nocturne.Alerts.ParityCorpus.Generator.Scenarios.B;

namespace Nocturne.Alerts.ParityCorpus.Generator.Scenarios;

/// <summary>
/// Glucose-derived leaves: threshold, rate_of_change, staleness, predicted, trend,
/// glucose_bucket, and decimal-precision boundary cases.
/// </summary>
public static class GlucoseLeafScenarios
{
    public static IEnumerable<ScenarioFile> All()
    {
        // ---- threshold -------------------------------------------------------------
        yield return Scenario(
            "threshold-below-boundaries",
            "below is strictly less-than: 69.9 fires, 70 and 70.1 do not; null glucose is false",
            [Rule(1, "threshold", """{"direction": "below", "value": 70}""")],
            [
                Tick(T(0), Ctx(T(0), glucose: 69.9m)),
                Tick(T(5), Ctx(T(5), glucose: 70m)),
                Tick(T(10), Ctx(T(10), glucose: 70.1m)),
                Tick(T(15), Ctx(T(15), glucose: null)),
            ]);

        yield return Scenario(
            "threshold-above-boundaries",
            "above is strictly greater-than: 250 does not fire, 250.1 does",
            [Rule(1, "threshold", """{"direction": "above", "value": 250}""")],
            [
                Tick(T(0), Ctx(T(0), glucose: 249.9m)),
                Tick(T(5), Ctx(T(5), glucose: 250m)),
                Tick(T(10), Ctx(T(10), glucose: 250.1m)),
            ]);

        yield return Scenario(
            "threshold-unknown-direction",
            "an unrecognised direction string evaluates false regardless of glucose",
            [Rule(1, "threshold", """{"direction": "sideways", "value": 70}""")],
            [Tick(T(0), Ctx(T(0), glucose: 40m))]);

        yield return Scenario(
            "threshold-case-insensitive-direction",
            "direction is lowercased before matching: BELOW behaves as below",
            [Rule(1, "threshold", """{"direction": "BELOW", "value": 70}""")],
            [Tick(T(0), Ctx(T(0), glucose: 65m))]);

        yield return Scenario(
            "decimal-equality-trailing-zeros",
            "decimal semantics: 5.50 threshold equals a 5.5 reading for ==, and 70.00 boundary is exact",
            [
                Rule(1, "iob", """{"operator": "==", "value": 5.50}"""),
                Rule(2, "threshold", """{"direction": "below", "value": 70.00}"""),
            ],
            [
                Tick(T(0), Ctx(T(0)) with { IobUnits = 5.5m }),
                Tick(T(5), Ctx(T(5), glucose: 69.999m) with { IobUnits = 5.500m }),
            ]);

        yield return Scenario(
            "decimal-high-precision-boundary",
            "high-precision decimal comparison: 100.0001 vs threshold 100.0001 across >=, >, ==",
            [
                Rule(1, "iob", """{"operator": ">=", "value": 100.0001}"""),
                Rule(2, "iob", """{"operator": ">", "value": 100.0001}"""),
                Rule(3, "iob", """{"operator": "==", "value": 100.0001}"""),
            ],
            [Tick(T(0), Ctx(T(0)) with { IobUnits = 100.0001m })]);

        // ---- rate_of_change ----------------------------------------------------------
        yield return Scenario(
            "rate-of-change-boundaries",
            "rising fires at rate >= threshold (inclusive); falling fires at rate <= -threshold (inclusive); null rate is false",
            [
                Rule(1, "rate_of_change", """{"direction": "rising", "rate": 3}"""),
                Rule(2, "rate_of_change", """{"direction": "falling", "rate": 3}"""),
            ],
            [
                Tick(T(0), Ctx(T(0), trendRate: 3m)),
                Tick(T(5), Ctx(T(5), trendRate: 2.99m)),
                Tick(T(10), Ctx(T(10), trendRate: -3m)),
                Tick(T(15), Ctx(T(15), trendRate: -2.99m)),
                Tick(T(20), Ctx(T(20), trendRate: null)),
            ]);

        yield return Scenario(
            "rate-of-change-unknown-direction",
            "an unrecognised direction evaluates false",
            [Rule(1, "rate_of_change", """{"direction": "diagonal", "rate": 1}""")],
            [Tick(T(0), Ctx(T(0), trendRate: 5m))]);

        // ---- staleness -----------------------------------------------------------------
        yield return Scenario(
            "staleness-operator-boundaries",
            "elapsed-minutes comparison for each operator at the exact boundary (reading 15 minutes old)",
            [
                Rule(1, "staleness", """{"operator": ">", "value": 15}"""),
                Rule(2, "staleness", """{"operator": ">=", "value": 15}"""),
                Rule(3, "staleness", """{"operator": "<", "value": 15}"""),
                Rule(4, "staleness", """{"operator": "<=", "value": 15}"""),
                Rule(5, "staleness", """{"operator": "==", "value": 15}"""),
            ],
            [
                Tick(T(15), Ctx(T(15)) with { LastReadingAt = T(0), LatestTimestamp = T(0) }),
                Tick(T(31), Ctx(T(31)) with { LastReadingAt = T(0), LatestTimestamp = T(0) }),
            ]);

        yield return Scenario(
            "staleness-cold-start",
            "both LastReadingAt and LatestTimestamp null: false for every operator (cold-start short-circuit)",
            [
                Rule(1, "staleness", """{"operator": ">", "value": 15}"""),
                Rule(2, "staleness", """{"operator": "<", "value": 15}"""),
            ],
            [Tick(T(0), Empty())]);

        yield return Scenario(
            "staleness-infinity-convention",
            "LastReadingAt null but LatestTimestamp set: elapsed is +infinity — > and >= true, <, <= and == false",
            [
                Rule(1, "staleness", """{"operator": ">", "value": 15}"""),
                Rule(2, "staleness", """{"operator": ">=", "value": 15}"""),
                Rule(3, "staleness", """{"operator": "<", "value": 15}"""),
                Rule(4, "staleness", """{"operator": "<=", "value": 15}"""),
                Rule(5, "staleness", """{"operator": "==", "value": 15}"""),
            ],
            [Tick(T(0), Empty() with { LatestTimestamp = T(-30) })]);

        yield return Scenario(
            "staleness-unknown-operator",
            "an unrecognised operator string evaluates false",
            [Rule(1, "staleness", """{"operator": "!=", "value": 15}""")],
            [Tick(T(30), Ctx(T(30)) with { LastReadingAt = T(0), LatestTimestamp = T(0) })]);

        // ---- predicted -----------------------------------------------------------------
        yield return Scenario(
            "predicted-within-horizon",
            "any prediction at offset <= within_minutes satisfying the comparison fires; points beyond the horizon are ignored",
            [Rule(1, "predicted", """{"operator": "<", "value": 70, "within_minutes": 30}""")],
            [
                Tick(T(0), Ctx(T(0)) with
                {
                    Predictions = [new(15, 65m), new(45, 50m)],
                }),
                Tick(T(5), Ctx(T(5)) with
                {
                    Predictions = [new(45, 50m)],
                }),
                Tick(T(10), Ctx(T(10)) with
                {
                    Predictions = [new(30, 69.9m)],
                }),
                Tick(T(15), Ctx(T(15))),
            ]);

        yield return Scenario(
            "predicted-horizon-boundary",
            "offset exactly equal to within_minutes is inside the horizon; offset one past is outside",
            [Rule(1, "predicted", """{"operator": ">=", "value": 180, "within_minutes": 20}""")],
            [
                Tick(T(0), Ctx(T(0)) with { Predictions = [new(20, 180m)] }),
                Tick(T(5), Ctx(T(5)) with { Predictions = [new(21, 300m)] }),
            ]);

        // ---- trend ------------------------------------------------------------------
        yield return Scenario(
            "trend-bucket-matching",
            "wire-form bucket comparison is case-insensitive; null context bucket and unknown configured bucket are false",
            [
                Rule(1, "trend", """{"bucket": "falling_fast"}"""),
                Rule(2, "trend", """{"bucket": "FALLING_FAST"}"""),
                Rule(3, "trend", """{"bucket": "plummeting"}"""),
            ],
            [
                Tick(T(0), Ctx(T(0)) with { TrendBucket = "falling_fast" }),
                Tick(T(5), Ctx(T(5)) with { TrendBucket = "rising" }),
                Tick(T(10), Ctx(T(10))),
            ]);

        // ---- glucose_bucket --------------------------------------------------------
        yield return Scenario(
            "glucose-bucket-membership",
            "set membership over the enricher-computed bucket; null bucket and empty bucket list are false",
            [
                Rule(1, "glucose_bucket", """{"buckets": ["very_low", "low"]}"""),
                Rule(2, "glucose_bucket", """{"buckets": []}"""),
            ],
            [
                Tick(T(0), Ctx(T(0), glucose: 60m) with { GlucoseBucket = "low" }),
                Tick(T(5), Ctx(T(5), glucose: 120m) with { GlucoseBucket = "tight_range" }),
                Tick(T(10), Ctx(T(10), glucose: 50m) with { GlucoseBucket = "very_low" }),
                Tick(T(15), Ctx(T(15))),
            ]);
    }
}
