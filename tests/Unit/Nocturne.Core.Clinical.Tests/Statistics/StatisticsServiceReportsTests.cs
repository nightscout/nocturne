using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Models.Basal;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Clinical.Tests.Statistics;

public class StatisticsServiceReportsTests
{
    private readonly StatisticsService _service = new();
    private static readonly DateTime June1 = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static SensorGlucose Reading(DateTime at, double mgdl, Guid? device = null) =>
        new() { Timestamp = at, Mgdl = mgdl, PatientDeviceId = device };

    private PunchCardResponse PunchCard(
        IEnumerable<SensorGlucose> glucose,
        DateOnly from,
        DateOnly to,
        TimeZoneInfo? zone = null,
        DateTimeOffset? now = null,
        IEnumerable<Bolus>? boluses = null,
        IEnumerable<CarbIntake>? carbs = null) =>
        _service.CalculatePunchCard(
            glucose, boluses ?? [], [], carbs ?? [], [], [],
            from, to, zone ?? TimeZoneInfo.Utc, now ?? Now);

    [Fact]
    public void Buckets_PutARecordInEveryWindowContainingIt()
    {
        PunchCardDayWindow[] windows = [new(0, 99), new(60, 159), new(100, 199)];
        long[] records = [10, 70, 99, 100, 120, 160, 250];

        var buckets = PunchCardDayBuckets.Bucket(records, windows, r => r);

        buckets.Select(b => b.ToArray()).Should().BeEquivalentTo(
            windows.Select(w => records.Where(r => r >= w.StartMs && r <= w.EndMs).ToArray()),
            options => options.WithStrictOrdering());
    }

    [Fact]
    public void Buckets_HandleWindowsThatAreNotAscending()
    {
        PunchCardDayWindow[] windows = [new(100, 199), new(0, 149), new(120, 130), new(300, 399)];
        long[] records = [5, 100, 125, 149, 150, 199, 200, 350, 400];

        var buckets = PunchCardDayBuckets.Bucket(records, windows, r => r);

        buckets.Select(b => b.ToArray()).Should().BeEquivalentTo(
            windows.Select(w => records.Where(r => r >= w.StartMs && r <= w.EndMs).ToArray()),
            options => options.WithStrictOrdering());
    }

    [Fact]
    public void ContributingDevices_CountsEachCgmsReadingsAndTheUnattributedOnes()
    {
        var a = new PatientDevice { Id = Guid.NewGuid(), DeviceCategory = DeviceCategory.CGM, Model = "A" };
        var idle = new PatientDevice { Id = Guid.NewGuid(), DeviceCategory = DeviceCategory.CGM, Model = "Idle" };
        var pump = new PatientDevice { Id = Guid.NewGuid(), DeviceCategory = DeviceCategory.InsulinPump, Model = "P" };

        var result = _service.CalculateContributingDevices(
            [Reading(June1, 100, a.Id), Reading(June1, 100, a.Id), Reading(June1, 100), Reading(June1, 100, pump.Id)],
            [a, idle, pump]);

        result.Select(d => (d.PatientDeviceId, d.Name, d.ReadingCount))
            .Should().Equal((a.Id, (string?)"A", 2), (null, (string?)null, 1));
    }

