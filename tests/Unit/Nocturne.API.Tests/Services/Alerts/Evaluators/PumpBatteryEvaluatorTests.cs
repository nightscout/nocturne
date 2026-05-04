using FluentAssertions;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Evaluators;

[Trait("Category", "Unit")]
public class PumpBatteryEvaluatorTests
{
    private readonly PumpBatteryEvaluator _sut = new();

    [Fact]
    public void ConditionType_ShouldBePumpBattery()
    {
        _sut.ConditionType.Should().Be(AlertConditionType.PumpBattery);
    }

    [Fact]
    public async Task ColdStart_ReturnsFalse()
    {
        var json = """{"operator": "<", "value": 20}""";
        var context = MakeContext(pumpBattery: 10m, hasEverPumpSnapshot: false);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task NullBattery_WithSnapshot_ReturnsFalse()
    {
        var json = """{"operator": "<", "value": 20}""";
        var context = MakeContext(pumpBattery: null, hasEverPumpSnapshot: true);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Theory]
    [InlineData("<", 20, 15, true)]
    [InlineData("<", 20, 25, false)]
    [InlineData("<=", 20, 20, true)]
    [InlineData(">", 50, 75, true)]
    [InlineData("==", 100, 100, true)]
    public async Task OperatorDispatch(string op, decimal threshold, decimal actual, bool expected)
    {
        var json = $$"""{"operator": "{{op}}", "value": {{threshold}}}""";
        var context = MakeContext(pumpBattery: actual, hasEverPumpSnapshot: true);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().Be(expected);
    }

    private static SensorContext MakeContext(decimal? pumpBattery, bool hasEverPumpSnapshot) => new()
    {
        LatestValue = 100m,
        LatestTimestamp = DateTime.UtcNow,
        TrendRate = 0m,
        LastReadingAt = DateTime.UtcNow,
        PumpBatteryPercent = pumpBattery,
        HasEverPumpSnapshot = hasEverPumpSnapshot,
    };
}
