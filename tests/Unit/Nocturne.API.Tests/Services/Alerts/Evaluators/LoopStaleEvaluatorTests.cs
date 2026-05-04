using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Evaluators;

[Trait("Category", "Unit")]
public class LoopStaleEvaluatorTests
{
    private static readonly DateTime FixedNow = new(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc);

    private readonly LoopStaleEvaluator _sut;

    public LoopStaleEvaluatorTests()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(FixedNow));
        _sut = new LoopStaleEvaluator(timeProvider);
    }

    [Fact]
    public void ConditionType_ShouldBeLoopStale()
    {
        _sut.ConditionType.Should().Be(AlertConditionType.LoopStale);
    }

    [Fact]
    public async Task HasEverApsCycled_False_ReturnsFalse()
    {
        var json = """{"operator": ">", "minutes": 5}""";
        var context = MakeContext(lastApsCycleAt: null, hasEverApsCycled: false);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Theory]
    [InlineData(">", 5, 10, true)]   // 10 minutes elapsed > 5 -> true
    [InlineData(">", 5, 3, false)]   // 3 minutes elapsed > 5 -> false
    [InlineData("<", 5, 3, true)]
    [InlineData(">=", 5, 5, true)]
    [InlineData("<=", 5, 5, true)]
    public async Task OperatorDispatch(string op, int threshold, int elapsedMinutes, bool expected)
    {
        var json = $$"""{"operator": "{{op}}", "minutes": {{threshold}}}""";
        var context = MakeContext(
            lastApsCycleAt: FixedNow.AddMinutes(-elapsedMinutes),
            hasEverApsCycled: true);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().Be(expected);
    }

    [Fact]
    public async Task ZeroMinutes_AtCurrentTimestamp_ReturnsFalseForGt()
    {
        var json = """{"operator": ">", "minutes": 0}""";
        var context = MakeContext(lastApsCycleAt: FixedNow, hasEverApsCycled: true);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    private static SensorContext MakeContext(DateTime? lastApsCycleAt, bool hasEverApsCycled) => new()
    {
        LatestValue = 100m,
        LatestTimestamp = FixedNow,
        TrendRate = 0m,
        LastReadingAt = FixedNow,
        LastApsCycleAt = lastApsCycleAt,
        HasEverApsCycled = hasEverApsCycled,
    };
}