    [Fact]
    public void ContributingDevices_IsEmptyWithoutReadings()
    {
        _service.CalculateContributingDevices([], [new PatientDevice { Id = Guid.NewGuid(), DeviceCategory = DeviceCategory.CGM }])
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    public void HasSufficientPeriodData_NeedsTenReadings(int count, bool expected) =>
        StatisticsService.HasSufficientPeriodData(count).Should().Be(expected);

    private PeriodStatistics Period(
        int readings,
        IReadOnlyCollection<Bolus>? boluses = null,
        IReadOnlyCollection<CarbIntake>? carbs = null) =>
        _service.CalculatePeriodStatistics(
            1, June1, June1.AddDays(1),
            Enumerable.Range(0, readings).Select(i => Reading(June1.AddMinutes(5 * i), 120)).ToList(),
            boluses ?? [], [], [], [], carbs ?? [], [],
            new DateTimeOffset(June1.AddDays(1), TimeSpan.Zero).ToUnixTimeMilliseconds(), TimeZoneInfo.Utc);

    [Fact]
    public void PeriodStatistics_BelowTheReadingMinimumCarriesOnlyItsCounts()
    {
        var period = Period(
            9,
            [new Bolus { Timestamp = June1, Insulin = 1 }],
            [new CarbIntake { Timestamp = June1, Carbs = 10 }]);

        period.HasSufficientData.Should().BeFalse();
        period.EntryCount.Should().Be(9);
        period.TreatmentCount.Should().Be(2);
        period.Analytics.Should().BeNull();
        period.Gmi.Should().BeNull();
        period.InsulinDelivery.Should().BeNull();
        period.PeriodDays.Should().Be(1);
    }

    [Fact]
    public void PeriodStatistics_AtTheMinimumDerivesGmiFromTheMeanAndSharesItsReliability()
    {
        var period = Period(10);

        period.HasSufficientData.Should().BeTrue();
        period.Gmi!.Value.Should().Be(_service.CalculateGMI(120).Value);
        period.Reliability.Should().BeSameAs(period.Analytics!.Reliability);
        period.Gmi.Reliability.Should().BeSameAs(period.Reliability);
        period.TreatmentSummary.Should().NotBeNull();
        period.InsulinDelivery.Should().NotBeNull();
    }

    [Fact]
    public void PunchCardRange_EndsOneTickBeforeTheNextLocalMidnight()
    {
        var sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");

        var (start, end) = StatisticsService.PunchCardRange(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), sydney);

        start.Should().Be(new DateTime(2026, 8, 31, 14, 0, 0, DateTimeKind.Utc));
        end.Should().Be(new DateTime(2026, 9, 30, 14, 0, 0, DateTimeKind.Utc).AddTicks(-1));
    }

    [Theory]
    [InlineData(2026, 10, 25, 1792879200000L, 1792969200000L)]
    [InlineData(2026, 3, 29, 1774738800000L, 1774821600000L)]
    [InlineData(2026, 6, 10, 1781042400000L, 1781128800000L)]
    public void PunchCard_BoundsEachDayByItsOwnLocalMidnights(int year, int month, int day, long start, long end)
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var date = new DateOnly(year, month, day);

        var punchCardDay = PunchCard([], date, date, berlin).Months.Single().Days.Single();

