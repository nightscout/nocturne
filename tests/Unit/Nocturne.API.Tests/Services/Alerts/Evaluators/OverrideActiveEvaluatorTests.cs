using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Evaluators;

[Trait("Category", "Unit")]
// No cold-start (HasEver*) test: OverrideActive intentionally has no HasEver guard because
// "no active override" is the legitimate empty-value state. See OverrideActiveEvaluator <remarks>.
public class OverrideActiveEvaluatorTests
{
    private static readonly DateTime FixedNow = new(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc);

    private readonly OverrideActiveEvaluator _sut;

    public OverrideActiveEvaluatorTests()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(FixedNow));
        _sut = new OverrideActiveEvaluator(timeProvider);
    }

    [Fact]
    public void ConditionType_ShouldBeOverrideActive()
    {
        _sut.ConditionType.Should().Be(AlertConditionType.OverrideActive);
    }

    [Fact]
    public async Task IsActiveTrue_NoActiveOverride_ReturnsFalse()
    {
        var json = """{"is_active": true}""";
        var context = MakeContext(activeOverride: null);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task IsActiveTrue_ActiveOverride_NoForMinutes_ReturnsTrue()
    {
        var json = """{"is_active": true}""";
        var context = MakeContext(activeOverride: MakeOverride(FixedNow.AddMinutes(-1)));

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task IsActiveTrue_ForMinutes_Elapsed_ReturnsTrue()
    {
        var json = """{"is_active": true, "for_minutes": 30}""";
        var context = MakeContext(activeOverride: MakeOverride(FixedNow.AddMinutes(-45)));

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task IsActiveTrue_ForMinutes_NotElapsed_ReturnsFalse()
    {
        var json = """{"is_active": true, "for_minutes": 30}""";
        var context = MakeContext(activeOverride: MakeOverride(FixedNow.AddMinutes(-10)));

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task IsActiveFalse_NoActiveOverride_ReturnsTrue()
    {
        var json = """{"is_active": false}""";
        var context = MakeContext(activeOverride: null);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task IsActiveFalse_ActiveOverride_ReturnsFalse()
    {
        var json = """{"is_active": false}""";
        var context = MakeContext(activeOverride: MakeOverride(FixedNow.AddMinutes(-5)));

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    private static OverrideSnapshot MakeOverride(DateTime startedAt) =>
        new(startedAt, EndsAt: null, Multiplier: 0.8m, Name: "Activity");

    private static SensorContext MakeContext(OverrideSnapshot? activeOverride) => new()
    {
        LatestValue = 100m,
        LatestTimestamp = FixedNow,
        TrendRate = 0m,
        LastReadingAt = FixedNow,
        ActiveOverride = activeOverride,
    };
}
