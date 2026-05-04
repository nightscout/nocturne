using FluentAssertions;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Evaluators;

[Trait("Category", "Unit")]
public class TrendEvaluatorTests
{
    private readonly TrendEvaluator _sut = new();

    [Fact]
    public void ConditionType_ShouldBeTrend()
    {
        _sut.ConditionType.Should().Be(AlertConditionType.Trend);
    }

    [Fact]
    public async Task NullTrendBucket_ReturnsFalse()
    {
        var json = """{"bucket": "rising_fast"}""";
        var context = MakeContext(trendBucket: null);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Theory]
    [InlineData("unknown", TrendBucket.Unknown)]
    [InlineData("rising_fast", TrendBucket.RisingFast)]
    [InlineData("rising", TrendBucket.Rising)]
    [InlineData("flat", TrendBucket.Flat)]
    [InlineData("falling", TrendBucket.Falling)]
    [InlineData("falling_fast", TrendBucket.FallingFast)]
    public async Task ExactMatch_ReturnsTrue(string bucket, TrendBucket actual)
    {
        var json = $$"""{"bucket": "{{bucket}}"}""";
        var context = MakeContext(trendBucket: actual);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Mismatch_ReturnsFalse()
    {
        var json = """{"bucket": "rising_fast"}""";
        var context = MakeContext(trendBucket: TrendBucket.Falling);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task MatchIsCaseInsensitive()
    {
        var json = """{"bucket": "Rising_Fast"}""";
        var context = MakeContext(trendBucket: TrendBucket.RisingFast);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task UnknownBucketString_ReturnsFalse()
    {
        var json = """{"bucket": "spinning"}""";
        var context = MakeContext(trendBucket: TrendBucket.Rising);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    private static SensorContext MakeContext(TrendBucket? trendBucket) => new()
    {
        LatestValue = 100m,
        LatestTimestamp = new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc),
        TrendRate = 0m,
        LastReadingAt = new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc),
        TrendBucket = trendBucket
    };
}
