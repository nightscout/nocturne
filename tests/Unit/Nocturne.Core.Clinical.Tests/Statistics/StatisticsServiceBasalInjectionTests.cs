using FluentAssertions;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using static Nocturne.Core.Clinical.Tests.Statistics.WindowEnd;

namespace Nocturne.Core.Clinical.Tests.Statistics;

public class StatisticsServiceBasalInjectionTests
{
    private readonly StatisticsService _sut = new(TimeProvider.System);

    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    private static readonly DateTime WindowStart = BerlinUtc(2026, 11, 1, 0);
    private static readonly DateTime WindowEnd = BerlinUtc(2026, 11, 8, 0);

    [Fact]
    public void HourlyDelivery_DailyDose_ReadsAsTheSameRateInEveryHour()
    {
        var result = _sut.CalculateHourlyInsulinDelivery(
            [], [], [], WindowStart, WindowEnd, Mills(WindowEnd), Berlin, DailyDoses(dia: 42));

        result.Hours.Should().HaveCount(24).And.OnlyContain(h => h.ScheduledBasal == 0.75 && h.Basal == 0.75);
        result.BasalPeriods.DailyTotal.Should().Be(18);
        result.DaysWithData.Should().Be(7);
    }

    [Fact]
    public void BasalAnalysis_DailyDose_ReadsAsAFlatRate()
    {
        var result = _sut.CalculateBasalAnalysis(
            [], WindowStart, WindowEnd, Mills(WindowEnd), Berlin, DailyDoses(dia: 42));

        result.Stats.AvgRate.Should().Be(0.75);
        result.Stats.MinRate.Should().Be(0.75);
        result.Stats.MaxRate.Should().Be(0.75);
        result.Stats.TotalDeliveredPerDay.Should().Be(18);
        result.TempBasalInfo.Total.Should().Be(0, "a basal injection is the baseline, not a temp adjustment");
        result.HourlyPercentiles.Should().HaveCount(24).And.OnlyContain(p =>
            p.P10 == 0.75 && p.P25 == 0.75 && p.Median == 0.75 && p.P75 == 0.75 && p.P90 == 0.75);
    }

    [Theory]
    [InlineData(0, 24)]
    [InlineData(42, 42)]
    public void Pieces_AMissedDose_LeavesAGapAfterTheCap(double dia, double expectedHours)
    {
        var first = BerlinUtc(2026, 11, 1, 21);
        var afterTheMissedDay = first.AddDays(2);
        var pieces = StatisticsService.BasalInjectionPieces(
        [
            Dose(first, 18, dia),
            Dose(afterTheMissedDay, 18, dia),
        ]);

        pieces.Should().HaveCount(2);
        pieces[0].StartTimestamp.Should().Be(first);
        pieces[0].EndTimestamp.Should().Be(first.AddHours(expectedHours));
        pieces[0].Rate.Should().BeApproximately(18 / expectedHours, 1e-9);
        pieces[1].StartTimestamp.Should().Be(afterTheMissedDay);
    }

    [Fact]
    public void Pieces_ADoseRunsUntilTheNextOne()
    {
        var first = BerlinUtc(2026, 11, 1, 21);
        var pieces = StatisticsService.BasalInjectionPieces([Dose(first.AddHours(12), 6, 42), Dose(first, 12, 42)]);

        pieces.Select(p => (p.StartTimestamp, p.EndTimestamp, p.Rate)).Should().Equal(
            (first, first.AddHours(12), 1.0),
            (first.AddHours(12), first.AddHours(54), 6 / 42.0));
        pieces.Should().OnlyContain(p => p.Origin == TempBasalOrigin.Scheduled);
    }

    [Fact]
    public void Pieces_DosesAtTheSameInstant_AreOneDoseOfTheirSum_LastingTheLongestCap()
    {
        var at = BerlinUtc(2026, 11, 1, 21);
        var pieces = StatisticsService.BasalInjectionPieces([Dose(at, 10, 0), Dose(at, 8, 36)]);

        pieces.Should().ContainSingle();
        pieces[0].EndTimestamp.Should().Be(at.AddHours(36));
        pieces[0].Rate.Should().BeApproximately(18 / 36.0, 1e-9);
    }

    [Fact]
    public void Pieces_SkipDosesOfNoUnits()
    {
        var at = BerlinUtc(2026, 11, 1, 21);
        var pieces = StatisticsService.BasalInjectionPieces([Dose(at, 18, 0), Dose(at.AddHours(6), 0, 0)]);

        pieces.Should().ContainSingle().Which.EndTimestamp.Should().Be(at.AddHours(24),
            "a dose of no units is not a dose, so it does not end the one before");
    }

