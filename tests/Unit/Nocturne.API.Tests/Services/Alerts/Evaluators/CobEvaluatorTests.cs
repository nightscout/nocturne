using FluentAssertions;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Evaluators;

[Trait("Category", "Unit")]
public class CobEvaluatorTests
{
    private readonly CobEvaluator _sut = new();

    [Fact]
    public void ConditionType_ShouldBeCob()
    {
        _sut.ConditionType.Should().Be(AlertConditionType.Cob);
    }

    [Fact]
    public async Task NullCob_ReturnsFalse()
    {
        var json = """{"operator": ">", "value": 30}""";
        var context = MakeContext(cob: null);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Theory]
    [InlineData(">", 30.0, 45.0, true)]
    [InlineData("<", 30.0, 20.0, true)]
    [InlineData(">=", 30.0, 30.0, true)]
    [InlineData(">", 30.0, 30.0, false)]
    public async Task OperatorDispatch(string op, decimal threshold, decimal actual, bool expected)
    {
        var json = $$"""{"operator": "{{op}}", "value": {{threshold}}}""";
        var context = MakeContext(cob: actual);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().Be(expected);
    }

    private static SensorContext MakeContext(decimal? cob) => new()
    {
        LatestValue = 100m,
        LatestTimestamp = new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc),
        TrendRate = 0m,
        LastReadingAt = new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc),
        CobGrams = cob
    };
}
