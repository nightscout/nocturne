using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Evaluators;

[Trait("Category", "Unit")]
public class StateSpanActiveEvaluatorTests
{
    private static readonly DateTime FixedNow = new(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc);

    private readonly StateSpanActiveEvaluator _sut;

    public StateSpanActiveEvaluatorTests()
    {
        _sut = new StateSpanActiveEvaluator(new FakeTimeProvider(new DateTimeOffset(FixedNow)));
    }

    [Fact]
    public void ConditionType_ShouldBeStateSpanActive()
    {
        _sut.ConditionType.Should().Be(AlertConditionType.StateSpanActive);
    }

    [Fact]
    public async Task SleepActive6h_IsActiveTrue_ReturnsTrue()
    {
        var json = """{"category": "Sleep", "is_active": true}""";
        var ctx = MakeContext((StateSpanCategory.Sleep, null),
            new StateSpanSnapshot(StateSpanCategory.Sleep, null, FixedNow.AddHours(-6)));

        (await _sut.EvaluateAsync(json, ctx, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task SleepActive6h_ForMinutes480_ReturnsFalse()
    {
        // 6h elapsed; rule asks for 8h (480 min).
        var json = """{"category": "Sleep", "is_active": true, "for_minutes": 480}""";
        var ctx = MakeContext((StateSpanCategory.Sleep, null),
            new StateSpanSnapshot(StateSpanCategory.Sleep, null, FixedNow.AddHours(-6)));

        (await _sut.EvaluateAsync(json, ctx, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task NoExerciseSpan_IsActiveTrue_ReturnsFalse()
    {
        var json = """{"category": "Exercise", "is_active": true}""";
        var ctx = MakeContext(activeSpans: new Dictionary<(StateSpanCategory, string?), StateSpanSnapshot>());

        (await _sut.EvaluateAsync(json, ctx, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task NoSleepSpan_IsActiveFalse_ReturnsTrue()
    {
        var json = """{"category": "Sleep", "is_active": false}""";
        var ctx = MakeContext(activeSpans: new Dictionary<(StateSpanCategory, string?), StateSpanSnapshot>());

        (await _sut.EvaluateAsync(json, ctx, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task PumpModeCategory_RejectedAsFalse()
    {
        // Defense in depth: PumpMode must be rejected at controller, but evaluator also fails
        // closed if a hand-edited DB row tries to slip through.
        var json = """{"category": "PumpMode", "state": "Suspended", "is_active": true}""";
        var ctx = MakeContext((StateSpanCategory.PumpMode, "Suspended"),
            new StateSpanSnapshot(StateSpanCategory.PumpMode, "Suspended", FixedNow.AddMinutes(-10)));

        (await _sut.EvaluateAsync(json, ctx, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task SpecificStateMatches()
    {
        var json = """{"category": "PumpConnectivity", "state": "Disconnected", "is_active": true}""";
        var ctx = MakeContext((StateSpanCategory.PumpConnectivity, "Disconnected"),
            new StateSpanSnapshot(StateSpanCategory.PumpConnectivity, "Disconnected", FixedNow.AddMinutes(-3)));

        (await _sut.EvaluateAsync(json, ctx, CancellationToken.None)).Should().BeTrue();
    }

    private static SensorContext MakeContext(
        (StateSpanCategory Category, string? State) key,
        StateSpanSnapshot snapshot)
    {
        return MakeContext(new Dictionary<(StateSpanCategory, string?), StateSpanSnapshot>
        {
            [key] = snapshot,
        });
    }

    private static SensorContext MakeContext(
        Dictionary<(StateSpanCategory, string?), StateSpanSnapshot> activeSpans) => new()
    {
        LatestValue = 100m,
        LatestTimestamp = FixedNow,
        TrendRate = 0m,
        LastReadingAt = FixedNow,
        ActiveStateSpans = activeSpans,
    };
}
