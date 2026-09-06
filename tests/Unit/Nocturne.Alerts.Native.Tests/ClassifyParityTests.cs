using System.Text.Json;
using FluentAssertions;
using Nocturne.Alerts.ParityCorpus.Generator.Harness;
using Nocturne.Core.Alerts.Native;
using Xunit;

namespace Nocturne.Alerts.Native.Tests;

/// <summary>
/// Parity for scope-class classification (scoped Do Not Disturb, ADR 0004): the
/// C# FFI path (<see cref="RustAlertEngine.Classify"/> → envelope → native
/// <c>nocturne_alerts_classify</c>) must reproduce the crate's
/// <c>classify</c> matrix byte-for-byte. The cases mirror the Rust unit tests in
/// <c>crates/nocturne-alerts-core/src/classify.rs</c> so the directional-leaf
/// contract can never drift between the host wire shape and the crate.
/// </summary>
public class ClassifyParityTests
{
    public static TheoryData<string, string, string> Cases() => new()
    {
        // condition_type, condition_params (payload-only), expected scope_class
        { "threshold", """{ "direction": "below", "value": 70 }""", "low" },
        { "threshold", """{ "direction": "above", "value": 250 }""", "high" },
        { "rate_of_change", """{ "direction": "falling", "rate": 2 }""", "low" },
        { "rate_of_change", """{ "direction": "rising", "rate": 2 }""", "high" },
        { "predicted", """{ "operator": "<", "value": 70, "within_minutes": 15 }""", "low" },
        { "predicted", """{ "operator": ">=", "value": 250, "within_minutes": 15 }""", "high" },
        { "predicted", """{ "operator": "==", "value": 100, "within_minutes": 15 }""", "undirected" },
        { "glucose_bucket", """{ "buckets": [0, 1] }""", "low" },
        { "glucose_bucket", """{ "buckets": [4, 5] }""", "high" },
        { "glucose_bucket", """{ "buckets": [0, 5] }""", "composite" },
        { "glucose_bucket", """{ "buckets": [2, 3] }""", "undirected" },
        { "signal_loss", """{ "timeout_minutes": 20 }""", "undirected" },
        // Composite of full child nodes — the stored shape.
        {
            "composite",
            """
            { "operator": "and", "conditions": [
                { "type": "threshold", "threshold": { "direction": "below", "value": 70 } },
                { "type": "iob", "iob": { "operator": "<", "value": 1 } },
                { "type": "rate_of_change", "rate_of_change": { "direction": "falling", "rate": 1 } }
            ] }
            """,
            "low"
        },
        {
            "composite",
            """
            { "operator": "or", "conditions": [
                { "type": "threshold", "threshold": { "direction": "below", "value": 70 } },
                { "type": "threshold", "threshold": { "direction": "above", "value": 250 } }
            ] }
            """,
            "composite"
        },
        // A directional leaf under NOT taints the whole rule to undirected.
        { "not", """{ "child": { "type": "threshold", "threshold": { "direction": "above", "value": 250 } } }""", "undirected" },
        // Sustained passes its child's direction through.
        { "sustained", """{ "child": { "type": "threshold", "threshold": { "direction": "below", "value": 70 } } }""", "low" },
        // Unknown type / malformed input silent-fail to the all-only default.
        { "teleport", "{}", "undirected" },
    };

    [NativeTheory]
    [MemberData(nameof(Cases))]
    public void Csharp_ffi_classify_matches_the_crate_matrix(
        string conditionType, string conditionParamsJson, string expectedScopeClass)
    {
        using var doc = JsonDocument.Parse(conditionParamsJson);

        var request = new RustClassifyRequest
        {
            ConditionType = conditionType,
            ConditionParams = doc.RootElement.Clone(),
        };

        var scopeClass = RustAlertEngine.Classify(request);

        scopeClass.Should().Be(
            expectedScopeClass,
            "the C# FFI classify path must agree with the crate's classify for {0}",
            conditionType);
    }

    /// <summary>An absent <c>condition_params</c> is the payload-less default: undirected.</summary>
    [NativeFact]
    public void Csharp_ffi_classify_defaults_missing_params_to_undirected()
    {
        using var doc = JsonDocument.Parse("{}");
        var request = new RustClassifyRequest
        {
            ConditionType = "threshold",
            ConditionParams = doc.RootElement.Clone(),
        };

        RustAlertEngine.Classify(request).Should().Be("undirected");
    }
}
