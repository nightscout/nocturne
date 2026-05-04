using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Evaluators;

[Trait("Category", "Unit")]
public class TimeSinceLastCarbEvaluatorTests
{
    private static readonly DateTime FixedNow = new(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc);

    private readonly TimeSinceLastCarbEvaluator _sut;

    public TimeSinceLastCarbEvaluatorTests()
    {
        _sut = new TimeSinceLastCarbEvaluator(new FakeTimeProvider(new DateTimeOffset(FixedNow)));
    }

    [Fact]
    public void ConditionType_ShouldBeTimeSinceLastCarb()
    {
        _sut.ConditionType.Should().Be(AlertConditionType.TimeSinceLastCarb);
    }

    [Fact]
    public async Task NoCarbRecord_GreaterEqualThreshold_ReturnsTrue()
    {
        var json = """{"operator": ">=", "minutes": 30}""";
        var ctx = MakeContext(lastCarbAt: null);

        (await _sut.EvaluateAsync(json, ctx, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task NoCarbRecord_LessThanThreshold_ReturnsFalse()
    {
        var json = """{"operator": "<", "minutes": 30}""";
        var ctx = MakeContext(lastCarbAt: null);

        (await _sut.EvaluateAsync(json, ctx, CancellationToken.None)).Should().BeFalse();
    }

    [Theory]
    [InlineData(">=", 30, 30, true)]   // exactly N min: GTE matches
    [InlineData(">", 30, 30, false)]   // exactly N min: GT does not match
    [InlineData(">=", 30, 29, false)]
    [InlineData("<", 10, 5, true)]
    [InlineData("<", 10, 10, false)]
    [InlineData("<=", 10, 10, true)]
    public async Task ElapsedComparison(string op, int threshold, int minutesAgo, bool expected)
    {
        var json = $$"""{"operator": "{{op}}", "minutes": {{threshold}}}""";
        var ctx = MakeContext(lastCarbAt: FixedNow.AddMinutes(-minutesAgo));

        (await _sut.EvaluateAsync(json, ctx, CancellationToken.None)).Should().Be(expected);
    }

    private static SensorContext MakeContext(DateTime? lastCarbAt) => new()
    {
        LatestValue = 100m,
        LatestTimestamp = FixedNow,
        TrendRate = 0m,
        LastReadingAt = FixedNow,
        LastCarbAt = lastCarbAt,
    };
}
