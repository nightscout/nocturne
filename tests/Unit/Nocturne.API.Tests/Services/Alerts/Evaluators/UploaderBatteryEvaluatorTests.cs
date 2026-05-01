using FluentAssertions;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Evaluators;

[Trait("Category", "Unit")]
public class UploaderBatteryEvaluatorTests
{
    private readonly UploaderBatteryEvaluator _sut = new();

    [Fact]
    public void ConditionType_ShouldBeUploaderBattery()
    {
        _sut.ConditionType.Should().Be(AlertConditionType.UploaderBattery);
    }

    [Fact]
    public async Task ColdStart_ReturnsFalse()
    {
        var json = """{"operator": "<", "value": 20}""";
        var context = MakeContext(uploaderBattery: 10m, hasEverUploaderSnapshot: false);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task NullBattery_WithSnapshot_ReturnsFalse()
    {
        var json = """{"operator": "<", "value": 20}""";
        var context = MakeContext(uploaderBattery: null, hasEverUploaderSnapshot: true);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Theory]
    [InlineData("<", 20, 10, true)]
    [InlineData("<", 20, 30, false)]
    [InlineData(">=", 50, 50, true)]
    [InlineData("==", 100, 100, true)]
    public async Task OperatorDispatch(string op, decimal threshold, decimal actual, bool expected)
    {
        var json = $$"""{"operator": "{{op}}", "value": {{threshold}}}""";
        var context = MakeContext(uploaderBattery: actual, hasEverUploaderSnapshot: true);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().Be(expected);
    }

    private static SensorContext MakeContext(decimal? uploaderBattery, bool hasEverUploaderSnapshot) => new()
    {
        LatestValue = 100m,
        LatestTimestamp = DateTime.UtcNow,
        TrendRate = 0m,
        LastReadingAt = DateTime.UtcNow,
        UploaderBatteryPercent = uploaderBattery,
        HasEverUploaderSnapshot = hasEverUploaderSnapshot,
    };
}
