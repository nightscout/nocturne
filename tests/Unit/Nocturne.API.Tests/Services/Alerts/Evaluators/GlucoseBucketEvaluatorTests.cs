using FluentAssertions;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Evaluators;

[Trait("Category", "Unit")]
public class GlucoseBucketEvaluatorTests
{
    private readonly GlucoseBucketEvaluator _sut = new();

    [Fact]
    public void ConditionType_ShouldBeGlucoseBucket()
    {
        _sut.ConditionType.Should().Be(AlertConditionType.GlucoseBucket);
    }

    [Fact]
    public async Task NullBucket_ReturnsFalse()
    {
        var json = """{"buckets": ["very_low", "low"]}""";
        var ctx = MakeContext(bucket: null);

        (await _sut.EvaluateAsync(json, ctx, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task EmptyBucketList_ReturnsFalse()
    {
        var json = """{"buckets": []}""";
        var ctx = MakeContext(bucket: GlucoseBucket.Low);

        (await _sut.EvaluateAsync(json, ctx, CancellationToken.None)).Should().BeFalse();
    }

    [Theory]
    [InlineData("very_low", GlucoseBucket.VeryLow, true)]
    [InlineData("low", GlucoseBucket.Low, true)]
    [InlineData("tight_range", GlucoseBucket.TightRange, true)]
    [InlineData("in_range", GlucoseBucket.InRange, true)]
    [InlineData("high", GlucoseBucket.High, true)]
    [InlineData("very_high", GlucoseBucket.VeryHigh, true)]
    [InlineData("very_low", GlucoseBucket.Low, false)]
    [InlineData("high", GlucoseBucket.InRange, false)]
    public async Task SingleBucketSelection(string wireBucket, GlucoseBucket actual, bool expected)
    {
        var json = $$"""{"buckets": ["{{wireBucket}}"]}""";
        var ctx = MakeContext(bucket: actual);

        (await _sut.EvaluateAsync(json, ctx, CancellationToken.None)).Should().Be(expected);
    }

    [Fact]
    public async Task MultipleBucketSelection_AnyMatch()
    {
        var json = """{"buckets": ["very_low", "low", "very_high"]}""";

        (await _sut.EvaluateAsync(json, MakeContext(GlucoseBucket.VeryLow), CancellationToken.None)).Should().BeTrue();
        (await _sut.EvaluateAsync(json, MakeContext(GlucoseBucket.Low), CancellationToken.None)).Should().BeTrue();
        (await _sut.EvaluateAsync(json, MakeContext(GlucoseBucket.VeryHigh), CancellationToken.None)).Should().BeTrue();
        (await _sut.EvaluateAsync(json, MakeContext(GlucoseBucket.TightRange), CancellationToken.None)).Should().BeFalse();
        (await _sut.EvaluateAsync(json, MakeContext(GlucoseBucket.InRange), CancellationToken.None)).Should().BeFalse();
        (await _sut.EvaluateAsync(json, MakeContext(GlucoseBucket.High), CancellationToken.None)).Should().BeFalse();
    }

    [Theory]
    // Defaults: vLow=54, low=70, tHigh=140, high=180, vHigh=250
    [InlineData(53.0, GlucoseBucket.VeryLow)]
    [InlineData(54.0, GlucoseBucket.Low)]    // 54 is Low (clinical convention)
    [InlineData(69.0, GlucoseBucket.Low)]
    [InlineData(70.0, GlucoseBucket.TightRange)]
    [InlineData(140.0, GlucoseBucket.TightRange)]
    [InlineData(141.0, GlucoseBucket.InRange)]
    [InlineData(180.0, GlucoseBucket.InRange)]
    [InlineData(181.0, GlucoseBucket.High)]
    [InlineData(250.0, GlucoseBucket.High)]
    [InlineData(251.0, GlucoseBucket.VeryHigh)]
    public void Resolver_BoundarySemantics(double glucose, GlucoseBucket expected)
    {
        var bucket = GlucoseBucketResolver.Compute((decimal)glucose, 70m, 180m, null, null, null);
        bucket.Should().Be(expected);
    }

    private static SensorContext MakeContext(GlucoseBucket? bucket) => new()
    {
        LatestValue = 100m,
        LatestTimestamp = new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc),
        TrendRate = 0m,
        LastReadingAt = new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc),
        GlucoseBucket = bucket,
    };
}
