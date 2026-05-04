using FluentAssertions;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Evaluators;

/// <summary>
/// ForceEvalRunner must evaluate every leaf in the tree exactly once, regardless of any
/// short-circuit logic the live composite evaluator would apply. The replay UI relies on
/// per-leaf truth being recorded at every tick.
/// </summary>
[Trait("Category", "Unit")]
public class ForceEvalRunnerTests
{
    private static SensorContext MakeContext() => new()
    {
        LatestValue = 100m,
        LatestTimestamp = DateTime.UtcNow,
        TrendRate = 0m,
        LastReadingAt = DateTime.UtcNow,
        Predictions = Array.Empty<PredictedGlucosePoint>(),
        ActiveAlerts = new Dictionary<Guid, ActiveAlertSnapshot>(),
        CurrentPath = string.Empty,
    };

    /// <summary>
    /// Per-leaf counter evaluator. Returns whatever bool was queued under
    /// <see cref="ConditionType"/>; increments a counter every call so tests can
    /// assert each leaf was touched exactly once (no short-circuit skip).
    /// </summary>
    private sealed class CountingThresholdEvaluator : IConditionEvaluator
    {
        public AlertConditionType ConditionType => AlertConditionType.Threshold;
        public int CallCount;
        private readonly Func<string, bool> _decide;

        public CountingThresholdEvaluator(Func<string, bool> decide) { _decide = decide; }

        public Task<bool> EvaluateAsync(string conditionParamsJson, SensorContext context, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(_decide(conditionParamsJson));
        }
    }

    private static ConditionNode Threshold(string tag) =>
        new("threshold", Threshold: new ThresholdCondition(tag, 0m));

    [Fact]
    public async Task EvaluatesEveryLeaf_InAndTree_EvenWhenFirstIsFalse()
    {
        // Build AND(a, b, c) where the live composite evaluator would short-circuit
        // after a=false. Force-eval must still touch b and c.
        var a = Threshold("a");
        var b = Threshold("b");
        var c = Threshold("c");
        var root = new ConditionNode("composite",
            Composite: new CompositeCondition("and", [a, b, c]));

        var stub = new CountingThresholdEvaluator(json => json.Contains("\"direction\":\"a\"") ? false : true);
        var registry = new ConditionEvaluatorRegistry(new IConditionEvaluator[] { stub });
        var runner = new ForceEvalRunner();

        var values = await runner.EvaluateAllLeavesAsync(root, MakeContext(), registry, CancellationToken.None);

        stub.CallCount.Should().Be(3);
        values.Should().HaveCount(3);
        values[0].Should().BeFalse();
        values[1].Should().BeTrue();
        values[2].Should().BeTrue();
    }

    [Fact]
    public async Task EvaluatesEveryLeaf_InOrTree_EvenWhenFirstIsTrue()
    {
        var a = Threshold("a");
        var b = Threshold("b");
        var c = Threshold("c");
        var root = new ConditionNode("composite",
            Composite: new CompositeCondition("or", [a, b, c]));

        var stub = new CountingThresholdEvaluator(json => json.Contains("\"direction\":\"a\"") ? true : false);
        var registry = new ConditionEvaluatorRegistry(new IConditionEvaluator[] { stub });
        var runner = new ForceEvalRunner();

        var values = await runner.EvaluateAllLeavesAsync(root, MakeContext(), registry, CancellationToken.None);

        stub.CallCount.Should().Be(3);
        values[0].Should().BeTrue();
        values[1].Should().BeFalse();
        values[2].Should().BeFalse();
    }

    [Fact]
    public async Task SingleLeaf_GetsEvaluatedOnce()
    {
        var leaf = Threshold("a");
        var stub = new CountingThresholdEvaluator(_ => true);
        var registry = new ConditionEvaluatorRegistry(new IConditionEvaluator[] { stub });
        var runner = new ForceEvalRunner();

        var values = await runner.EvaluateAllLeavesAsync(leaf, MakeContext(), registry, CancellationToken.None);

        stub.CallCount.Should().Be(1);
        values.Should().HaveCount(1);
        values[0].Should().BeTrue();
    }

    [Fact]
    public async Task NestedComposite_EvaluatesAllLeavesInPreOrder()
    {
        // AND(a, OR(b, c)) → leaves [a:0, b:1, c:2], all evaluated.
        var a = Threshold("a");
        var b = Threshold("b");
        var c = Threshold("c");
        var inner = new ConditionNode("composite",
            Composite: new CompositeCondition("or", [b, c]));
        var root = new ConditionNode("composite",
            Composite: new CompositeCondition("and", [a, inner]));

        var stub = new CountingThresholdEvaluator(json => json.Contains("\"direction\":\"b\""));
        var registry = new ConditionEvaluatorRegistry(new IConditionEvaluator[] { stub });
        var runner = new ForceEvalRunner();

        var values = await runner.EvaluateAllLeavesAsync(root, MakeContext(), registry, CancellationToken.None);

        stub.CallCount.Should().Be(3);
        values[0].Should().BeFalse();
        values[1].Should().BeTrue();
        values[2].Should().BeFalse();
    }
}