    [Fact]
    public void Pieces_ADiaPastTheReadLookback_IsCappedToIt()
    {
        var at = BerlinUtc(2026, 11, 1, 21);
        var piece = StatisticsService.BasalInjectionPieces([Dose(at, 18, 100)]).Single();

        piece.EndTimestamp.Should().Be(at.AddHours(72));
        StatisticsService.BasalInjectionReadSpan(at.AddHours(72), at.AddHours(80)).Should().Be((at, at.AddHours(152)));
    }

    [Fact]
    public void BasalAnalysis_ADoseBeforeTheWindow_CountsOnlyItsPartInside()
    {
        var dose = Dose(WindowStart.AddHours(-30), 18, 42);

        var result = _sut.CalculateBasalAnalysis(
            [], WindowStart, WindowStart.AddDays(1), Mills(WindowStart.AddDays(1)), Berlin, [dose]);

        result.Stats.TotalDelivered.Should().Be(Math.Round(12 * 18 / 42.0 * 100) / 100);
    }

    [Theory]
    [InlineData("pump")]
    [InlineData(null)]
    public void HourlyDelivery_InjectionsBesidePumpTemps_KeepBoth(string? device)
    {
        var (temps, injection, start, end) = PumpDayWithAnInjection(device);

        var result = _sut.CalculateHourlyInsulinDelivery(
            temps, [], [], start, end, Mills(end), TimeZoneInfo.Utc, [injection]);

        result.Hours.Sum(h => h.TempBasal).Should().BeApproximately(24, 1e-9);
        result.Hours.Sum(h => h.ScheduledBasal).Should().BeApproximately(11, 1e-9,
            "the dose runs 24 hours from 02:00, 22 of them in the window");
        result.BasalPeriods.DailyTotal.Should().Be(35);
    }

    [Theory]
    [InlineData("pump")]
    [InlineData(null)]
    public void BasalAnalysis_InjectionsBesidePumpTemps_KeepBoth(string? device)
    {
        var (temps, injection, start, end) = PumpDayWithAnInjection(device);

        var result = _sut.CalculateBasalAnalysis(temps, start, end, Mills(end), TimeZoneInfo.Utc, [injection]);

        result.Stats.TotalDelivered.Should().Be(35);
        result.Stats.AvgRate.Should().Be(Math.Round(35 / 24.0 * 100) / 100);
        result.Stats.MinRate.Should().Be(1.0);
        result.Stats.MaxRate.Should().Be(1.5, "the pump's 1.0 U/h and the dose's 0.5 U/h run at once");
        result.HourlyPercentiles[1].Should().Match<HourlyBasalPercentileData>(p => p.P10 == 1.0 && p.Median == 1.0 && p.P90 == 1.0);
        result.HourlyPercentiles[5].Should().Match<HourlyBasalPercentileData>(p => p.P10 == 1.5 && p.Median == 1.5 && p.P90 == 1.5);
        result.TempBasalInfo.Total.Should().Be(24, "an injection does not cut the pump's temps short");
    }

    private static (List<TempBasal> Temps, BasalInjection Injection, DateTime Start, DateTime End) PumpDayWithAnInjection(string? device)
    {
        var start = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        var temps = Enumerable.Range(0, 24)
            .Select(h => new TempBasal
            {
                StartTimestamp = start.AddHours(h),
                EndTimestamp = start.AddHours(h + 1),
                Rate = 1.0,
                ScheduledRate = 0.8,
                Origin = TempBasalOrigin.Algorithm,
                Device = device,
            })
            .ToList();
        return (temps, Dose(start.AddHours(2), 12, 0), start, start.AddDays(1));
    }

    private static List<BasalInjection> DailyDoses(double dia) =>
        Enumerable.Range(0, 31)
            .Select(d => Dose(BerlinLocalToUtc(new DateTime(2026, 10, 15, 21, 0, 0).AddDays(d)), 18, dia))
            .ToList();

    private static BasalInjection Dose(DateTime timestampUtc, double units, double dia) => new()
    {
        Timestamp = timestampUtc,
        Units = units,
        InsulinContext = dia > 0 ? new TreatmentInsulinContext { Dia = dia } : null,
    };

    private static DateTime BerlinUtc(int year, int month, int day, int hour) =>
        BerlinLocalToUtc(new DateTime(year, month, day, hour, 0, 0));

    private static DateTime BerlinLocalToUtc(DateTime local) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Berlin);
}
