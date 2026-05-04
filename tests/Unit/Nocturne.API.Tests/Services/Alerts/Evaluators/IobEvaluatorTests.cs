using FluentAssertions;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Evaluators;

[Trait("Category", "Unit")]
public class IobEvaluatorTests
{
    private readonly IobEvaluator _sut = new();

    [Fact]
    public void ConditionType_ShouldBeIob()
    {
        _sut.ConditionType.Should().Be(AlertConditionType.Iob);
    }

    [Fact]
    public async Task NullIob_ReturnsFalse()
    {
        var json = """{"operator": ">", "value": 2}""";
        var context = MakeContext(iob: null);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Theory]
    [InlineData(">", 2.0, 3.0, true)]
    [InlineData(">", 2.0, 1.5, false)]
    [InlineData("<", 2.0, 1.0, true)]
    [InlineData("<=", 2.0, 2.0, true)]
    [InlineData(">=", 2.0, 2.0, true)]
    [InlineData("==", 2.0, 2.0, true)]
    [InlineData("==", 2.0, 1.9, false)]
    public async Task OperatorDispatch(string op, decimal threshold, decimal actual, bool expected)
    {
        var json = $$"""{"operator": "{{op}}", "value": {{threshold}}}""";
        var context = MakeContext(iob: actual);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().Be(expected);
    }

    private static SensorContext MakeContext(decimal? iob) => new()
    {
        LatestValue = 100m,
        LatestTimestamp = new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc),
        TrendRate = 0m,
        LastReadingAt = new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc),
        IobUnits = iob
    };
}
