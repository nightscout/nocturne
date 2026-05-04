using FluentAssertions;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Evaluators;

[Trait("Category", "Unit")]
public class ReservoirEvaluatorTests
{
    private readonly ReservoirEvaluator _sut = new();

    [Fact]
    public void ConditionType_ShouldBeReservoir()
    {
        _sut.ConditionType.Should().Be(AlertConditionType.Reservoir);
    }

    [Fact]
    public async Task NullReservoir_ReturnsFalse()
    {
        var json = """{"operator": "<", "value": 20}""";
        var context = MakeContext(reservoir: null);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Theory]
    [InlineData("<", 20.0, 15.0, true)]
    [InlineData("<", 20.0, 25.0, false)]
    [InlineData("<=", 20.0, 20.0, true)]
    [InlineData(">=", 20.0, 25.0, true)]
    public async Task OperatorDispatch(string op, decimal threshold, decimal actual, bool expected)
    {
        var json = $$"""{"operator": "{{op}}", "value": {{threshold}}}""";
        var context = MakeContext(reservoir: actual);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().Be(expected);
    }

    private static SensorContext MakeContext(decimal? reservoir) => new()
    {
        LatestValue = 100m,
        LatestTimestamp = new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc),
        TrendRate = 0m,
        LastReadingAt = new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc),
        ReservoirUnits = reservoir
    };
}
