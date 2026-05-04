using FluentAssertions;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Evaluators;

[Trait("Category", "Unit")]
public class TempBasalEvaluatorTests
{
    private static readonly DateTime FixedNow = new(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc);
    private readonly TempBasalEvaluator _sut = new();

    [Fact]
    public void ConditionType_ShouldBeTempBasal()
    {
        _sut.ConditionType.Should().Be(AlertConditionType.TempBasal);
    }

    [Theory]
    [InlineData(">")]
    [InlineData("<")]
    [InlineData("==")]
    public async Task NoActiveTemp_ReturnsFalseForAnyOperator(string op)
    {
        var json = $$"""{"metric": "rate", "operator": "{{op}}", "value": 1.0}""";
        var context = MakeContext(activeTemp: null);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Theory]
    [InlineData(">", 1.0, 1.5, true)]
    [InlineData(">", 1.0, 0.5, false)]
    [InlineData("<", 1.0, 0.5, true)]
    [InlineData("==", 1.0, 1.0, true)]
    public async Task RateMetric_ComparesViaComparisonOps(string op, decimal threshold, decimal rate, bool expected)
    {
        var json = $$"""{"metric": "rate", "operator": "{{op}}", "value": {{threshold}}}""";
        var context = MakeContext(activeTemp: new TempBasalSnapshot(rate, ScheduledRate: 0.8m, FixedNow));

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().Be(expected);
    }

    [Fact]
    public async Task PercentMetric_NullScheduled_ReturnsFalse()
    {
        var json = """{"metric": "percent_of_scheduled", "operator": ">", "value": 100}""";
        var context = MakeContext(activeTemp: new TempBasalSnapshot(Rate: 2.0m, ScheduledRate: null, FixedNow));

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task PercentMetric_ZeroScheduled_ReturnsFalse()
    {
        var json = """{"metric": "percent_of_scheduled", "operator": ">", "value": 100}""";
        var context = MakeContext(activeTemp: new TempBasalSnapshot(Rate: 2.0m, ScheduledRate: 0m, FixedNow));

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Theory]
    // 1.5 / 1.0 * 100 = 150
    [InlineData(1.5, 1.0, ">", 100, true)]
    [InlineData(1.5, 1.0, "<", 100, false)]
    // 0.5 / 1.0 * 100 = 50
    [InlineData(0.5, 1.0, "<", 100, true)]
    [InlineData(1.0, 1.0, "==", 100, true)]
    public async Task PercentMetric_BothRatesPresent_ComparesComputedPercent(
        decimal rate, decimal scheduled, string op, decimal threshold, bool expected)
    {
        var json = $$"""{"metric": "percent_of_scheduled", "operator": "{{op}}", "value": {{threshold}}}""";
        var context = MakeContext(activeTemp: new TempBasalSnapshot(rate, scheduled, FixedNow));

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().Be(expected);
    }

    private static SensorContext MakeContext(TempBasalSnapshot? activeTemp) => new()
    {
        LatestValue = 100m,
        LatestTimestamp = FixedNow,
        TrendRate = 0m,
        LastReadingAt = FixedNow,
        ActiveTempBasal = activeTemp,
    };
}
