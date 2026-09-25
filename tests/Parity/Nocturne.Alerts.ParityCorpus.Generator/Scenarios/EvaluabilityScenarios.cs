using Nocturne.Alerts.ParityCorpus.Generator.Harness;
using static Nocturne.Alerts.ParityCorpus.Generator.Scenarios.B;

namespace Nocturne.Alerts.ParityCorpus.Generator.Scenarios;

/// <summary>
/// Tree shapes whose evaluation throws (docs/alerts/engine-semantics.md §1.4). The rule is
/// skipped on every tick, with no root, leaves, tracker or timer ops, whether or not evaluation
/// would reach the fault. The rule beside it evaluates normally.
/// </summary>
public static class EvaluabilityScenarios
{
    private const string Low = """{"type": "threshold", "threshold": {"direction": "below", "value": 70}}""";

    public static IEnumerable<ScenarioFile> All()
    {
        yield return Scenario(
            "unevaluable-composite-without-conditions",
            "a composite with no conditions list, at the root or as a child with no payload, skips the rule",
            [
                Rule(1, "composite", """{"operator": "and"}"""),
                Rule(2, "composite", """{"operator": "and", "conditions": [{"type": "sustained"}, {"type": "composite"}]}"""),
                Rule(3, "threshold", """{"direction": "below", "value": 70}"""),
            ],
            [Tick(T(0), Ctx(T(0), glucose: 65m))]);

        yield return Scenario(
            "unevaluable-composite-without-operator",
            "a composite with conditions but no operator skips the rule; with neither it evaluates false",
            [
                Rule(1, "composite", $$"""{"conditions": [{{Low}}]}"""),
                Rule(2, "composite", """{"conditions": []}"""),
            ],
            [Tick(T(0), Ctx(T(0), glucose: 65m))]);

        yield return Scenario(
            "unevaluable-null-condition-slot",
            "a null composite slot skips the rule even when an earlier or-child is true",
            [Rule(1, "composite", $$"""{"operator": "or", "conditions": [{{Low}}, null]}""")],
            [
                Tick(T(0), Ctx(T(0), glucose: 65m)),
                Tick(T(5), Ctx(T(5), glucose: 100m)),
            ]);

        yield return Scenario(
            "unevaluable-child-without-type",
            "a composite or not child with no type skips the rule",
            [
                Rule(1, "composite", """{"operator": "and", "conditions": [{"threshold": {"direction": "below", "value": 70}}]}"""),
                Rule(2, "not", """{"child": {"type": null}}"""),
            ],
            [Tick(T(0), Ctx(T(0), glucose: 65m))]);

        yield return Scenario(
            "unevaluable-missing-direction",
            "threshold and rate_of_change without a direction skip the rule, with or without a reading",
            [
                Rule(1, "threshold", """{"value": 70}"""),
                Rule(2, "composite", """{"operator": "and", "conditions": [{"type": "rate_of_change", "rate_of_change": {"rate": 2}}]}"""),
                Rule(3, "not", """{"child": {"type": "threshold"}}"""),
            ],
            [
                Tick(T(0), Ctx(T(0), glucose: 65m, trendRate: -3m)),
                Tick(T(5), Ctx(T(5), glucose: null, trendRate: null)),
            ]);

        yield return Scenario(
            "unevaluable-member-name-kind",
            "a multi-word kind spelled by its member name drops its payload, so a RateOfChange child has no direction and skips the rule",
            [
                Rule(1, "composite", """
                    {"operator": "and", "conditions": [
                        {"type": "RateOfChange", "rate_of_change": {"direction": "falling", "rate": 2}}
                    ]}
                    """),
            ],
            [Tick(T(0), Ctx(T(0), glucose: 65m, trendRate: -3m))]);

        var parent = RId(900);
        yield return Scenario(
            "unevaluable-alert-state-without-state",
            "alert_state without a state skips the rule whether or not the referenced alert is active",
            [Rule(1, "alert_state", $$"""{"alert_id": "{{parent}}"}""")],
            [
                Tick(T(0), Ctx(T(0)) with { ActiveAlerts = [new(parent, "firing", T(-10), null)] }),
                Tick(T(5), Ctx(T(5))),
            ]);

        yield return Scenario(
            "unevaluable-behind-zero-minute-sustained",
            "a fault under a sustained whose minutes are not positive still skips the rule",
            [Rule(1, "sustained", """{"minutes": 0, "child": {"type": "threshold"}}""")],
            [Tick(T(0), Ctx(T(0), glucose: 65m))]);

        yield return Scenario(
            "unevaluable-auto-resolve-tree",
            "an auto-resolve tree holding a fault never resolves, even when an earlier or-child is true",
            [
                Rule(1, "threshold", """{"direction": "below", "value": 70}""",
                    autoResolveParams: """
                        {"type": "composite", "composite": {"operator": "or", "conditions": [
                            {"type": "threshold", "threshold": {"direction": "above", "value": 60}},
                            null
                        ]}}
                        """),
            ],
            [
                Tick(T(0), Ctx(T(0), glucose: 65m)),
                Tick(T(5), Ctx(T(5), glucose: 66m)),
            ]);
    }
}
