using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Evaluators;

[Trait("Category", "Unit")]
public class TimeSinceLastBolusEvaluatorTests
{
    private static readonly DateTime FixedNow = new(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc);

    private readonly TimeSinceLastBolusEvaluator _sut;

    public TimeSinceLastBolusEvaluatorTests()
    {
        _sut = new TimeSinceLastBolusEvaluator(new FakeTimeProvider(new DateTimeOffset(FixedNow)));
    }

    [Fact]
    public void ConditionType_ShouldBeTimeSinceLastBolus()
    {
        _sut.ConditionType.Should().Be(AlertConditionType.TimeSinceLastBolus);
    }

    [Fact]
    public async Task NoBolusRecord_GreaterEqualThreshold_ReturnsTrue()
    {
        var json = """{"operator": ">=", "minutes": 60}""";
        var ctx = MakeContext(lastBolusAt: null);

        (await _sut.EvaluateAsync(json, ctx, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task BolusFiveMinutesAgo_LessThanTen_ReturnsTrue()
    {
        var json = """{"operator": "<", "minutes": 10}""";
        var ctx = MakeContext(lastBolusAt: FixedNow.AddMinutes(-5));

        (await _sut.EvaluateAsync(json, ctx, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task BolusExactlyAtThreshold_GreaterEquals_True()
    {
        var json = """{"operator": ">=", "minutes": 30}""";
        var ctx = MakeContext(lastBolusAt: FixedNow.AddMinutes(-30));

        (await _sut.EvaluateAsync(json, ctx, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task BolusExactlyAtThreshold_StrictGreater_False()
    {
        var json = """{"operator": ">", "minutes": 30}""";
        var ctx = MakeContext(lastBolusAt: FixedNow.AddMinutes(-30));

        (await _sut.EvaluateAsync(json, ctx, CancellationToken.None)).Should().BeFalse();
    }

    private static SensorContext MakeContext(DateTime? lastBolusAt) => new()
    {
        LatestValue = 100m,
        LatestTimestamp = FixedNow,
        TrendRate = 0m,
        LastReadingAt = FixedNow,
        LastBolusAt = lastBolusAt,
    };
}
