using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;
using Nocturne.Alerts.ParityCorpus.Generator.Harness;
using Nocturne.Core.Alerts.Native;
using Xunit;

namespace Nocturne.Alerts.Native.Tests;

/// <summary>
/// FFI marshalling stress: very large condition trees, non-ASCII payloads
/// (emoji, CJK), null/empty fields, and the error-envelope-to-typed-exception
/// path.
/// </summary>
public class MarshallingStressTests
{
    private static readonly Guid RuleId = Guid.Parse("00000000-0000-0000-0000-0000000000aa");

    private static JsonElement Element(JsonNode node) =>
        JsonSerializer.SerializeToElement(node, AlertEnvelopeJson.Options);

    private static JsonElement EmptyContext() => Element(new JsonObject());

    [NativeFact]
    public void Version_is_a_semver_string()
    {
        RustAlertEngine.Version().Should().MatchRegex(new Regex(@"^\d+\.\d+\.\d+$"));
    }

    [NativeFact]
    public void Huge_condition_tree_round_trips()
    {
        const int leafCount = 400;
        var conditions = new JsonArray();
        for (var i = 0; i < leafCount; i++)
        {
            conditions.Add(new JsonObject
            {
                ["type"] = "threshold",
                ["threshold"] = new JsonObject { ["direction"] = "below", ["value"] = i },
            });
        }
        var conditionParams = new JsonObject { ["operator"] = "or", ["conditions"] = conditions };

        var rule = new RustAlertRule
        {
            Id = RuleId,
            ConditionType = "composite",
            ConditionParams = Element(conditionParams),
        };
        var context = Element(new JsonObject
        {
            ["latest_value"] = 100,
            ["latest_timestamp"] = "2026-01-05T12:00:00Z",
        });

        var response = RustAlertEngine.Evaluate(rule, context, new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc));

        var result = JsonNode.Parse(response.Result!.Value.GetRawText())!;
        // threshold below is strict (latest < value): leaves 0..100 are false, 101..399 true.
        result["root"]!.GetValue<bool>().Should().BeTrue();
        var leaves = result["leaves"]!.AsArray();
        leaves.Should().HaveCount(leafCount);
        leaves[50]!["value"]!.GetValue<bool>().Should().BeFalse();
        leaves[250]!["value"]!.GetValue<bool>().Should().BeTrue();

