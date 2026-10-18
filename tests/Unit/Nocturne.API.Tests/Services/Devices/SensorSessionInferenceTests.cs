using FluentAssertions;
using Nocturne.API.Services.Devices;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Devices;

public class SensorSessionInferenceTests
{
    private static readonly DateTime Origin = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan FiveMinutes = TimeSpan.FromMinutes(5);

    private static readonly CgmProperties G6 = new()
    {
        SensorDurationDays = 10,
        WarmupMinutes = 120,
        UpdateIntervalMinutes = 5,
        HasSeparateTransmitter = true,
        TransmitterDurationDays = 90,
    };

    private static readonly CgmProperties G7 = new()
    {
        SensorDurationDays = 10,
        WarmupMinutes = 30,
        UpdateIntervalMinutes = 5,
        HasSeparateTransmitter = false,
    };

    private static List<DateTime> Run(DateTime from, DateTime to)
    {
        var readings = new List<DateTime>();
        for (var t = from; t <= to; t += FiveMinutes)
            readings.Add(t);
        return readings;
    }

    /// <summary>Readings from <see cref="Origin"/> for four hours, a gap, then four more hours.</summary>
    private static List<DateTime> WithGap(TimeSpan gap)
    {
        var before = Run(Origin, Origin.AddHours(4));
        var resumed = before[^1] + gap;
        return [.. before, .. Run(resumed, resumed.AddHours(4))];
    }

    private static DateTime LongAfter(IReadOnlyList<DateTime> readings) => readings[^1].AddDays(1);

    [Fact]
    public void AWarmupSizedGap_InfersAStartOneWarmupBeforeReadingsResume()
    {
        var readings = WithGap(TimeSpan.FromMinutes(135));
        var resumed = Origin.AddHours(4).AddMinutes(135);

        var starts = SensorSessionInference.Detect(readings, G6, [], LongAfter(readings));

        starts.Should().ContainSingle().Which.Should().Be(new InferredSensorStart(
            StartedAt: resumed.AddMinutes(-120),
            LastReadingBefore: Origin.AddHours(4),
            FirstReadingAfter: resumed));
    }

    [Fact]
    public void AGapShorterThanTheWarmup_IsADropout()
    {
        var readings = WithGap(TimeSpan.FromMinutes(90));

        SensorSessionInference.Detect(readings, G6, [], LongAfter(readings)).Should().BeEmpty();
    }

    [Fact]
    public void AGapExactlyOneWarmupLong_IsASensorChange()
    {
        var readings = WithGap(TimeSpan.FromMinutes(30));

        var starts = SensorSessionInference.Detect(readings, G7, [], LongAfter(readings));

        starts.Should().ContainSingle().Which.StartedAt.Should().Be(Origin.AddHours(4));
    }

    [Fact]
    public void AGapFarLongerThanTheWarmup_IsAnOutage()
    {
        var readings = WithGap(TimeSpan.FromMinutes(120) + SensorSessionInference.MaxChangeTime + FiveMinutes);

        SensorSessionInference.Detect(readings, G6, [], LongAfter(readings)).Should().BeEmpty();
    }

    [Fact]
    public void AGapAtTheChangeTimeCeiling_IsStillASensorChange()
    {
        var readings = WithGap(TimeSpan.FromMinutes(120) + SensorSessionInference.MaxChangeTime);

        SensorSessionInference.Detect(readings, G6, [], LongAfter(readings)).Should().ContainSingle();
    }

    /// <summary>
    /// A reconnecting phone backfills a dropout shortly after readings resume, so a gap is only
    /// judged once the series has run on past it for a while.
    /// </summary>
    [Fact]
    public void AGapThatHasNotSettled_IsNotJudgedYet()
    {
        var readings = WithGap(TimeSpan.FromMinutes(45));
        var resumed = Origin.AddHours(4).AddMinutes(45);

        SensorSessionInference.Detect(readings, G7, [], resumed + SensorSessionInference.SettleTime - TimeSpan.FromMinutes(1))
            .Should().BeEmpty();
        SensorSessionInference.Detect(readings, G7, [], resumed + SensorSessionInference.SettleTime)
            .Should().ContainSingle();
    }

    [Fact]
    public void TheStartOfTheSeries_IsNotASensorChange()
    {
        var readings = Run(Origin, Origin.AddHours(8));

        SensorSessionInference.Detect(readings, G7, [], LongAfter(readings)).Should().BeEmpty();
    }

    [Fact]
    public void ARecordedSessionEventInTheGap_Wins()
    {
        var readings = WithGap(TimeSpan.FromMinutes(40));
        var recorded = Origin.AddHours(4).AddMinutes(10);

        SensorSessionInference.Detect(readings, G7, [recorded], LongAfter(readings)).Should().BeEmpty();
    }

    /// <summary>A sensor change is often logged a little before the old sensor comes off, or after the new one is warm.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void ARecordedSessionEventJustOutsideTheGap_Wins(int side)
    {
        var readings = WithGap(TimeSpan.FromMinutes(40));
        var edge = side < 0 ? Origin.AddHours(4) : Origin.AddHours(4).AddMinutes(40);
        var recorded = edge + side * (SensorSessionInference.RecordedEventSlack - TimeSpan.FromMinutes(1));

        SensorSessionInference.Detect(readings, G7, [recorded], LongAfter(readings)).Should().BeEmpty();
    }

    [Fact]
    public void ARecordedSessionEventFarFromTheGap_DoesNotSuppressIt()
    {
        var readings = WithGap(TimeSpan.FromMinutes(40));
        var recorded = Origin.AddHours(4) - SensorSessionInference.RecordedEventSlack - TimeSpan.FromMinutes(1);

        SensorSessionInference.Detect(readings, G7, [recorded], LongAfter(readings)).Should().ContainSingle();
    }

    [Fact]
    public void EachSensorChangeInTheSeries_IsInferred()
    {
        var first = Run(Origin, Origin.AddDays(1));
        var second = Run(first[^1].AddMinutes(35), first[^1].AddDays(1));
        var third = Run(second[^1].AddMinutes(50), second[^1].AddDays(1));
        List<DateTime> readings = [.. first, .. second, .. third];

        var starts = SensorSessionInference.Detect(readings, G7, [], LongAfter(readings));

        starts.Select(s => s.FirstReadingAfter).Should().Equal(second[0], third[0]);
    }

    [Fact]
    public void UnorderedAndDuplicateReadings_AreHandled()
    {
        var readings = WithGap(TimeSpan.FromMinutes(40));
        List<DateTime> shuffled = [.. readings.AsEnumerable().Reverse(), .. readings.Take(10)];

        SensorSessionInference.Detect(shuffled, G7, [], LongAfter(readings))
            .Should().Equal(SensorSessionInference.Detect(readings, G7, [], LongAfter(readings)));
    }
}
