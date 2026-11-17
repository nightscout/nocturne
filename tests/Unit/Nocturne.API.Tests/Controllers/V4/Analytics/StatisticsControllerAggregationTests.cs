using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Analytics;

public partial class StatisticsControllerTests
{
    private static readonly DateTime June1 = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private async Task<PunchCardResponse> PunchCard(DateOnly from, DateOnly to)
    {
        var result = await CreateController(statisticsService: new StatisticsService(TimeProvider.System)).GetPunchCardData(from, to);
        return result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<PunchCardResponse>().Subject;
    }

    [Fact]
    public async Task GetRangeAnalytics_ListsEachCgmThatReadAndTheUnattributedReadings()
    {
        var read = new PatientDevice { Id = Guid.NewGuid(), DeviceCategory = DeviceCategory.CGM, Model = "Reader" };
        var silent = new PatientDevice { Id = Guid.NewGuid(), DeviceCategory = DeviceCategory.CGM, Model = "Silent" };
        var pump = new PatientDevice { Id = Guid.NewGuid(), DeviceCategory = DeviceCategory.InsulinPump, Model = "Pump" };
        _patientDeviceRepoMock
            .Setup(r => r.GetByDateRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([read, silent, pump]);
        SetupGlucose(
        [
            new SensorGlucose { Timestamp = June1.AddMinutes(5), Mgdl = 100, PatientDeviceId = read.Id },
            new SensorGlucose { Timestamp = June1.AddMinutes(10), Mgdl = 110, PatientDeviceId = read.Id },
            new SensorGlucose { Timestamp = June1.AddMinutes(15), Mgdl = 120, PatientDeviceId = read.Id },
            new SensorGlucose { Timestamp = June1.AddMinutes(20), Mgdl = 130 },
            new SensorGlucose { Timestamp = June1.AddMinutes(25), Mgdl = 140 },
            new SensorGlucose { Timestamp = June1.AddMinutes(30), Mgdl = 150, PatientDeviceId = pump.Id },
        ]);
        SetupEmptyTreatments();

        var result = await CreateController(statisticsService: new StatisticsService(TimeProvider.System))
            .GetRangeAnalytics(June1, June1.AddDays(1));

        var payload = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<ReportAnalysisResult>().Subject;
        payload.ContributingDevices.Should().BeEquivalentTo(
            new[]
            {
                new ContributingDevice { PatientDeviceId = read.Id, Name = "Reader", ReadingCount = 3 },
                new ContributingDevice { PatientDeviceId = null, Name = null, ReadingCount = 2 },
            },
            options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task GetRangeAnalytics_ListsNoDevicesWhenEveryReadingIsAttributedToANonCgm()
    {
        var pump = new PatientDevice { Id = Guid.NewGuid(), DeviceCategory = DeviceCategory.InsulinPump };
        _patientDeviceRepoMock
            .Setup(r => r.GetByDateRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([pump]);
        SetupGlucose([new SensorGlucose { Timestamp = June1.AddMinutes(5), Mgdl = 100, PatientDeviceId = pump.Id }]);
        SetupEmptyTreatments();

        var result = await CreateController(statisticsService: new StatisticsService(TimeProvider.System))
            .GetRangeAnalytics(June1, June1.AddDays(1));

        result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<ReportAnalysisResult>().Subject
            .ContributingDevices.Should().BeEmpty();
    }

    [Fact]
    public async Task GetMultiPeriodStatistics_LeavesAPeriodWithFewerThanTenReadingsWithoutFigures()
    {
        var now = DefaultNow.UtcDateTime;
        SetupGlucose(Enumerable.Range(1, 9)
            .Select(i => new SensorGlucose { Timestamp = now.AddMinutes(-5 * i), Mgdl = 120 })
            .ToList());
        SetupEmptyTreatments();
        _carbIntakeRepoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([new CarbIntake { Timestamp = now.AddMinutes(-10), Carbs = 20 }]);

        var result = await CreateController(statisticsService: new StatisticsService(TimeProvider.System)).GetMultiPeriodStatistics();

        var day = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<MultiPeriodStatistics>().Subject.LastDay!;
        day.HasSufficientData.Should().BeFalse();
        day.Analytics.Should().BeNull();
        day.TreatmentSummary.Should().BeNull();
        day.InsulinDelivery.Should().BeNull();
        day.Gmi.Should().BeNull();
        day.Reliability.Should().BeNull();
        day.EntryCount.Should().Be(9);
        day.TreatmentCount.Should().Be(1);
    }

    [Fact]
    public async Task GetMultiPeriodStatistics_DerivesEachPeriodsGmiFromItsMeanAndCarriesItsReliability()
    {
        SetupMultiPeriodWithProfile([]);

        var result = await CreateController(statisticsService: new StatisticsService(TimeProvider.System)).GetMultiPeriodStatistics();

        var day = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<MultiPeriodStatistics>().Subject.LastDay!;
        day.HasSufficientData.Should().BeTrue();
        day.Gmi!.Value.Should().Be(new StatisticsService(TimeProvider.System).CalculateGMI(120).Value);
        day.Gmi.Reliability.Should().BeSameAs(day.Reliability);
        day.Reliability.Should().BeSameAs(day.Analytics!.Reliability);
    }

    [Fact]
    public async Task GetPunchCardData_ReportsAnEmptyDayAsZeroesAndLeavesAMonthWithNoReadingsWithoutSummary()
    {
        SetupEmptyTreatments();
        SetupPunchCardGlucose(
        [
            new SensorGlucose { Timestamp = June1.AddHours(1), Mgdl = 100 },
            new SensorGlucose { Timestamp = June1.AddHours(2), Mgdl = 100 },
        ]);

        var payload = await PunchCard(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 2));

        var month = payload.Months.Should().ContainSingle().Subject;
        var empty = month.Days.Single(d => d.Date == "2026-06-02");
        empty.TotalReadings.Should().Be(0);
        empty.InRangeCount.Should().Be(0);
        empty.InRangePercent.Should().Be(0);
        empty.AverageGlucose.Should().Be(0);
        empty.TotalInsulin.Should().Be(0);
        empty.Entries.Should().BeEmpty();
        month.Summary!.DayCount.Should().Be(1);
        month.Summary.TotalReadings.Should().Be(2);
    }

    [Fact]
    public async Task GetPunchCardData_LeavesTheSummaryNullAndTheGlobalMaximaZeroWhenNothingWasRead()
    {
        SetupEmptyTreatments();
        SetupPunchCardGlucose([]);

        var payload = await PunchCard(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 3));

        payload.Months.Should().ContainSingle().Which.Summary.Should().BeNull();
        payload.GlobalMaxCarbs.Should().Be(0);
        payload.GlobalMaxInsulin.Should().Be(0);
        payload.GlobalMaxCarbInsulinDiff.Should().Be(0);
    }

    [Fact]
    public async Task GetPunchCardData_ReportsADayOfOneReadingAsEntirelyInItsBand()
    {
        SetupEmptyTreatments();
        SetupPunchCardGlucose([new SensorGlucose { Timestamp = June1.AddHours(3), Mgdl = 100 }]);

        var day = (await PunchCard(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 1)))
            .Months.Single().Days.Single();

        day.TotalReadings.Should().Be(1);
        day.InRangeCount.Should().Be(1);
        day.InRangePercent.Should().Be(100);
        day.LowCount.Should().Be(0);
        day.AverageGlucose.Should().Be(100);
    }

    [Fact]
    public async Task GetPunchCardData_RoundsEachBandsCountFromItsPercentageOfTheReadings()
    {
        SetupEmptyTreatments();
        double[] values = [50, 65, 100, 100, 150, 200, 300];
        SetupPunchCardGlucose(values
            .Select((v, i) => new SensorGlucose { Timestamp = June1.AddMinutes(5 * i), Mgdl = v })
            .ToArray());

        var day = (await PunchCard(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 1)))
            .Months.Single().Days.Single();

        day.TotalReadings.Should().Be(7);
        day.AverageGlucose.Should().BeApproximately(values.Average(), 1e-9);
        day.InRangePercent.Should().BeApproximately(300.0 / 7, 1e-9);
        day.VeryLowPercent.Should().BeApproximately(100.0 / 7, 1e-9);
        (day.VeryLowCount, day.LowCount, day.InRangeCount, day.HighCount, day.VeryHighCount)
            .Should().Be((1, 1, 3, 1, 1));
    }

