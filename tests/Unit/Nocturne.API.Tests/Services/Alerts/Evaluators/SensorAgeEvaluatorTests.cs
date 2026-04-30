using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Evaluators;

[Trait("Category", "Unit")]
public class SensorAgeEvaluatorTests
{
    private static readonly DateTime FixedNow = new(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc);
    private readonly SensorAgeEvaluator _sut;

    public SensorAgeEvaluatorTests()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(FixedNow));
        _sut = new SensorAgeEvaluator(timeProvider);
    }

    [Fact]
    public void ConditionType_ShouldBeSensorAge()
    {
        _sut.ConditionType.Should().Be(AlertConditionType.SensorAge);
    }

    [Fact]
    public async Task NullLastSensorStartAt_ReturnsFalse()
    {
        var json = """{"operator": ">", "value": 10}""";
        var context = MakeContext(lastSensorStartAt: null);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task AgeInDays_ExceedsThreshold_ReturnsTrue()
    {
        var json = """{"operator": ">", "value": 10}""";
        var context = MakeContext(lastSensorStartAt: FixedNow.AddDays(-11));

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task AgeInDays_BelowThreshold_ReturnsFalse()
    {
        var json = """{"operator": ">", "value": 10}""";
        var context = MakeContext(lastSensorStartAt: FixedNow.AddDays(-5));

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task AgeInDays_GreaterOrEqualBoundary_ReturnsTrue()
    {
        var json = """{"operator": ">=", "value": 10}""";
        var context = MakeContext(lastSensorStartAt: FixedNow.AddDays(-10));

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeTrue();
    }

    private static SensorContext MakeContext(DateTime? lastSensorStartAt) => new()
    {
        LatestValue = 100m,
        LatestTimestamp = FixedNow,
        TrendRate = 0m,
        LastReadingAt = FixedNow,
        LastSensorStartAt = lastSensorStartAt
    };
}