        punchCardDay.Timestamp.Should().Be(start);
        punchCardDay.EndTimestamp.Should().Be(end);
    }

    [Fact]
    public void PunchCard_TakesDayBoundsFromTheTenantZoneNotTheHostZone()
    {
        var auckland = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland");
        var date = new DateOnly(2026, 6, 10);

        var punchCardDay = PunchCard([], date, date, auckland).Months.Single().Days.Single();

        punchCardDay.Timestamp.Should().Be(new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.FromHours(12)).ToUnixTimeMilliseconds());
        punchCardDay.EndTimestamp.Should().Be(new DateTimeOffset(2026, 6, 11, 0, 0, 0, TimeSpan.FromHours(12)).ToUnixTimeMilliseconds());
    }

    [Fact]
    public void PunchCard_ReportsAnEmptyDayAsZeroesAndAMonthWithoutReadingsWithoutSummary()
    {
        var withData = PunchCard([Reading(June1.AddHours(1), 100)], new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 2));
        var empty = withData.Months.Single().Days.Single(d => d.Date == "2026-06-02");

        empty.TotalReadings.Should().Be(0);
        empty.AverageGlucose.Should().Be(0);
        empty.InRangePercent.Should().Be(0);
        withData.Months.Single().Summary!.DayCount.Should().Be(1);

        var none = PunchCard([], new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 2));
        none.Months.Single().Summary.Should().BeNull();
        none.GlobalMaxInsulin.Should().Be(0);
    }

    [Fact]
    public void PunchCard_DerivesEachBandsCountFromItsPercentage()
    {
        double[] values = [50, 65, 100, 100, 150, 200, 300];
        var readings = values.Select((v, i) => Reading(June1.AddMinutes(5 * i), v)).ToList();
        readings.Add(Reading(June1.AddDays(1).AddHours(1), 100));

        var month = PunchCard(readings, new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 2)).Months.Single();

        var day = month.Days.First();
        (day.VeryLowCount, day.LowCount, day.InRangeCount, day.HighCount, day.VeryHighCount)
            .Should().Be((1, 1, 3, 1, 1));
        month.Summary!.TotalReadings.Should().Be(8);
        month.Summary.InRangePercent.Should().BeApproximately(4.0 / 8 * 100, 1e-9);
        month.Summary.AvgGlucose.Should().BeApproximately((values.Sum() + 100) / 8, 1e-9);
    }

    [Fact]
    public void PunchCard_WeighsTheMonthByCoveredTimeAcrossASensorSwap()
    {
        // A one-minute sensor with a 100-minute low on the first day, a five-minute sensor in range
        // on the second.
        var oneMinute = Enumerable.Range(0, 1440)
            .Select(m => Reading(June1.AddMinutes(m), m is >= 180 and < 280 ? 60 : 120));
        var fiveMinute = Enumerable.Range(0, 288)
            .Select(i => Reading(June1.AddDays(1).AddMinutes(i * 5), 120));

        var summary = PunchCard(
            oneMinute.Concat(fiveMinute), new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 2))
            .Months.Single().Summary!;

        summary.LowPercent.Should().BeApproximately(100.0 / 2880 * 100, 0.01);
        summary.InRangePercent.Should().BeApproximately(100 - 100.0 / 2880 * 100, 0.01);
    }

    [Fact]
    public void PunchCard_AveragesTddOverTheDaysSoFarOfTheCurrentMonth()
    {
        var bolus = new Bolus { Timestamp = June1.AddHours(12), Insulin = 12 };
        var readings = Enumerable.Range(1, 10).Select(d => Reading(June1.AddDays(d - 1).AddHours(1), 100)).ToList();

        var summary = PunchCard(
            readings, new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30),
            now: new DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero),
            boluses: [bolus]).Months.Single().Summary!;

        summary.Tdd.Should().BeApproximately(12.0 / 10, 1e-9);
    }

    [Fact]
    public void PunchCard_SplitsMonthsAtLocalMidnightAndTakesGlobalMaximaAcrossThem()
    {
        var sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");
        var lastOfSeptember = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 9, 30, 23, 59, 0), sydney);
        var firstOfOctober = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 10, 1, 0, 0, 0), sydney);

        var response = PunchCard(
            [Reading(lastOfSeptember, 100), Reading(firstOfOctober, 200)],
            new DateOnly(2026, 9, 30), new DateOnly(2026, 10, 1), sydney,
            carbs:
            [
                new CarbIntake { Timestamp = lastOfSeptember, Carbs = 30 },
                new CarbIntake { Timestamp = firstOfOctober, Carbs = 10 },
            ]);

        response.Months.Select(m => m.MonthName).Should().Equal("September", "October");
        response.Months.Select(m => m.MaxCarbs).Should().Equal(30, 10);
        response.GlobalMaxCarbs.Should().Be(30);
        response.DateRange.From.Should().Be(new DateTime(2026, 9, 29, 14, 0, 0, DateTimeKind.Utc).ToString("o"));
    }

    [Fact]
    public void PunchCard_StopsWhenCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => _service.CalculatePunchCard(
            [], [], [], [], [], [], new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 2),
            TimeZoneInfo.Utc, Now, cts.Token);

        act.Should().Throw<OperationCanceledException>();
    }
}
