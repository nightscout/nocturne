using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Evaluators;

[Trait("Category", "Unit")]
public class SiteAgeEvaluatorTests
{
    private static readonly DateTime FixedNow = new(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc);
    private readonly SiteAgeEvaluator _sut;

    public SiteAgeEvaluatorTests()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(FixedNow));
        _sut = new SiteAgeEvaluator(timeProvider);
    }

    [Fact]
    public void ConditionType_ShouldBeSiteAge()
    {
        _sut.ConditionType.Should().Be(AlertConditionType.SiteAge);
    }

    [Fact]
    public async Task NullLastSiteChangeAt_ReturnsFalse()
    {
        var json = """{"operator": ">", "value": 72}""";
        var context = MakeContext(lastSiteChangeAt: null);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task AgeInHours_ExceedsThreshold_ReturnsTrue()
    {
        // 73 hours ago > 72-hour threshold.
        var json = """{"operator": ">", "value": 72}""";
        var context = MakeContext(lastSiteChangeAt: FixedNow.AddHours(-73));

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task AgeInHours_BelowThreshold_ReturnsFalse()
    {
        var json = """{"operator": ">", "value": 72}""";
        var context = MakeContext(lastSiteChangeAt: FixedNow.AddHours(-48));

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task AgeInHours_GreaterOrEqualBoundary_ReturnsTrue()
    {
        var json = """{"operator": ">=", "value": 24}""";
        var context = MakeContext(lastSiteChangeAt: FixedNow.AddHours(-24));

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeTrue();
    }

    private static SensorContext MakeContext(DateTime? lastSiteChangeAt) => new()
    {
        LatestValue = 100m,
        LatestTimestamp = FixedNow,
        TrendRate = 0m,
        LastReadingAt = FixedNow,
        LastSiteChangeAt = lastSiteChangeAt
    };
}
