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
    public async Task NoReading_OperatorSemantics(string op, bool expected)
    {
        var json = $$"""{"operator": "{{op}}", "value": 15}""";
        var context = MakeContext(lastReadingAt: null);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().Be(expected);
    }

    // ----- Finite-elapsed comparisons -----

    [Fact]
    public async Task GreaterThan_TriggersWhenElapsedExceedsValue()
    {
        var json = """{"operator": ">", "value": 15}""";
        var context = MakeContext(lastReadingAt: FixedNow.AddMinutes(-20));

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task GreaterThan_DoesNotTriggerAtBoundary()
    {
        var json = """{"operator": ">", "value": 15}""";
        var context = MakeContext(lastReadingAt: FixedNow.AddMinutes(-15));

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task GreaterThan_DoesNotTriggerForFreshReading()
    {
        var json = """{"operator": ">", "value": 15}""";
        var context = MakeContext(lastReadingAt: FixedNow.AddMinutes(-2));

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task GreaterThanOrEqual_TriggersAtBoundary()
    {
        var json = """{"operator": ">=", "value": 15}""";
        var context = MakeContext(lastReadingAt: FixedNow.AddMinutes(-15));

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task LessThan_TriggersWhenWithinWindow()
    {
        var json = """{"operator": "<", "value": 15}""";
        var context = MakeContext(lastReadingAt: FixedNow.AddMinutes(-10));

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task LessThan_DoesNotTriggerAtBoundary()
    {
        var json = """{"operator": "<", "value": 15}""";
        var context = MakeContext(lastReadingAt: FixedNow.AddMinutes(-15));

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task LessThanOrEqual_TriggersAtBoundary()
    {
        var json = """{"operator": "<=", "value": 15}""";
        var context = MakeContext(lastReadingAt: FixedNow.AddMinutes(-15));

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Equal_TriggersAtExactElapsed()
    {
        var json = """{"operator": "==", "value": 10}""";
        var context = MakeContext(lastReadingAt: FixedNow.AddMinutes(-10));

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Equal_DoesNotTriggerWhenElapsedDiffers()
    {
        var json = """{"operator": "==", "value": 10}""";
        var context = MakeContext(lastReadingAt: FixedNow.AddMinutes(-12));

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task UnknownOperator_ReturnsFalse()
    {
        var json = """{"operator": "~", "value": 15}""";
        var context = MakeContext(lastReadingAt: FixedNow.AddMinutes(-20));

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Returns_false_when_no_reading_history_at_all()
    {
        var json = """{"operator": ">", "value": 15}""";
        var context = new SensorContext
        {
            LatestValue = null,
            LatestTimestamp = null,
            TrendRate = null,
            LastReadingAt = null,
        };

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    private static SensorContext MakeContext(DateTime? lastReadingAt) => new()
    {
        LatestValue = 100m,
        LatestTimestamp = FixedNow,
        TrendRate = 0m,
        LastReadingAt = lastReadingAt
    };
}
