using System.Text.Json;
using FluentAssertions;
using Moq;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Evaluators;

[Trait("Category", "Unit")]
public class NotEvaluatorTests
{
    private static readonly JsonSerializerOptions SnakeCaseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    private readonly NotEvaluator _sut;

    public NotEvaluatorTests()
    {
        // Build a registry containing Threshold + RateOfChange + Composite + Not.
        // Composite needs the registry too, so we wire it through the same service provider.
        var serviceProvider = new Mock<IServiceProvider>();

        var compositeEval = new CompositeEvaluator(serviceProvider.Object);
        var notEval = new NotEvaluator(serviceProvider.Object);

        var evaluators = new IConditionEvaluator[]
        {
            new ThresholdEvaluator(),
            new RateOfChangeEvaluator(),
            compositeEval,
            notEval
        };
        var registry = new ConditionEvaluatorRegistry(evaluators);

        serviceProvider
            .Setup(sp => sp.GetService(typeof(ConditionEvaluatorRegistry)))
            .Returns(registry);

        _sut = notEval;
    }

    [Fact]
    public void ConditionType_ShouldBeNot()
    {
        _sut.ConditionType.Should().Be(AlertConditionType.Not);
    }

    [Fact]
    public async Task Evaluate_NotOfLeafThreshold_NegatesResult()
    {
        // Threshold "below 70" against value 60 = true; Not = false.
        var notTrue = new NotCondition(
            new ConditionNode("threshold", Threshold: new ThresholdCondition("below", 70)));
        var json = JsonSerializer.Serialize(notTrue, SnakeCaseOptions);
        var contextTrue = MakeContext(latestValue: 60m);

        (await _sut.EvaluateAsync(json, contextTrue, CancellationToken.None)).Should().BeFalse();

        // Threshold against value 100 = false; Not = true.
        var contextFalse = MakeContext(latestValue: 100m);
        (await _sut.EvaluateAsync(json, contextFalse, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Evaluate_NotOfCompositeAndOfTwoTrues_ReturnsFalse()
    {
        var inner = new CompositeCondition("and", new List<ConditionNode>
        {
            new("threshold", Threshold: new ThresholdCondition("below", 70)),
            new("rate_of_change", RateOfChange: new RateOfChangeCondition("falling", 3.0m))
        });
        var notNode = new NotCondition(new ConditionNode("composite", Composite: inner));
        var json = JsonSerializer.Serialize(notNode, SnakeCaseOptions);

        // value 60 < 70 (true) AND rate -4 <= -3 (true) => composite true => Not false
        var context = MakeContext(latestValue: 60m, trendRate: -4.0m);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Evaluate_NotOfCompositeOrOfTwoFalses_ReturnsTrue()
    {
        var inner = new CompositeCondition("or", new List<ConditionNode>
        {
            new("threshold", Threshold: new ThresholdCondition("below", 70)),
            new("rate_of_change", RateOfChange: new RateOfChangeCondition("falling", 3.0m))
        });
        var notNode = new NotCondition(new ConditionNode("composite", Composite: inner));
        var json = JsonSerializer.Serialize(notNode, SnakeCaseOptions);

        // value 100 not below 70, rate -1 not falling fast => composite false => Not true
        var context = MakeContext(latestValue: 100m, trendRate: -1.0m);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Evaluate_WithMissingChild_ReturnsFalse()
    {
        // NotCondition serialised with a null Child — defensive guard.
        var json = """{"child": null}""";
        var context = MakeContext();

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Evaluate_WithUnregisteredChildType_ReturnsTrue()
    {
        // Child type "iob" is not registered in this registry.
        // CompositeEvaluator-style fail-mode: missing evaluator => child evaluates false
        // => Not negates to true.
        var notNode = new NotCondition(new ConditionNode("iob"));
        var json = JsonSerializer.Serialize(notNode, SnakeCaseOptions);
        var context = MakeContext();

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeTrue();
    }

    private static SensorContext MakeContext(decimal? latestValue = 100m, decimal? trendRate = 0m) => new()
    {
        LatestValue = latestValue,
        LatestTimestamp = DateTime.UtcNow,
        TrendRate = trendRate,
        LastReadingAt = DateTime.UtcNow
    };
}
