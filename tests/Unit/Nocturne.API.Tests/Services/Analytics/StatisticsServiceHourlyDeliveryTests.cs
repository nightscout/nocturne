using FluentAssertions;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.Analytics;

/// <summary>
/// Tests for CalculateHourlyInsulinDelivery: duration-weighted hour-of-day
/// bucketing of pump-confirmed delivery in the user's timezone, averaged over
/// days that have data. Guards the nightscout/nocturne#509 regression where a
/// report window extending before the first record produced a phantom
/// multi-hundred-unit spike at the window-start hour.
/// </summary>
public class StatisticsServiceHourlyDeliveryTests
{
    private readonly StatisticsService _sut = new();

    private static readonly DateTime StartDate = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime EndDate = new(2024, 1, 31, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FlatBasal_DistributesAcrossOverlappedHours()
    {
        // 1.2 U/hr from 10:00 to 13:00 on a single day
        var tempBasals = new[]
        {
            MakeTempBasal(rate: 1.2, startUtc: Utc(2024, 1, 1, 10, 0), endUtc: Utc(2024, 1, 1, 13, 0)),
        };

        var result = _sut.CalculateHourlyInsulinDelivery(
            tempBasals, [], [], StartDate, EndDate);

        result.DayCount.Should().Be(1);
        result.Hours[10].Basal.Should().Be(1.2);
        result.Hours[11].Basal.Should().Be(1.2);
        result.Hours[12].Basal.Should().Be(1.2);
        result.Hours[13].Basal.Should().Be(0);
        result.Hours[9].Basal.Should().Be(0);
    }

    [Fact]
    public void PartialHours_SplitOnHourBoundaries()
    {
        // 1.0 U/hr from 10:30 to 11:30 — half an hour in each bucket
        var tempBasals = new[]
        {
            MakeTempBasal(rate: 1.0, startUtc: Utc(2024, 1, 1, 10, 30), endUtc: Utc(2024, 1, 1, 11, 30)),
        };

        var result = _sut.CalculateHourlyInsulinDelivery(
            tempBasals, [], [], StartDate, EndDate);

        result.Hours[10].Basal.Should().Be(0.5);
        result.Hours[11].Basal.Should().Be(0.5);
    }

    [Fact]
    public void UserTimezone_BucketsByLocalHour()
    {
        // 1.0 U/hr from 00:00 to 01:00 UTC; at UTC-8 that's 16:00-17:00 local
        var tz = TimeZoneInfo.CreateCustomTimeZone("test-8", TimeSpan.FromHours(-8), "-8", "-8");
        var tempBasals = new[]
        {
            MakeTempBasal(rate: 1.0, startUtc: Utc(2024, 1, 1, 0, 0), endUtc: Utc(2024, 1, 1, 1, 0)),
        };

        var result = _sut.CalculateHourlyInsulinDelivery(
            tempBasals, [], [], StartDate, EndDate, tz);

        result.Hours[16].Basal.Should().Be(1.0);
        result.Hours[0].Basal.Should().Be(0);
    }

    [Fact]
    public void HalfHourOffsetTimezone_SplitsOnLocalHourBoundaries()
    {
        // 1.0 U/hr from 00:00 to 01:00 UTC; at UTC+9:30 that's 09:30-10:30 local
        var tz = TimeZoneInfo.CreateCustomTimeZone("test+930", TimeSpan.FromMinutes(9 * 60 + 30), "+9:30", "+9:30");
        var tempBasals = new[]
        {
            MakeTempBasal(rate: 1.0, startUtc: Utc(2024, 1, 1, 0, 0), endUtc: Utc(2024, 1, 1, 1, 0)),
        };

        var result = _sut.CalculateHourlyInsulinDelivery(
            tempBasals, [], [], StartDate, EndDate, tz);

        result.Hours[9].Basal.Should().Be(0.5);
        result.Hours[10].Basal.Should().Be(0.5);
    }

    [Fact]
    public void WindowExtendingBeforeFirstRecord_AveragesOverDataDaysOnly()
    {
        // nightscout/nocturne#509: 30-day window, but delivery data exists only
        // for the last two days (flat 0.3 U/hr). Every hour must average 0.3 —
        // no hour may absorb the empty weeks as phantom insulin.
        var tempBasals = new[]
        {
            MakeTempBasal(rate: 0.3, startUtc: Utc(2024, 1, 29, 0, 0), endUtc: Utc(2024, 1, 30, 0, 0), origin: TempBasalOrigin.Scheduled),
            MakeTempBasal(rate: 0.3, startUtc: Utc(2024, 1, 30, 0, 0), endUtc: Utc(2024, 1, 31, 0, 0), origin: TempBasalOrigin.Scheduled),
        };

        var result = _sut.CalculateHourlyInsulinDelivery(
            tempBasals, [], [], StartDate, EndDate);

        result.DayCount.Should().Be(2, "only two days have delivery data");
        foreach (var hour in result.Hours)
        {
            hour.Basal.Should().Be(0.3, $"hour {hour.Hour} must show the flat rate, not a gap artifact");
        }
    }

    [Fact]
    public void Origin_SplitsScheduledFromTempAdjustments()
    {
        var tempBasals = new[]
        {
            MakeTempBasal(rate: 0.5, startUtc: Utc(2024, 1, 1, 8, 0), endUtc: Utc(2024, 1, 1, 9, 0), origin: TempBasalOrigin.Scheduled),
            MakeTempBasal(rate: 1.5, startUtc: Utc(2024, 1, 1, 9, 0), endUtc: Utc(2024, 1, 1, 10, 0), origin: TempBasalOrigin.Algorithm),
        };

        var result = _sut.CalculateHourlyInsulinDelivery(
            tempBasals, [], [], StartDate, EndDate);

        result.Hours[8].ScheduledBasal.Should().Be(0.5);
        result.Hours[8].TempBasal.Should().Be(0);
        result.Hours[9].ScheduledBasal.Should().Be(0);
        result.Hours[9].TempBasal.Should().Be(1.5);
    }

    [Fact]
    public void Boluses_BucketByHour_AlgorithmBolusesCountAsTempBasal()
    {
        var boluses = new[] { MakeBolus(5.0, Utc(2024, 1, 1, 8, 15)) };
        var algorithmBoluses = new[] { MakeBolus(0.4, Utc(2024, 1, 1, 9, 5)) };

        var result = _sut.CalculateHourlyInsulinDelivery(
            [], boluses, algorithmBoluses, StartDate, EndDate);

        result.Hours[8].Bolus.Should().Be(5.0);
        result.Hours[9].Bolus.Should().Be(0);
        result.Hours[9].TempBasal.Should().Be(0.4);
        result.Hours[8].Total.Should().Be(5.0);
    }

    [Fact]
    public void Averages_DivideByDistinctDataDays()
    {
        var boluses = new[]
        {
            MakeBolus(4.0, Utc(2024, 1, 1, 12, 0)),
            MakeBolus(2.0, Utc(2024, 1, 2, 12, 30)),
        };

        var result = _sut.CalculateHourlyInsulinDelivery(
            [], boluses, [], StartDate, EndDate);

        result.DayCount.Should().Be(2);
        result.Hours[12].Bolus.Should().Be(3.0);
        result.Hours[12].Count.Should().Be(2);
    }

    [Fact]
    public void BasalAndBolusAverages_UseSeparateDayDenominators()
    {
        // Profile-derived scheduled segments tiling a whole month (the
        // no-TempBasal fallback) must not dilute boluses that exist on only
        // two days: each component averages over its own data days.
        var tempBasals = Enumerable.Range(0, 30)
            .Select(day => MakeTempBasal(
                rate: 0.5,
                startUtc: StartDate.AddDays(day),
                endUtc: StartDate.AddDays(day + 1),
                origin: TempBasalOrigin.Scheduled))
            .ToArray();
        var boluses = new[]
        {
            MakeBolus(4.0, Utc(2024, 1, 29, 12, 0)),
            MakeBolus(2.0, Utc(2024, 1, 30, 12, 0)),
        };

        var result = _sut.CalculateHourlyInsulinDelivery(
            tempBasals, boluses, [], StartDate, EndDate);

        result.Hours[12].ScheduledBasal.Should().Be(0.5, "basal averages over its 30 covered days");
        result.Hours[12].Bolus.Should().Be(3.0, "boluses average over their own 2 data days");
        result.DayCount.Should().Be(30);
    }

    [Fact]
    public void NoData_ReturnsZeroedHours()
    {
        var result = _sut.CalculateHourlyInsulinDelivery(
            [], [], [], StartDate, EndDate);

        result.Hours.Should().HaveCount(24);
        result.DayCount.Should().Be(0);
        result.Hours.Should().OnlyContain(h => Math.Abs(h.Total) < 1e-9);
    }

    private static DateTime Utc(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    private static TempBasal MakeTempBasal(
        double rate,
        DateTime startUtc,
        DateTime endUtc,
        TempBasalOrigin origin = TempBasalOrigin.Algorithm)
    {
        return new TempBasal
        {
            StartTimestamp = startUtc,
            EndTimestamp = endUtc,
            Rate = rate,
            Origin = origin,
        };
    }

    private static Bolus MakeBolus(double insulin, DateTime timestampUtc)
    {
        return new Bolus
        {
            Insulin = insulin,
            Timestamp = timestampUtc,
        };
    }
}
