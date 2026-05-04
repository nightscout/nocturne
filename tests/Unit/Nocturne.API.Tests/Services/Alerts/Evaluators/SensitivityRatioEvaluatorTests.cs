using FluentAssertions;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Evaluators;

[Trait("Category", "Unit")]
public class SensitivityRatioEvaluatorTests
{
    private readonly SensitivityRatioEvaluator _sut = new();

    [Fact]
    public void ConditionType_ShouldBeSensitivityRatio()
    {
        _sut.ConditionType.Should().Be(AlertConditionType.SensitivityRatio);
    }

    [Fact]
    public async Task ColdStart_ReturnsFalse()
    {
        var json = """{"operator": "<", "value": 0.8}""";
        var context = MakeContext(sensitivityRatio: 0.5m, hasEverApsSensitivity: false);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task NullRatio_WithFlag_ReturnsFalse()
    {
        var json = """{"operator": "<", "value": 0.8}""";
        var context = MakeContext(sensitivityRatio: null, hasEverApsSensitivity: true);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Theory]
    [InlineData("<", 0.8, 0.5, true)]
    [InlineData("<", 0.8, 1.0, false)]
    [InlineData(">", 1.2, 1.5, true)]
    [InlineData("==", 1.0, 1.0, true)]
    public async Task OperatorDispatch(string op, decimal threshold, decimal actual, bool expected)
    {
        var json = $$"""{"operator": "{{op}}", "value": {{threshold}}}""";
        var context = MakeContext(sensitivityRatio: actual, hasEverApsSensitivity: true);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().Be(expected);
    }

    private static SensorContext MakeContext(decimal? sensitivityRatio, bool hasEverApsSensitivity) => new()
    {
        LatestValue = 100m,
        LatestTimestamp = DateTime.UtcNow,
        TrendRate = 0m,
        LastReadingAt = DateTime.UtcNow,
        SensitivityRatio = sensitivityRatio,
        HasEverApsSensitivity = hasEverApsSensitivity,
    };
}
