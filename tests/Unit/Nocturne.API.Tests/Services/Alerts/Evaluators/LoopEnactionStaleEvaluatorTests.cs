using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Evaluators;

[Trait("Category", "Unit")]
public class LoopEnactionStaleEvaluatorTests
{
    private static readonly DateTime FixedNow = new(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc);

    private readonly LoopEnactionStaleEvaluator _sut;

    public LoopEnactionStaleEvaluatorTests()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(FixedNow));
        _sut = new LoopEnactionStaleEvaluator(timeProvider);
    }

    [Fact]
    public void ConditionType_ShouldBeLoopEnactionStale()
    {
        _sut.ConditionType.Should().Be(AlertConditionType.LoopEnactionStale);
    }

    [Fact]
    public async Task HasEverApsCycled_False_ReturnsFalse()
    {
        var json = """{"operator": ">", "minutes": 5}""";
        var context = MakeContext(lastApsEnactedAt: null, hasEverApsCycled: false);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task HasEverApsCycled_True_ButNoEnactedYet_ReturnsFalse()
    {
        // Open-loop scenario: cycles exist but nothing has enacted.
        // The evaluator returns false (no anchor to compare against) rather than firing
        // immediately. Per design these users should not enable the rule.
        var json = """{"operator": ">", "minutes": 5}""";
        var context = MakeContext(lastApsEnactedAt: null, hasEverApsCycled: true);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Theory]
    [InlineData(">", 5, 10, true)]
    [InlineData(">", 5, 3, false)]
    [InlineData("<", 5, 3, true)]
    [InlineData(">=", 5, 5, true)]
    public async Task OperatorDispatch(string op, int threshold, int elapsedMinutes, bool expected)
    {
        var json = $$"""{"operator": "{{op}}", "minutes": {{threshold}}}""";
        var context = MakeContext(
            lastApsEnactedAt: FixedNow.AddMinutes(-elapsedMinutes),
            hasEverApsCycled: true);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().Be(expected);
    }

    private static SensorContext MakeContext(DateTime? lastApsEnactedAt, bool hasEverApsCycled) => new()
    {
        LatestValue = 100m,
        LatestTimestamp = FixedNow,
        TrendRate = 0m,
        LastReadingAt = FixedNow,
        LastApsEnactedAt = lastApsEnactedAt,
        HasEverApsCycled = hasEverApsCycled,
    };
}
