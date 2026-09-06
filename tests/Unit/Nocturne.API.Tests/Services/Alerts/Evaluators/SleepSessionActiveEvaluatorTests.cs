using FluentAssertions;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Evaluators;

[Trait("Category", "Unit")]
public class SleepSessionActiveEvaluatorTests
{
    private static readonly DateTime FixedNow = new(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc);

    private readonly SleepSessionActiveEvaluator _sut = new();

    [Fact]
    public void ConditionType_ShouldBeSleepSessionActive()
    {
        _sut.ConditionType.Should().Be(AlertConditionType.SleepSessionActive);
    }

    [Fact]
    public async Task IsActiveTrue_SessionActive_ReturnsTrue()
    {
        var json = """{"is_active": true}""";
        var ctx = MakeContext(sleepActive: true);

        (await _sut.EvaluateAsync(json, ctx, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task IsActiveTrue_NoSession_ReturnsFalse()
    {
        var json = """{"is_active": true}""";
        var ctx = MakeContext(sleepActive: false);

        (await _sut.EvaluateAsync(json, ctx, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task IsActiveFalse_NoSession_ReturnsTrue()
    {
        var json = """{"is_active": false}""";
        var ctx = MakeContext(sleepActive: false);

        (await _sut.EvaluateAsync(json, ctx, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task IsActiveFalse_SessionActive_ReturnsFalse()
    {
        var json = """{"is_active": false}""";
        var ctx = MakeContext(sleepActive: true);

        (await _sut.EvaluateAsync(json, ctx, CancellationToken.None)).Should().BeFalse();
    }

    private static SensorContext MakeContext(bool sleepActive) => new()
    {
        LatestValue = 100m,
        LatestTimestamp = FixedNow,
        TrendRate = 0m,
        LastReadingAt = FixedNow,
        SleepSessionActive = sleepActive,
    };
}
