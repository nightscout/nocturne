using System.Text.Json;
using FluentAssertions;
using Moq;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

#region ThresholdEvaluator

[Trait("Category", "Unit")]
public class ThresholdEvaluatorTests
{
    private readonly ThresholdEvaluator _sut = new();

    [Fact]
    public void ConditionType_ShouldBeThreshold()
    {
        _sut.ConditionType.Should().Be(AlertConditionType.Threshold);
    }

    [Fact]
    public async Task Below_TriggersWhenValueBelowThreshold()
    {
        var json = """{"direction": "below", "value": 70}""";
        var context = MakeContext(latestValue: 65);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Below_DoesNotTriggerWhenValueAboveThreshold()
    {
        var json = """{"direction": "below", "value": 70}""";
        var context = MakeContext(latestValue: 85);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Below_ExactBoundaryReturnsFalse()
    {
        var json = """{"direction": "below", "value": 70}""";
        var context = MakeContext(latestValue: 70);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Above_TriggersWhenValueAboveThreshold()
    {
        var json = """{"direction": "above", "value": 250}""";
        var context = MakeContext(latestValue: 260);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Above_DoesNotTriggerWhenValueBelowThreshold()
    {
        var json = """{"direction": "above", "value": 250}""";
        var context = MakeContext(latestValue: 200);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Above_ExactBoundaryReturnsFalse()
    {
        var json = """{"direction": "above", "value": 250}""";
        var context = MakeContext(latestValue: 250);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task NullLatestValue_ReturnsFalse()
    {
        var json = """{"direction": "below", "value": 70}""";
        var context = MakeContext(latestValue: null);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task UnknownDirection_ReturnsFalse()
    {
        var json = """{"direction": "sideways", "value": 70}""";
        var context = MakeContext(latestValue: 50);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    private static SensorContext MakeContext(decimal? latestValue = 100) => new()
    {
        LatestValue = latestValue,
        LatestTimestamp = DateTime.UtcNow,
        TrendRate = 0m,
        LastReadingAt = DateTime.UtcNow
    };
}

#endregion

#region RateOfChangeEvaluator

[Trait("Category", "Unit")]
public class RateOfChangeEvaluatorTests
{
    private readonly RateOfChangeEvaluator _sut = new();

    [Fact]
    public void ConditionType_ShouldBeRateOfChange()
    {
        _sut.ConditionType.Should().Be(AlertConditionType.RateOfChange);
    }

    [Fact]
    public async Task Falling_TriggersWhenRateAtNegativeThreshold()
    {
        // rate = -3.0, threshold = 3.0 => -3.0 <= -3.0 => true
        var json = """{"direction": "falling", "rate": 3.0}""";
        var context = MakeContext(trendRate: -3.0m);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Falling_TriggersWhenRateBelowNegativeThreshold()
    {
        var json = """{"direction": "falling", "rate": 3.0}""";
        var context = MakeContext(trendRate: -4.0m);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Falling_DoesNotTriggerWhenRateAboveNegativeThreshold()
    {
        var json = """{"direction": "falling", "rate": 3.0}""";
        var context = MakeContext(trendRate: -2.0m);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Rising_TriggersWhenRateAtThreshold()
    {
        var json = """{"direction": "rising", "rate": 3.0}""";
        var context = MakeContext(trendRate: 3.0m);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Rising_TriggersWhenRateAboveThreshold()
    {
        var json = """{"direction": "rising", "rate": 3.0}""";
        var context = MakeContext(trendRate: 5.0m);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Rising_DoesNotTriggerWhenRateBelowThreshold()
    {
        var json = """{"direction": "rising", "rate": 3.0}""";
        var context = MakeContext(trendRate: 2.0m);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task NullTrendRate_ReturnsFalse()
    {
        var json = """{"direction": "falling", "rate": 3.0}""";
        var context = MakeContext(trendRate: null);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task UnknownDirection_ReturnsFalse()
    {
        var json = """{"direction": "spinning", "rate": 3.0}""";
        var context = MakeContext(trendRate: 10m);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    private static SensorContext MakeContext(decimal? trendRate = 0m) => new()
    {
        LatestValue = 100m,
        LatestTimestamp = DateTime.UtcNow,
        TrendRate = trendRate,
        LastReadingAt = DateTime.UtcNow
    };
}

#endregion

#region CompositeEvaluator

[Trait("Category", "Unit")]
public class CompositeEvaluatorTests
{
    private static readonly JsonSerializerOptions SnakeCaseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    private readonly ConditionEvaluatorRegistry _registry;
    private readonly CompositeEvaluator _sut;

    public CompositeEvaluatorTests()
    {
        var evaluators = new IConditionEvaluator[]
        {
            new ThresholdEvaluator(),
            new RateOfChangeEvaluator()
        };

        _registry = new ConditionEvaluatorRegistry(evaluators);
        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider
            .Setup(sp => sp.GetService(typeof(ConditionEvaluatorRegistry)))
            .Returns(_registry);
        _sut = new CompositeEvaluator(serviceProvider.Object);
    }

    [Fact]
    public void ConditionType_ShouldBeComposite()
    {
        _sut.ConditionType.Should().Be(AlertConditionType.Composite);
    }

    [Fact]
    public async Task And_AllTrue_ReturnsTrue()
    {
        // Both conditions true: value < 70 AND falling rate <= -3
        var composite = new CompositeCondition("and", new List<ConditionNode>
        {
            new("threshold", Threshold: new ThresholdCondition("below", 70)),
            new("rate_of_change", RateOfChange: new RateOfChangeCondition("falling", 3.0m))
        });
        var json = JsonSerializer.Serialize(composite, SnakeCaseOptions);
        var context = new SensorContext
        {
            LatestValue = 60m,
            LatestTimestamp = DateTime.UtcNow,
            TrendRate = -4.0m,
            LastReadingAt = DateTime.UtcNow
        };

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task And_OneFalse_ReturnsFalse()
    {
        // First true (value < 70), second false (rate > -3)
        var composite = new CompositeCondition("and", new List<ConditionNode>
        {
            new("threshold", Threshold: new ThresholdCondition("below", 70)),
            new("rate_of_change", RateOfChange: new RateOfChangeCondition("falling", 3.0m))
        });
        var json = JsonSerializer.Serialize(composite, SnakeCaseOptions);
        var context = new SensorContext
        {
            LatestValue = 60m,
            LatestTimestamp = DateTime.UtcNow,
            TrendRate = -1.0m,
            LastReadingAt = DateTime.UtcNow
        };

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Or_AnyTrue_ReturnsTrue()
    {
        // First false (value > 70), second true (rate <= -3)
        var composite = new CompositeCondition("or", new List<ConditionNode>
        {
            new("threshold", Threshold: new ThresholdCondition("below", 70)),
            new("rate_of_change", RateOfChange: new RateOfChangeCondition("falling", 3.0m))
        });
        var json = JsonSerializer.Serialize(composite, SnakeCaseOptions);
        var context = new SensorContext
        {
            LatestValue = 100m,
            LatestTimestamp = DateTime.UtcNow,
            TrendRate = -4.0m,
            LastReadingAt = DateTime.UtcNow
        };

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Or_AllFalse_ReturnsFalse()
    {
        var composite = new CompositeCondition("or", new List<ConditionNode>
        {
            new("threshold", Threshold: new ThresholdCondition("below", 70)),
            new("rate_of_change", RateOfChange: new RateOfChangeCondition("falling", 3.0m))
        });
        var json = JsonSerializer.Serialize(composite, SnakeCaseOptions);
        var context = new SensorContext
        {
            LatestValue = 100m,
            LatestTimestamp = DateTime.UtcNow,
            TrendRate = -1.0m,
            LastReadingAt = DateTime.UtcNow
        };

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task EmptyConditionsList_ReturnsFalse()
    {
        var composite = new CompositeCondition("and", new List<ConditionNode>());
        var json = JsonSerializer.Serialize(composite, SnakeCaseOptions);
        var context = new SensorContext
        {
            LatestValue = 60m,
            LatestTimestamp = DateTime.UtcNow,
            TrendRate = -4.0m,
            LastReadingAt = DateTime.UtcNow
        };

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task NestedComposite_Works()
    {
        // Register the composite evaluator itself in the registry
        var evaluators = new List<IConditionEvaluator>
        {
            new ThresholdEvaluator(),
            new RateOfChangeEvaluator()
        };
        var registry = new ConditionEvaluatorRegistry(evaluators);
        var sp = new Mock<IServiceProvider>();
        sp.Setup(s => s.GetService(typeof(ConditionEvaluatorRegistry))).Returns(registry);
        var compositeEval = new CompositeEvaluator(sp.Object);
        // Re-create registry with composite included
        evaluators.Add(compositeEval);
        var fullRegistry = new ConditionEvaluatorRegistry(evaluators);
        var spFull = new Mock<IServiceProvider>();
        spFull.Setup(s => s.GetService(typeof(ConditionEvaluatorRegistry))).Returns(fullRegistry);
        var sut = new CompositeEvaluator(spFull.Object);

        // Outer OR: (inner AND fails) OR (threshold succeeds)
        var inner = new CompositeCondition("and", new List<ConditionNode>
        {
            new("threshold", Threshold: new ThresholdCondition("below", 70)),
            new("rate_of_change", RateOfChange: new RateOfChangeCondition("falling", 3.0m))
        });

        var outer = new CompositeCondition("or", new List<ConditionNode>
        {
            new("composite", Composite: inner),
            new("threshold", Threshold: new ThresholdCondition("above", 250))
        });

        var json = JsonSerializer.Serialize(outer, SnakeCaseOptions);
        var context = new SensorContext
        {
            LatestValue = 300m, // above 250 is true, below 70 is false
            LatestTimestamp = DateTime.UtcNow,
            TrendRate = -1.0m, // not falling fast enough for inner AND
            LastReadingAt = DateTime.UtcNow
        };

        (await sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task UnknownOperator_ReturnsFalse()
    {
        var composite = new CompositeCondition("xor", new List<ConditionNode>
        {
            new("threshold", Threshold: new ThresholdCondition("below", 70))
        });
        var json = JsonSerializer.Serialize(composite, SnakeCaseOptions);
        var context = new SensorContext
        {
            LatestValue = 60m,
            LatestTimestamp = DateTime.UtcNow,
            TrendRate = 0m,
            LastReadingAt = DateTime.UtcNow
        };

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }
}

#endregion

#region ConditionEvaluatorRegistry

[Trait("Category", "Unit")]
public class ConditionEvaluatorRegistryTests
{
    [Fact]
    public void GetEvaluator_ReturnsCorrectEvaluatorByType()
    {
        var threshold = new ThresholdEvaluator();
        var roc = new RateOfChangeEvaluator();
        var registry = new ConditionEvaluatorRegistry(new IConditionEvaluator[] { threshold, roc });

        registry.GetEvaluator(AlertConditionType.Threshold).Should().BeSameAs(threshold);
        registry.GetEvaluator(AlertConditionType.RateOfChange).Should().BeSameAs(roc);
    }

    [Fact]
    public void GetEvaluator_ReturnsNullForUnknownType()
    {
        var registry = new ConditionEvaluatorRegistry(new IConditionEvaluator[] { new ThresholdEvaluator() });

        registry.GetEvaluator((AlertConditionType)999).Should().BeNull();
    }
}

#endregion
