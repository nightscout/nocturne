using FluentAssertions;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

[Trait("Category", "Unit")]
public class SmartSnoozeTrendGateTests
{
    private static readonly DateTime Now = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);

    private const string Low = """{"direction":"below","value":70}""";
    private const string High = """{"direction":"above","value":180}""";

    /// <summary>Readings at 5-minute cadence, newest last, the newest <paramref name="ageMinutes"/> old.</summary>
    private static List<GlucosePoint> FiveMinute(double tMinus10, double tMinus5, double latest, double ageMinutes = 1) =>
    [
        new(Now.AddMinutes(-ageMinutes - 10), tMinus10),
        new(Now.AddMinutes(-ageMinutes - 5), tMinus5),
        new(Now.AddMinutes(-ageMinutes), latest),
    ];

    private static TrendGateOutcome Evaluate(string conditionParams, IReadOnlyList<GlucosePoint> readings,
        AlertConditionType type = AlertConditionType.Threshold)
        => SmartSnoozeTrendGate.Evaluate(type, conditionParams, readings, Now);

    [Fact]
    public void Low_NoiseLevelRise_DoesNotExtend()
    {
        // +0.1 mg/dL/min — the sign-only gate this replaces extended on this.
        Evaluate(Low, FiveMinute(60, 60.5, 61)).Should().Be(TrendGateOutcome.NotFavorable);
    }

    [Fact]
    public void Low_GenuineRiseOverFiveMinutes_Extends()
    {
        Evaluate(Low, FiveMinute(58, 60, 65)).Should().Be(TrendGateOutcome.Favorable);
    }

    [Fact]
    public void Low_SteadyRiseOverTenMinutes_ExtendsThroughTheLongLookback()
    {
        // +3 in the last 5 minutes (short gate fails) but +11 over 10 minutes.
        Evaluate(Low, FiveMinute(55, 63, 66)).Should().Be(TrendGateOutcome.Favorable);
    }

    [Fact]
    public void Low_RiseExactlyAtBothThresholds_DoesNotExtend()
    {
        Evaluate(Low, FiveMinute(60, 66, 70)).Should().Be(TrendGateOutcome.NotFavorable);
    }

    [Fact]
    public void Low_Falling_DoesNotExtend()
    {
        Evaluate(Low, FiveMinute(70, 65, 60)).Should().Be(TrendGateOutcome.NotFavorable);
    }

    [Fact]
    public void Asymmetric_SameSmallMovementReleasesAHighButNotALow()
    {
        Evaluate(High, FiveMinute(200, 200, 198.5)).Should().Be(TrendGateOutcome.Favorable);
        Evaluate(Low, FiveMinute(60, 60, 61.5)).Should().Be(TrendGateOutcome.NotFavorable);
    }

    [Fact]
    public void High_FallOverTenMinutesAboveTwo_Extends()
    {
        // -0.5 in 5 minutes, -2.5 over 10.
        Evaluate(High, FiveMinute(202.5, 200.5, 200)).Should().Be(TrendGateOutcome.Favorable);
    }

    [Fact]
    public void High_FallExactlyAtBothThresholds_DoesNotExtend()
    {
        Evaluate(High, FiveMinute(202, 201, 200)).Should().Be(TrendGateOutcome.NotFavorable);
    }

    [Fact]
    public void High_Rising_DoesNotExtend()
    {
        Evaluate(High, FiveMinute(200, 205, 210)).Should().Be(TrendGateOutcome.NotFavorable);
    }

    [Fact]
    public void OneMinuteCadence_UsesWallClockSpansNotReadingCounts()
    {
        // +0.9 mg/dL/min: under 4 per reading, but +4.5 over 5 minutes.
        var rising = Enumerable.Range(0, 12)
            .Select(i => new GlucosePoint(Now.AddMinutes(-i), 70 - 0.9 * i))
            .ToList();
        Evaluate(Low, rising).Should().Be(TrendGateOutcome.Favorable);

        // +0.5 mg/dL/min: +2.5 over 5 minutes, +5 over 10.
        var drifting = Enumerable.Range(0, 12)
            .Select(i => new GlucosePoint(Now.AddMinutes(-i), 70 - 0.5 * i))
            .ToList();
        Evaluate(Low, drifting).Should().Be(TrendGateOutcome.NotFavorable);
    }

    [Fact]
    public void InputOrder_DoesNotMatter()
    {
        var readings = FiveMinute(58, 60, 65);
        readings.Reverse();
        Evaluate(Low, readings).Should().Be(TrendGateOutcome.Favorable);
    }

    [Fact]
    public void NoReadings_IsInsufficientData()
    {
        Evaluate(Low, []).Should().Be(TrendGateOutcome.InsufficientData);
    }

    [Fact]
    public void StaleNewestReading_IsInsufficientData()
    {
        Evaluate(Low, FiveMinute(50, 60, 75, ageMinutes: 12)).Should().Be(TrendGateOutcome.InsufficientData);
        Evaluate(Low, FiveMinute(50, 60, 75, ageMinutes: 11)).Should().Be(TrendGateOutcome.Favorable);
    }

    [Fact]
    public void MissingFiveMinuteReading_IsInsufficientData()
    {
        List<GlucosePoint> readings = [new(Now.AddMinutes(-11), 50), new(Now.AddMinutes(-1), 75)];
        Evaluate(Low, readings).Should().Be(TrendGateOutcome.InsufficientData);
    }

    [Fact]
    public void MissingTenMinuteReading_IsInsufficientData()
    {
        List<GlucosePoint> readings = [new(Now.AddMinutes(-6), 50), new(Now.AddMinutes(-1), 75)];
        Evaluate(Low, readings).Should().Be(TrendGateOutcome.InsufficientData);
    }

    [Fact]
    public void ReadingsOutsideTheSlotTolerance_AreNotUsed()
    {
        // Newest at -1; slots are centred on -6 and -11 with ±2.5 min.
        List<GlucosePoint> readings =
        [
            new(Now.AddMinutes(-14), 40),
            new(Now.AddMinutes(-2.5), 60),
            new(Now.AddMinutes(-1), 75),
        ];
        Evaluate(Low, readings).Should().Be(TrendGateOutcome.InsufficientData);
    }

    [Theory]
    [InlineData(AlertConditionType.Composite)]
    [InlineData(AlertConditionType.Predicted)]
    [InlineData(AlertConditionType.RateOfChange)]
    public void NonThresholdRule_IsNotApplicable(AlertConditionType type)
    {
        Evaluate(Low, FiveMinute(50, 60, 75), type).Should().Be(TrendGateOutcome.NotApplicable);
    }

    [Theory]
    [InlineData("""{"direction":"sideways","value":70}""")]
    [InlineData("""{"value":70}""")]
    [InlineData("not json")]
    public void UnreadableDirection_IsNotApplicable(string conditionParams)
    {
        Evaluate(conditionParams, FiveMinute(50, 60, 75)).Should().Be(TrendGateOutcome.NotApplicable);
    }

    [Theory]
    [InlineData("""{"direction":"below","value":70}""")]
    [InlineData("""{"Direction":"BELOW","Value":70}""")]
    public void Direction_IsReadCaseInsensitively(string conditionParams)
    {
        // The stored wire form is snake_case; a case-sensitive read would see no direction and
        // make the fallback a silent no-op.
        Evaluate(conditionParams, FiveMinute(58, 60, 65)).Should().Be(TrendGateOutcome.Favorable);
    }

    [Fact]
    public void ThresholdValue_PlaysNoPart_SoItsDisplayUnitCannotMatter()
    {
        // A value typed in mmol/L would be nonsense as mg/dL; the gate reads only the direction
        // and the readings, which are stored in mg/dL.
        const string mmolLookingLow = """{"direction":"below","value":3.9}""";
        Evaluate(mmolLookingLow, FiveMinute(58, 60, 65)).Should().Be(TrendGateOutcome.Favorable);
        Evaluate(mmolLookingLow, FiveMinute(60, 60.5, 61)).Should().Be(TrendGateOutcome.NotFavorable);
    }
}
