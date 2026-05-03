using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Evaluators;

[Trait("Category", "Unit")]
public class PumpStateEvaluatorTests
{
    private static readonly DateTime FixedNow = new(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc);

    private readonly PumpStateEvaluator _sut;

    public PumpStateEvaluatorTests()
    {
        _sut = new PumpStateEvaluator(new FakeTimeProvider(new DateTimeOffset(FixedNow)));
    }

    [Fact]
    public void ConditionType_ShouldBePumpState()
    {
        _sut.ConditionType.Should().Be(AlertConditionType.PumpState);
    }

    [Fact]
    public async Task IsActiveTrue_ManualActive31Min_ForMinutes30_ReturnsTrue()
    {
        var json = """{"mode": "Manual", "is_active": true, "for_minutes": 30}""";
        var ctx = MakeContext(new PumpStateSnapshot(PumpModeState.Manual, FixedNow.AddMinutes(-31)));

        (await _sut.EvaluateAsync(json, ctx, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task IsActiveTrue_ManualActive29Min_ForMinutes30_ReturnsFalse()
    {
        var json = """{"mode": "Manual", "is_active": true, "for_minutes": 30}""";
        var ctx = MakeContext(new PumpStateSnapshot(PumpModeState.Manual, FixedNow.AddMinutes(-29)));

        (await _sut.EvaluateAsync(json, ctx, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task IsActiveFalse_PumpInAutomatic_RuleManual_ReturnsTrue()
    {
        var json = """{"mode": "Manual", "is_active": false}""";
        var ctx = MakeContext(new PumpStateSnapshot(PumpModeState.Automatic, FixedNow.AddMinutes(-60)));

        (await _sut.EvaluateAsync(json, ctx, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task IsActiveFalse_NoActiveMode_ReturnsTrue()
    {
        var json = """{"mode": "Manual", "is_active": false}""";
        var ctx = MakeContext(activePumpState: null);

        (await _sut.EvaluateAsync(json, ctx, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task IsActiveTrue_NoActiveMode_ReturnsFalse()
    {
        var json = """{"mode": "Manual", "is_active": true}""";
        var ctx = MakeContext(activePumpState: null);

        (await _sut.EvaluateAsync(json, ctx, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task IsActiveTrue_NoForMinutes_ReturnsTrue()
    {
        var json = """{"mode": "Manual", "is_active": true}""";
        var ctx = MakeContext(new PumpStateSnapshot(PumpModeState.Manual, FixedNow.AddMinutes(-1)));

        (await _sut.EvaluateAsync(json, ctx, CancellationToken.None)).Should().BeTrue();
    }

    private static SensorContext MakeContext(PumpStateSnapshot? activePumpState) => new()
    {
        LatestValue = 100m,
        LatestTimestamp = FixedNow,
        TrendRate = 0m,
        LastReadingAt = FixedNow,
        ActivePumpState = activePumpState,
    };
}