        // leaf_paths over the same tree: a full ConditionNode wrapping the payload.
        var node = new JsonObject { ["type"] = "composite", ["composite"] = conditionParams.DeepClone() };
        var paths = RustAlertEngine.LeafPaths(Element(node));
        paths.Root.Should().Be("composite");
        paths.Leaves.Should().HaveCount(leafCount);
        paths.Paths.Should().HaveCount(leafCount + 1); // every leaf slot + the composite root
        paths.Leaves![0].Path.Should().Be("composite[0].threshold");
        paths.Leaves[leafCount - 1].Path.Should().Be($"composite[{leafCount - 1}].threshold");
    }

    [NativeFact]
    public void Non_ascii_strings_round_trip_through_the_boundary()
    {
        // The state-span state string must match exactly between rule payload
        // and context, so emoji/CJK surviving UTF-8 marshalling is observable
        // through the evaluation result, not just echoed bytes.
        const string spanState = "运动🏃‍♀️ — ナイトスカウト ✨";
        var rule = new RustAlertRule
        {
            Id = RuleId,
            ConditionType = "state_span_active",
            ConditionParams = Element(new JsonObject
            {
                ["category"] = "Exercise",
                ["state"] = spanState,
                ["is_active"] = true,
            }),
        };
        var context = Element(new JsonObject
        {
            ["active_state_spans"] = new JsonArray
            {
                new JsonObject
                {
                    ["category"] = "Exercise",
                    ["state"] = spanState,
                    ["started_at"] = "2026-01-05T11:00:00Z",
                },
            },
        });

        var response = RustAlertEngine.Evaluate(rule, context, new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc));

        var result = JsonNode.Parse(response.Result!.Value.GetRawText())!;
        result["root"]!.GetValue<bool>().Should().BeTrue("the non-ASCII state string must survive the round trip intact");

        // And a near-miss state string must NOT match (catches mangled encodings
        // that would also mangle the probe string).
        var mismatchContext = Element(new JsonObject
        {
            ["active_state_spans"] = new JsonArray
            {
                new JsonObject
                {
                    ["category"] = "Exercise",
                    ["state"] = spanState + "!",
                    ["started_at"] = "2026-01-05T11:00:00Z",
                },
            },
        });
        var mismatch = RustAlertEngine.Evaluate(rule, mismatchContext, new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc));
        JsonNode.Parse(mismatch.Result!.Value.GetRawText())!["root"]!.GetValue<bool>().Should().BeFalse();
    }

    [NativeFact]
    public void Null_condition_params_evaluates_false()
    {
        var rule = new RustAlertRule
        {
            Id = RuleId,
            ConditionType = "threshold",
            ConditionParams = JsonSerializer.SerializeToElement<object?>(null),
        };

        var response = RustAlertEngine.Evaluate(rule, EmptyContext(), new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc));

        var result = JsonNode.Parse(response.Result!.Value.GetRawText())!;
        result["root"]!.GetValue<bool>().Should().BeFalse("a null condition record evaluates false");
        result["leaves"]!.AsArray().Should().HaveCount(1);
    }

    [NativeFact]
    public void Empty_condition_params_evaluates_with_defaults()
    {
        var rule = new RustAlertRule
        {
            Id = RuleId,
            ConditionType = "threshold",
            ConditionParams = Element(new JsonObject()),
        };

        var response = RustAlertEngine.Evaluate(rule, EmptyContext(), new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc));

        JsonNode.Parse(response.Result!.Value.GetRawText())!["root"]!.GetValue<bool>().Should().BeFalse();
    }

    [NativeFact]
    public void Unknown_condition_type_throws_typed_exception()
    {
        var rule = new RustAlertRule
        {
            Id = RuleId,
            ConditionType = "definitely_not_a_condition",
            ConditionParams = Element(new JsonObject()),
        };

        var act = () => RustAlertEngine.Evaluate(rule, EmptyContext(), new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc));

        act.Should().Throw<RustAlertEngineException>()
            .WithMessage("*unknown condition_type 'definitely_not_a_condition'*");
    }

    [NativeFact]
    public void Unsupported_schema_version_throws_typed_exception()
    {
        var request = new RustEvaluateRequest
        {
            SchemaVersion = 42,
            Rule = new RustAlertRule
            {
                Id = RuleId,
                ConditionType = "threshold",
                ConditionParams = Element(new JsonObject()),
            },
            Context = EmptyContext(),
            Now = new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc),
        };

        var act = () => RustAlertEngine.Evaluate(request);

        act.Should().Throw<RustAlertEngineException>().WithMessage("*unsupported schema_version 42*");
    }

    [NativeFact]
    public void Malformed_envelope_returns_error_envelope_at_the_raw_layer()
    {
        var raw = AlertsInterop.Evaluate("{ this is not json");

        var envelope = JsonNode.Parse(raw)!;
        envelope["ok"]!.GetValue<bool>().Should().BeFalse();
        envelope["error"]!.GetValue<string>().Should().Contain("invalid request envelope");
        envelope["schema_version"]!.GetValue<int>().Should().Be(1);
    }

    [NativeFact]
    public void Malformed_leaf_paths_node_throws_typed_exception()
    {
        // `type` must be a string — a number is a JsonException in the C# engine.
        var node = JsonSerializer.SerializeToElement(new { type = 5 });

        var act = () => RustAlertEngine.LeafPaths(node);

        act.Should().Throw<RustAlertEngineException>().WithMessage("*malformed condition node*");
    }

    [NativeFact]
    public void Leaf_paths_honours_root_override()
    {
        var node = Element(new JsonObject
        {
            ["type"] = "sustained",
            ["sustained"] = new JsonObject
            {
                ["minutes"] = 5,
                ["child"] = new JsonObject
                {
                    ["type"] = "threshold",
                    ["threshold"] = new JsonObject { ["direction"] = "above", ["value"] = 180 },
                },
            },
        });

        var response = RustAlertEngine.LeafPaths(node, root: "auto_resolve");

        response.Root.Should().Be("auto_resolve");
        response.Paths.Should().Equal("auto_resolve", "auto_resolve[0].threshold");
        response.Leaves.Should().ContainSingle()
            .Which.Should().Be(new RustLeafPath(0, "auto_resolve[0].threshold"));
    }

    [NativeFact]
    public void Sustained_state_threads_through_typed_wrapper()
    {
        var rule = new RustAlertRule
        {
            Id = RuleId,
            ConditionType = "sustained",
            ConditionParams = Element(new JsonObject
            {
                ["minutes"] = 10,
                ["child"] = new JsonObject
                {
                    ["type"] = "threshold",
                    ["threshold"] = new JsonObject { ["direction"] = "below", ["value"] = 70 },
                },
            }),
        };
        var context = Element(new JsonObject
        {
            ["latest_value"] = 60,
            ["latest_timestamp"] = "2026-01-05T12:00:00Z",
        });

        var first = RustAlertEngine.Evaluate(rule, context, new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc));
        first.Timers.Should().ContainKey("sustained")
            .WhoseValue.Should().Be(new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc));
        first.Tracker!.State.Should().Be("idle");
        first.Tracker.NextExcursionOrdinal.Should().Be(1);

        var second = RustAlertEngine.Evaluate(
            rule, context, new DateTime(2026, 1, 5, 12, 10, 0, DateTimeKind.Utc), first.Timers, first.Tracker);

        JsonNode.Parse(second.Result!.Value.GetRawText())!["root"]!.GetValue<bool>().Should().BeTrue();
        second.Tracker!.State.Should().Be("active");
        second.Tracker.ActiveExcursionOrdinal.Should().Be(1);
        second.Tracker.NextExcursionOrdinal.Should().Be(2);
        second.Tracker.UpdatedAt.Should().Be(new DateTime(2026, 1, 5, 12, 10, 0, DateTimeKind.Utc));
    }
}
