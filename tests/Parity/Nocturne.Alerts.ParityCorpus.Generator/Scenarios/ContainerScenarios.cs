using Nocturne.Alerts.ParityCorpus.Generator.Harness;
using static Nocturne.Alerts.ParityCorpus.Generator.Scenarios.B;

namespace Nocturne.Alerts.ParityCorpus.Generator.Scenarios;

/// <summary>
/// Containers: composite (and/or, short-circuit, malformed), not (inversion, missing
/// child, not-over-unknown), sustained (timer lifecycle, paths), nesting, and the
/// path-casing quirk.
/// </summary>
public static class ContainerScenarios
{
    public static IEnumerable<ScenarioFile> All()
    {
        yield return Scenario(
            "composite-and-or",
            "and requires all children true; or requires any; empty conditions and unknown operators are false",
            [
                Rule(1, "composite", """
                    {"operator": "and", "conditions": [
                        {"type": "threshold", "threshold": {"direction": "below", "value": 70}},
                        {"type": "trend", "trend": {"bucket": "falling"}}
                    ]}
                    """),
                Rule(2, "composite", """
                    {"operator": "or", "conditions": [
                        {"type": "threshold", "threshold": {"direction": "below", "value": 70}},
                        {"type": "trend", "trend": {"bucket": "falling"}}
                    ]}
                    """),
                Rule(3, "composite", """{"operator": "and", "conditions": []}"""),
                Rule(4, "composite", """
                    {"operator": "xor", "conditions": [
                        {"type": "threshold", "threshold": {"direction": "below", "value": 200}}
                    ]}
                    """),
            ],
            [
                Tick(T(0), Ctx(T(0), glucose: 65m) with { TrendBucket = "falling" }),   // and true, or true
                Tick(T(5), Ctx(T(5), glucose: 65m) with { TrendBucket = "flat" }),      // and false, or true
                Tick(T(10), Ctx(T(10), glucose: 100m) with { TrendBucket = "flat" }),   // both false
            ]);

        yield return Scenario(
            "composite-operator-case-insensitive",
            "the composite operator is lowercased before matching: AND and Or work",
            [
                Rule(1, "composite", """
                    {"operator": "AND", "conditions": [
                        {"type": "threshold", "threshold": {"direction": "below", "value": 70}}
                    ]}
                    """),
                Rule(2, "composite", """
                    {"operator": "Or", "conditions": [
                        {"type": "threshold", "threshold": {"direction": "below", "value": 70}}
                    ]}
                    """),
            ],
            [Tick(T(0), Ctx(T(0), glucose: 65m))]);

        yield return Scenario(
            "composite-short-circuit-skips-sustained",
            "an AND short-circuits at the first false child, so a sustained child after it is neither set nor cleared — its timer survives the skipped tick",
            [
                Rule(1, "composite", """
                    {"operator": "and", "conditions": [
                        {"type": "threshold", "threshold": {"direction": "below", "value": 70}},
                        {"type": "sustained", "sustained": {"minutes": 10, "child":
                            {"type": "trend", "trend": {"bucket": "falling"}}}}
                    ]}
                    """),
            ],
            [
                // Tick 0: both children evaluated; sustained sets its timer (trend falling), root false (sustained not yet elapsed).
                Tick(T(0), Ctx(T(0), glucose: 65m) with { TrendBucket = "falling" }),
                // Tick 1: glucose back above 70 -> AND short-circuits; the sustained child is skipped, timer NOT cleared.
                Tick(T(5), Ctx(T(5), glucose: 100m) with { TrendBucket = "rising" }),
                // Tick 2: glucose low again; sustained finds its 10-minute-old timer still set -> fires immediately.
                Tick(T(10), Ctx(T(10), glucose: 65m) with { TrendBucket = "falling" }),
            ]);

        yield return Scenario(
            "or-short-circuit-skips-sustained",
            "an OR short-circuits at the first true child; a sustained sibling after it is not evaluated that tick",
            [
                Rule(1, "composite", """
                    {"operator": "or", "conditions": [
                        {"type": "threshold", "threshold": {"direction": "below", "value": 70}},
                        {"type": "sustained", "sustained": {"minutes": 5, "child":
                            {"type": "trend", "trend": {"bucket": "falling"}}}}
                    ]}
                    """),
            ],
            [
                // Trend falling but glucose also low: OR short-circuits at the threshold, sustained never starts.
                Tick(T(0), Ctx(T(0), glucose: 65m) with { TrendBucket = "falling" }),
                // Glucose normal: sustained is now evaluated for the first time, sets its timer, root false.
                Tick(T(5), Ctx(T(5), glucose: 100m) with { TrendBucket = "falling" }),
                // Five minutes later the sustained window has elapsed: root true.
                Tick(T(10), Ctx(T(10), glucose: 100m) with { TrendBucket = "falling" }),
            ]);

        yield return Scenario(
            "not-inversion-and-edge-cases",
            "not inverts its child; a missing child is false (not true); not over an unknown child kind is true (child silently false)",
            [
                Rule(1, "not", """{"child": {"type": "threshold", "threshold": {"direction": "below", "value": 70}}}"""),
                Rule(2, "not", """{}"""),
                Rule(3, "not", """{"child": {"type": "quantum_flux", "threshold": {"direction": "below", "value": 70}}}"""),
            ],
            [
                Tick(T(0), Ctx(T(0), glucose: 65m)),    // r1: child true -> false; r2 false; r3 true
                Tick(T(5), Ctx(T(5), glucose: 100m)),   // r1: child false -> true
            ]);

        yield return Scenario(
            "nested-composite-not",
            "three-level nesting: and(or(low, falling), not(override_active)) with leaf ids assigned in pre-order",
            [
                Rule(1, "composite", """
                    {"operator": "and", "conditions": [
                        {"type": "composite", "composite": {"operator": "or", "conditions": [
                            {"type": "threshold", "threshold": {"direction": "below", "value": 70}},
                            {"type": "trend", "trend": {"bucket": "falling_fast"}}
                        ]}},
                        {"type": "not", "not": {"child":
                            {"type": "override_active", "override_active": {"is_active": true, "for_minutes": null}}}}
                    ]}
                    """),
            ],
            [
                Tick(T(0), Ctx(T(0), glucose: 65m)),                                        // low, no override -> true
                Tick(T(5), Ctx(T(5), glucose: 65m) with { ActiveOverride = new(T(0)) }),    // low but override -> false
                Tick(T(10), Ctx(T(10), glucose: 100m) with { TrendBucket = "falling_fast" }), // falling fast, no override -> true
                Tick(T(15), Ctx(T(15), glucose: 100m)),                                     // neither -> false
            ]);

        yield return Scenario(
            "malformed-container-payloads",
            "a container whose payload is missing is treated as a leaf (gets a leaf id) and evaluates false",
            [
                Rule(1, "composite", """
                    {"operator": "and", "conditions": [
                        {"type": "sustained"},
                        {"type": "composite"},
                        {"type": "not"}
                    ]}
                    """),
            ],
            [Tick(T(0), Ctx(T(0), glucose: 65m))]);

        yield return Scenario(
            "unknown-and-signal-loss-in-tree",
            "unknown node kinds and signal_loss nodes inside a tree evaluate false (signal_loss has no evaluator)",
            [
                Rule(1, "composite", """
                    {"operator": "or", "conditions": [
                        {"type": "warp_drive", "threshold": {"direction": "below", "value": 70}},
                        {"type": "signal_loss", "signal_loss": {"timeout_minutes": 15}}
                    ]}
                    """),
            ],
            [Tick(T(0), Ctx(T(0), glucose: 40m) with { LastReadingAt = T(-60), LatestTimestamp = T(-60) })]);

        yield return Scenario(
            "signal-loss-root-rule-skipped",
            "a rule whose root condition type is signal_loss has no evaluator: the orchestrator skips it entirely (no tracker transition)",
            [Rule(1, "signal_loss", """{"timeout_minutes": 15}""")],
            [Tick(T(0), Ctx(T(0)) with { LastReadingAt = T(-60), LatestTimestamp = T(-60) })]);

        yield return Scenario(
            "uppercase-type-discriminators",
            "type matching is lowercase-invariant: Threshold/COMPOSITE spellings dispatch normally",
            [
                Rule(1, "composite", """
                    {"operator": "and", "conditions": [
                        {"type": "Threshold", "threshold": {"direction": "below", "value": 70}}
                    ]}
                    """),
            ],
            [Tick(T(0), Ctx(T(0), glucose: 65m))]);
    }
}