    [Fact]
    public async Task GetPunchCardData_SplitsMonthsAtTheTenantsLocalMidnightAndScalesEachByItsOwnMaxima()
    {
        _therapySettingsResolverMock
            .Setup(r => r.GetTimezoneAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync("Australia/Sydney");
        var sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");
        var lastOfSeptember = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 9, 30, 23, 59, 0), sydney);
        var firstOfOctober = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 10, 1, 0, 0, 0), sydney);
        SetupEmptyTreatments();
        SetupPunchCardGlucose(
        [
            new SensorGlucose { Timestamp = lastOfSeptember, Mgdl = 100 },
            new SensorGlucose { Timestamp = firstOfOctober, Mgdl = 200 },
        ]);
        SetupBoluses(BolusKind.Manual,
        [
            new Bolus { Timestamp = lastOfSeptember, Insulin = 3 },
            new Bolus { Timestamp = firstOfOctober, Insulin = 8 },
        ]);
        _carbIntakeRepoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new CarbIntake { Timestamp = lastOfSeptember, Carbs = 30 },
                new CarbIntake { Timestamp = firstOfOctober, Carbs = 10 },
            ]);

        var payload = await PunchCard(new DateOnly(2026, 9, 30), new DateOnly(2026, 10, 1));

        payload.Months.Select(m => (m.Year, m.Month, m.MonthName))
            .Should().Equal((2026, 8, "September"), (2026, 9, "October"));
        var september = payload.Months[0];
        var october = payload.Months[1];
        september.Days.Single().Entries.Select(e => e.Mgdl).Should().Equal(100);
        october.Days.Single().Entries.Select(e => e.Mgdl).Should().Equal(200);
        september.MaxCarbs.Should().Be(30);
        october.MaxCarbs.Should().Be(10);
        september.MaxInsulin.Should().Be(3);
        october.MaxInsulin.Should().Be(8);
        payload.GlobalMaxCarbs.Should().Be(30);
        payload.GlobalMaxInsulin.Should().Be(8);
        payload.GlobalMaxCarbInsulinDiff.Should().Be(10, "30 g over 3 U is the larger of 10 and 1.2");
    }
}
