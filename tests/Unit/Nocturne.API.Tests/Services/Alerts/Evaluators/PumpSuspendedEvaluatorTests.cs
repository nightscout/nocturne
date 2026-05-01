using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Evaluators;

[Trait("Category", "Unit")]
public class PumpSuspendedEvaluatorTests
{
    private static readonly DateTime FixedNow = new(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc);

    private readonly PumpSuspendedEvaluator _sut;

    public PumpSuspendedEvaluatorTests()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(FixedNow));
        _sut = new PumpSuspendedEvaluator(timeProvider);
    }

    [Fact]
    public void ConditionType_ShouldBePumpSuspended()
    {
        _sut.ConditionType.Should().Be(AlertConditionType.PumpSuspended);
    }

    [Fact]
    public async Task ColdStart_ReturnsFalse()
    {
        var json = """{"is_active": true}""";
        var context = MakeContext(activeSuspension: null, hasEverPumpSnapshot: false);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task IsActiveTrue_NoActiveSuspension_ReturnsFalse()
    {
        var json = """{"is_active": true}""";
        var context = MakeContext(activeSuspension: null, hasEverPumpSnapshot: true);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task IsActiveTrue_ActiveSuspension_NoForMinutes_ReturnsTrue()
    {
        var json = """{"is_active": true}""";
        var context = MakeContext(
            activeSuspension: new PumpSuspensionSnapshot(FixedNow.AddMinutes(-1)),
            hasEverPumpSnapshot: true);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task IsActiveTrue_ForMinutes_Elapsed_ReturnsTrue()
    {
        var json = """{"is_active": true, "for_minutes": 30}""";
        var context = MakeContext(
            activeSuspension: new PumpSuspensionSnapshot(FixedNow.AddMinutes(-45)),
            hasEverPumpSnapshot: true);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task IsActiveTrue_ForMinutes_NotElapsed_ReturnsFalse()
    {
        var json = """{"is_active": true, "for_minutes": 30}""";
        var context = MakeContext(
            activeSuspension: new PumpSuspensionSnapshot(FixedNow.AddMinutes(-10)),
            hasEverPumpSnapshot: true);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task IsActiveFalse_NoActiveSuspension_ReturnsTrue()
    {
        var json = """{"is_active": false}""";
        var context = MakeContext(activeSuspension: null, hasEverPumpSnapshot: true);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task IsActiveFalse_ActiveSuspension_ReturnsFalse()
    {
        var json = """{"is_active": false}""";
        var context = MakeContext(
            activeSuspension: new PumpSuspensionSnapshot(FixedNow.AddMinutes(-5)),
            hasEverPumpSnapshot: true);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task StaleSnapshot_NullSuspension_TreatedAsInactive()
    {
        // When the enricher nulls ActivePumpSuspension because the latest pump snapshot is stale,
        // the evaluator should behave as "no active suspension" — IsActive=true returns false,
        // IsActive=false returns true.
        var json = """{"is_active": true, "for_minutes": 30}""";
        var context = MakeContext(activeSuspension: null, hasEverPumpSnapshot: true);

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    private static SensorContext MakeContext(PumpSuspensionSnapshot? activeSuspension, bool hasEverPumpSnapshot) => new()
    {
        LatestValue = 100m,
        LatestTimestamp = FixedNow,
        TrendRate = 0m,
        LastReadingAt = FixedNow,
        ActivePumpSuspension = activeSuspension,
        HasEverPumpSnapshot = hasEverPumpSnapshot,
    };
}
