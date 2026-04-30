using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Evaluators;

[Trait("Category", "Unit")]
public class StalenessEvaluatorTests
{
    private static readonly DateTime FixedNow = new(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc);
    private readonly StalenessEvaluator _sut;

    public StalenessEvaluatorTests()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(FixedNow));
        _sut = new StalenessEvaluator(timeProvider);
    }

    [Fact]
    public void ConditionType_ShouldBeStaleness()
    {
        _sut.ConditionType.Should().Be(AlertConditionType.Staleness);
    }

    // ----- No-reading semantics: elapsed = "infinity" -----

    [Theory]
    [InlineData(">", true)]
    [InlineData(">=", true)]
    [InlineData("<", false)]
    [InlineData("<=", false)]
    [InlineData("==", false)]
    public void NoReading_OperatorSemantics(string op, bool expected)
    {
        var json = $$"""{"operator": "{{op}}", "value": 15}""";
        var context = MakeContext(lastReadingAt: null);

        _sut.Evaluate(json, context).Should().Be(expected);
    }

    // ----- Finite-elapsed comparisons -----

    [Fact]
    public void GreaterThan_TriggersWhenElapsedExceedsValue()
    {
        var json = """{"operator": ">", "value": 15}""";
        var context = MakeContext(lastReadingAt: FixedNow.AddMinutes(-20));

        _sut.Evaluate(json, context).Should().BeTrue();
    }

    [Fact]
    public void GreaterThan_DoesNotTriggerAtBoundary()
    {
        var json = """{"operator": ">", "value": 15}""";
        var context = MakeContext(lastReadingAt: FixedNow.AddMinutes(-15));

        _sut.Evaluate(json, context).Should().BeFalse();
    }

    [Fact]
    public void GreaterThan_DoesNotTriggerForFreshReading()
    {
        var json = """{"operator": ">", "value": 15}""";
        var context = MakeContext(lastReadingAt: FixedNow.AddMinutes(-2));

        _sut.Evaluate(json, context).Should().BeFalse();
    }

    [Fact]
    public void GreaterThanOrEqual_TriggersAtBoundary()
    {
        var json = """{"operator": ">=", "value": 15}""";
        var context = MakeContext(lastReadingAt: FixedNow.AddMinutes(-15));

        _sut.Evaluate(json, context).Should().BeTrue();
    }

    [Fact]
    public void LessThan_TriggersWhenWithinWindow()
    {
        var json = """{"operator": "<", "value": 15}""";
        var context = MakeContext(lastReadingAt: FixedNow.AddMinutes(-10));

        _sut.Evaluate(json, context).Should().BeTrue();
    }

    [Fact]
    public void LessThan_DoesNotTriggerAtBoundary()
    {
        var json = """{"operator": "<", "value": 15}""";
        var context = MakeContext(lastReadingAt: FixedNow.AddMinutes(-15));

        _sut.Evaluate(json, context).Should().BeFalse();
    }

    [Fact]
    public void LessThanOrEqual_TriggersAtBoundary()
    {
        var json = """{"operator": "<=", "value": 15}""";
        var context = MakeContext(lastReadingAt: FixedNow.AddMinutes(-15));

        _sut.Evaluate(json, context).Should().BeTrue();
    }

    [Fact]
    public void Equal_TriggersAtExactElapsed()
    {
        var json = """{"operator": "==", "value": 10}""";
        var context = MakeContext(lastReadingAt: FixedNow.AddMinutes(-10));

        _sut.Evaluate(json, context).Should().BeTrue();
    }

    [Fact]
    public void Equal_DoesNotTriggerWhenElapsedDiffers()
    {
        var json = """{"operator": "==", "value": 10}""";
        var context = MakeContext(lastReadingAt: FixedNow.AddMinutes(-12));

        _sut.Evaluate(json, context).Should().BeFalse();
    }

    [Fact]
    public void UnknownOperator_ReturnsFalse()
    {
        var json = """{"operator": "~", "value": 15}""";
        var context = MakeContext(lastReadingAt: FixedNow.AddMinutes(-20));

        _sut.Evaluate(json, context).Should().BeFalse();
    }

    private static SensorContext MakeContext(DateTime? lastReadingAt) => new()
    {
        LatestValue = 100m,
        LatestTimestamp = FixedNow,
        TrendRate = 0m,
        LastReadingAt = lastReadingAt
    };
}
