using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Evaluators;

[Trait("Category", "Unit")]
public class SignalLossEvaluatorTests
{
    private static readonly DateTime FixedNow = new(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc);
    private readonly SignalLossEvaluator _sut = new(new FakeTimeProvider(new DateTimeOffset(FixedNow)));

    [Fact]
    public void ConditionType_ShouldBeSignalLoss()
    {
        _sut.ConditionType.Should().Be(AlertConditionType.SignalLoss);
    }

    [Theory]
    [InlineData(-14.99, false)]
    [InlineData(-15, true)]
    [InlineData(-60, true)]
    [InlineData(0, false)]
    public async Task FiresOnceTheTimeoutIsReached(double lastReadingOffsetMinutes, bool expected)
    {
        var context = MakeContext(FixedNow.AddMinutes(lastReadingOffsetMinutes), FixedNow);

        (await Evaluate("""{"timeout_minutes": 15}""", context)).Should().Be(expected);
    }

    [Fact]
    public async Task NoReadingHistory_IsFalse()
    {
        (await Evaluate("""{"timeout_minutes": 15}""", MakeContext(null, null))).Should().BeFalse();
    }

    [Fact]
    public async Task NullLastReadingWithALatestTimestamp_IsInfinitelyStale()
    {
        var context = MakeContext(null, FixedNow.AddMinutes(-1));

        (await Evaluate("""{"timeout_minutes": 15}""", context)).Should().BeTrue();
    }

    [Theory]
    [InlineData("""{"timeout_minutes": 0}""")]
    [InlineData("""{"timeout_minutes": -5}""")]
    [InlineData("{}")]
    public async Task NonPositiveTimeout_IsFalse(string json)
    {
        var context = MakeContext(FixedNow.AddHours(-2), FixedNow.AddHours(-2));

        (await Evaluate(json, context)).Should().BeFalse();
    }

    private Task<bool> Evaluate(string json, SensorContext context) =>
        _sut.EvaluateAsync(json, context, CancellationToken.None);

    private static SensorContext MakeContext(DateTime? lastReadingAt, DateTime? latestTimestamp) => new()
    {
        LatestValue = null,
        LatestTimestamp = latestTimestamp,
        TrendRate = null,
        LastReadingAt = lastReadingAt,
    };
}
