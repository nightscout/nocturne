using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Analytics;

/// <summary>
/// The reports that group by local day or hour take their zone from <see cref="ITenantClockResolver"/>
/// and bin nothing without one. Each record sits at 07:00 on Monday 2 March in Sydney and at 20:00 on
/// Sunday 1 March in UTC, so a fallback to UTC changes the weekday, the day and the hour.
/// </summary>
public partial class StatisticsControllerTests
{
    private static readonly DateTime SydneyMondayMorning = new(2026, 3, 1, 20, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowStart = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowEnd = new(2026, 3, 4, 0, 0, 0, DateTimeKind.Utc);

    public static TheoryData<string?, bool, TimeZoneUnavailableReason> UnavailableZones => new()
    {
        { null, false, TimeZoneUnavailableReason.NotConfigured },
        { "  ", false, TimeZoneUnavailableReason.NotConfigured },
        { "Not/AZone", false, TimeZoneUnavailableReason.Unrecognised },
        { null, true, TimeZoneUnavailableReason.Share },
        { "Not/AZone", true, TimeZoneUnavailableReason.Share },
    };

    private StatisticsController ControllerOnClock(string? timezoneId, bool share)
    {
        _therapySettingsResolverMock
            .Setup(r => r.GetTimezoneAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(timezoneId);
        var category = new CategoryReadContext();
        if (share)
            category.MarkShare();
        return CreateController(statisticsService: new StatisticsService(), categoryReadContext: category);
    }

    private void SetupInsulinAtSydneyMorning()
    {
        SetupEmptyTreatments();
        _bolusRepoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<bool>(), It.IsAny<BolusKind?>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime? _, DateTime? _, string? _, string? _, int _, int _, bool _, bool _, BolusKind? kind,
                DateTime? _, Guid? _, CancellationToken _) =>
                kind == BolusKind.Manual
                    ? new List<Bolus> { new() { Timestamp = SydneyMondayMorning, Insulin = 4 } }
                    : new List<Bolus>());
        _tempBasalRepoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TempBasal>
            {
                new()
                {
                    StartTimestamp = SydneyMondayMorning,
                    EndTimestamp = SydneyMondayMorning.AddMinutes(30),
                    Rate = 1.2,
                    ScheduledRate = 1,
                    Origin = TempBasalOrigin.Algorithm,
                },
            });
    }

    [Theory]
    [InlineData("Australia/Sydney", 1)]
    [InlineData(null, 2)]
    public async Task AssessDataSufficiency_CountsDaysOnTheTenantZone(string? timezoneId, int expectedDays)
    {
        var request = new DataSufficiencyRequest
        {
            Days = 1,
            Entries = new[]
            {
                new DateTime(2026, 1, 14, 14, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc),
            }.Select(t => new SensorGlucose { Timestamp = t, Mgdl = 100 }).ToList(),
        };

        var result = await ControllerOnClock(timezoneId, share: false).AssessDataSufficiency(request);

        result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<DataSufficiencyAssessment>().Subject
            .DaysWithData.Should().Be(expectedDays);
    }

    [Theory]
    [MemberData(nameof(UnavailableZones))]
    public async Task AssessDataSufficiency_WithoutAZone_CountsUtcDaysAndSaysWhy(
        string? timezoneId, bool share, TimeZoneUnavailableReason expected)
    {
        var request = new DataSufficiencyRequest
        {
            Days = 1,
            Entries = [new SensorGlucose { Timestamp = SydneyMondayMorning, Mgdl = 100 }],
        };

        var result = await ControllerOnClock(timezoneId, share).AssessDataSufficiency(request);

        var payload = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<DataSufficiencyAssessment>().Subject;
        payload.DaysWithData.Should().Be(1);
        payload.TimeZoneUnavailableReason.Should().Be(expected);
    }

    [Fact]
    public async Task AssessDataSufficiency_OnATenantZone_CarriesNoReason()
    {
        var request = new DataSufficiencyRequest
        {
            Days = 1,
            Entries = [new SensorGlucose { Timestamp = SydneyMondayMorning, Mgdl = 100 }],
        };

        var result = await ControllerOnClock("Australia/Sydney", share: false).AssessDataSufficiency(request);

        result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<DataSufficiencyAssessment>().Subject
            .TimeZoneUnavailableReason.Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(UnavailableZones))]
    public async Task CalculateGlycemicVariability_WithoutAZone_WithholdsTheAverageDailyRiskRangeAndSaysWhy(
        string? timezoneId, bool share, TimeZoneUnavailableReason expected)
    {
        var request = new GlycemicVariabilityRequest
        {
            Values = [100, 140, 90, 180],
            Entries = new[] { 100, 140, 90, 180 }
                .Select((mgdl, i) => new SensorGlucose { Timestamp = SydneyMondayMorning.AddMinutes(5 * i), Mgdl = mgdl })
                .ToList(),
        };

        var result = await ControllerOnClock(timezoneId, share).CalculateGlycemicVariability(request);

        var payload = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<GlycemicVariability>().Subject;
        payload.AverageDailyRiskRange.Should().BeNull();
        payload.TimeZoneUnavailableReason.Should().Be(expected);
    }

    [Fact]
    public async Task CalculateGlycemicVariability_OnATenantZone_ServesTheAverageDailyRiskRange()
    {
        var request = new GlycemicVariabilityRequest
        {
            Values = [100, 140, 90, 180],
            Entries = new[] { 100, 140, 90, 180 }
                .Select((mgdl, i) => new SensorGlucose { Timestamp = SydneyMondayMorning.AddMinutes(5 * i), Mgdl = mgdl })
                .ToList(),
        };

        var result = await ControllerOnClock("Australia/Sydney", share: false).CalculateGlycemicVariability(request);

        var payload = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<GlycemicVariability>().Subject;
        payload.AverageDailyRiskRange.Should().BeGreaterThan(0);
        payload.TimeZoneUnavailableReason.Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(UnavailableZones))]
    public async Task AnalyzeGlucoseDataExtended_WithoutAZone_SaysWhyOnTheAnalyticsItself(
        string? timezoneId, bool share, TimeZoneUnavailableReason expected)
    {
        var readings = Enumerable.Range(0, 6)
            .Select(i => new SensorGlucose { Timestamp = SydneyMondayMorning.AddMinutes(5 * i), Mgdl = 100 + i * 10 })
            .ToList();

        var result = await ControllerOnClock(timezoneId, share)
            .AnalyzeGlucoseDataExtended(new ExtendedGlucoseAnalyticsRequest { Entries = readings });

        var payload = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<ExtendedGlucoseAnalytics>().Subject;
        payload.GlycemicVariability!.AverageDailyRiskRange.Should().BeNull();
        payload.TimeZoneUnavailableReason.Should().Be(expected);
        payload.GlycemicVariability.TimeZoneUnavailableReason.Should().Be(expected);
        payload.DataSufficiency.TimeZoneUnavailableReason.Should().Be(expected);
    }

    [Theory]
    [MemberData(nameof(UnavailableZones))]
    public async Task AnalyzeGlucoseData_WithoutAZone_SaysWhyOnTheAnalytics(
        string? timezoneId, bool share, TimeZoneUnavailableReason expected)
    {
        var readings = Enumerable.Range(0, 6)
            .Select(i => new SensorGlucose { Timestamp = SydneyMondayMorning.AddMinutes(5 * i), Mgdl = 100 + i * 10 })
            .ToList();

        var result = await ControllerOnClock(timezoneId, share)
            .AnalyzeGlucoseData(new GlucoseAnalyticsRequest { Entries = readings });

        var payload = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<GlucoseAnalytics>().Subject;
        payload.GlycemicVariability!.AverageDailyRiskRange.Should().BeNull();
        payload.TimeZoneUnavailableReason.Should().Be(expected);
        payload.GlycemicVariability.TimeZoneUnavailableReason.Should().Be(expected);
    }

    [Theory]
    [MemberData(nameof(UnavailableZones))]
    public async Task GetMultiPeriodStatistics_WithoutAZone_SaysWhyOnEachPeriodsAnalytics(
        string? timezoneId, bool share, TimeZoneUnavailableReason expected)
    {
        var now = DefaultNow.UtcDateTime;
        SetupGlucose(Enumerable.Range(1, 12)
            .Select(i => new SensorGlucose { Timestamp = now.AddMinutes(-5 * i), Mgdl = 100 + i * 5 })
            .ToList());
        SetupEmptyTreatments();

        var result = await ControllerOnClock(timezoneId, share).GetMultiPeriodStatistics();

        var data = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<MultiPeriodStatistics>().Subject;
        PeriodAnalytics(data).Should().OnlyContain(a =>
            a.TimeZoneUnavailableReason == expected
            && a.GlycemicVariability!.AverageDailyRiskRange == null
            && a.GlycemicVariability.TimeZoneUnavailableReason == expected);
    }

    private static IEnumerable<GlucoseAnalytics> PeriodAnalytics(MultiPeriodStatistics data) =>
        new[] { data.LastDay, data.Last3Days, data.LastWeek, data.LastMonth, data.Last90Days }
            .Select(period => period.Analytics!);

    [Theory]
    [MemberData(nameof(UnavailableZones))]
    public async Task GetRangeAnalytics_WithoutAZone_WithholdsTheAverageDailyRiskRangeAndSaysWhy(
        string? timezoneId, bool share, TimeZoneUnavailableReason expected)
    {
        SetupGlucose(Enumerable.Range(0, 6)
            .Select(i => new SensorGlucose { Timestamp = SydneyMondayMorning.AddMinutes(5 * i), Mgdl = 100 + i * 10 })
            .ToList());
        SetupEmptyTreatments();

        var result = await ControllerOnClock(timezoneId, share).GetRangeAnalytics(WindowStart, WindowEnd);

        var payload = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<ReportAnalysisResult>().Subject;
        payload.TimeZoneUnavailableReason.Should().Be(expected);
        payload.Analysis.GlycemicVariability!.AverageDailyRiskRange.Should().BeNull();
        payload.Analysis.TimeZoneUnavailableReason.Should().Be(expected);
        payload.Analysis.GlycemicVariability.TimeZoneUnavailableReason.Should().Be(expected);
        payload.Analysis.DataSufficiency.TimeZoneUnavailableReason.Should().Be(expected);
    }

    [Fact]
    public async Task GetRangeAnalytics_CountsTotalDaysAndDaysWithDataOnTheSamePatientDays()
    {
        SetupGlucose(new[]
            {
                new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 3, 2, 4, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 3, 3, 4, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 3, 3, 20, 0, 0, DateTimeKind.Utc),
            }
            .Select(at => new SensorGlucose { Timestamp = at, Mgdl = 110 })
            .ToList());
        SetupEmptyTreatments();

        var result = await ControllerOnClock("Australia/Sydney", share: false).GetRangeAnalytics(WindowStart, WindowEnd);

        var sufficiency = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<ReportAnalysisResult>().Subject.Analysis.DataSufficiency;
        sufficiency.TotalDays.Should().Be(4);
        sufficiency.DaysWithData.Should().Be(4);
    }

    [Fact]
    public async Task GetRangeAnalytics_FromTheLongDaylightSavingDay_CountsEachPatientDayTheWindowTouches()
    {
        var sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");
        var firstDay = new DateOnly(2026, 4, 5);
        var start = DateTimeOffset.FromUnixTimeMilliseconds(LocalSlotBinning.DayStartMills(firstDay, sydney)).UtcDateTime;
        var end = DateTimeOffset.FromUnixTimeMilliseconds(
            LocalSlotBinning.DayStartMills(firstDay.AddDays(3), sydney) + 6 * LocalSlotBinning.HourMs).UtcDateTime;
        SetupGlucose(Enumerable.Range(0, 4)
            .Select(i => new SensorGlucose
            {
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(
                    LocalSlotBinning.DayStartMills(firstDay.AddDays(i), sydney) + 30 * LocalSlotBinning.MinuteMs).UtcDateTime,
                Mgdl = 110,
            })
            .ToList());
        SetupEmptyTreatments();

        var result = await ControllerOnClock("Australia/Sydney", share: false).GetRangeAnalytics(start, end);

        var sufficiency = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<ReportAnalysisResult>().Subject.Analysis.DataSufficiency;
        sufficiency.TotalDays.Should().Be(4);
        sufficiency.DaysWithData.Should().Be(4);
    }

    [Fact]
    public async Task GetRangeAnalytics_ARolling24HourWindow_KeepsPerDayFiguresOnItsDuration()
    {
        var start = new DateTime(2026, 3, 2, 4, 0, 0, DateTimeKind.Utc);
        var end = start.AddHours(24);
        SetupGlucose(Enumerable.Range(0, 144)
            .Select(i => new SensorGlucose { Timestamp = start.AddMinutes(10 * i), Mgdl = 110 })
            .ToList());
        SetupEmptyTreatments();
        _carbIntakeRepoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CarbIntake> { new() { Timestamp = start.AddHours(1), Carbs = 60 } });

        var result = await ControllerOnClock("Australia/Sydney", share: false).GetRangeAnalytics(start, end);

        var analysis = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<ReportAnalysisResult>().Subject.Analysis;
        analysis.DataSufficiency.TotalDays.Should().Be(2);
        analysis.DataSufficiency.DaysWithData.Should().Be(2);
        analysis.DataSufficiency.ExpectedReadings.Should().Be(288);
        analysis.DataSufficiency.CompletenessPercentage.Should().Be(50);
        analysis.TreatmentSummary!.DailyCarbs.Should().Be(60);
    }

    [Fact]
    public async Task GetWeekdayAverages_BucketsOnTheTenantZone()
    {
        SetupGlucose([new SensorGlucose { Timestamp = SydneyMondayMorning, Mgdl = 100 }]);

        var result = await ControllerOnClock("Australia/Sydney", share: false)
            .GetWeekdayAverages(WindowStart, WindowEnd);

        var payload = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<WeekdayAveragesResponse>().Subject;
        payload.TimeZoneUnavailableReason.Should().BeNull();
        var slot = payload.Slots.Should().ContainSingle().Subject;
        slot.MinuteOfDay.Should().Be(7 * 60);
        slot.Mean.Keys.Should().Equal(DayOfWeek.Monday);
    }

    [Theory]
    [MemberData(nameof(UnavailableZones))]
    public async Task GetWeekdayAverages_WithoutAZone_BinsNothingAndSaysWhy(
        string? timezoneId, bool share, TimeZoneUnavailableReason expected)
    {
        SetupGlucose([new SensorGlucose { Timestamp = SydneyMondayMorning, Mgdl = 100 }]);

        var result = await ControllerOnClock(timezoneId, share).GetWeekdayAverages(WindowStart, WindowEnd);

        var payload = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<WeekdayAveragesResponse>().Subject;
        payload.Slots.Should().BeEmpty();
        payload.TimeZoneUnavailableReason.Should().Be(expected);
        _glucoseRepoMock.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task GetWeekdayAverages_WhenTheTimezoneLookupFails_BinsNothingAndSaysSo()
    {
        SetupGlucose([new SensorGlucose { Timestamp = SydneyMondayMorning, Mgdl = 100 }]);
        _therapySettingsResolverMock
            .Setup(r => r.GetTimezoneAsync(null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var result = await CreateController(statisticsService: new StatisticsService())
            .GetWeekdayAverages(WindowStart, WindowEnd);

        var payload = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<WeekdayAveragesResponse>().Subject;
        payload.Slots.Should().BeEmpty();
        payload.TimeZoneUnavailableReason.Should().Be(TimeZoneUnavailableReason.LookupFailed);
    }

    [Fact]
    public async Task GetDailyBasalBolusRatios_GroupsByTheTenantsLocalDay()
    {
        SetupInsulinAtSydneyMorning();

        var result = await ControllerOnClock("Australia/Sydney", share: false)
            .GetDailyBasalBolusRatios(WindowStart, WindowEnd);

        var payload = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<DailyBasalBolusRatioResponse>().Subject;
        payload.TimeZoneUnavailableReason.Should().BeNull();
        payload.DailyData.Select(d => d.Date).Should().Equal("2026-03-02");
    }

    [Theory]
    [MemberData(nameof(UnavailableZones))]
    public async Task GetDailyBasalBolusRatios_WithoutAZone_GroupsNothingAndSaysWhy(
        string? timezoneId, bool share, TimeZoneUnavailableReason expected)
    {
        SetupInsulinAtSydneyMorning();

        var result = await ControllerOnClock(timezoneId, share).GetDailyBasalBolusRatios(WindowStart, WindowEnd);

        var payload = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<DailyBasalBolusRatioResponse>().Subject;
        payload.DailyData.Should().BeEmpty();
        payload.TimeZoneUnavailableReason.Should().Be(expected);
        _bolusRepoMock.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task GetPunchCardData_PlacesTheReadingOnTheTenantsLocalDay()
    {
        SetupGlucose([new SensorGlucose { Timestamp = SydneyMondayMorning, Mgdl = 100 }]);
        SetupEmptyTreatments();

        var result = await ControllerOnClock("Australia/Sydney", share: false)
            .GetPunchCardData(new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 2));

        var payload = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<PunchCardResponse>().Subject;
        payload.TimeZoneUnavailableReason.Should().BeNull();
        var day = payload.Months.Should().ContainSingle().Subject.Days.Should().ContainSingle().Subject;
        day.Date.Should().Be("2026-03-02");
        day.TotalReadings.Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(UnavailableZones))]
    public async Task GetPunchCardData_WithoutAZone_ReadsNothingAndSaysWhy(
        string? timezoneId, bool share, TimeZoneUnavailableReason expected)
    {
        SetupGlucose([new SensorGlucose { Timestamp = SydneyMondayMorning, Mgdl = 100 }]);
        SetupEmptyTreatments();

        var result = await ControllerOnClock(timezoneId, share)
            .GetPunchCardData(new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 2));

        var payload = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<PunchCardResponse>().Subject;
        payload.Months.Should().BeEmpty();
        payload.TimeZoneUnavailableReason.Should().Be(expected);
        _glucoseRepoMock.Invocations.Should().BeEmpty();
        _carbIntakeRepoMock.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task GetBasalAnalysis_BucketsTheHourlyPercentilesOnTheTenantsLocalHour()
    {
        SetupInsulinAtSydneyMorning();

        var result = await ControllerOnClock("Australia/Sydney", share: false)
            .GetBasalAnalysis(WindowStart, WindowEnd);

        var payload = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<BasalAnalysisResponse>().Subject;
        payload.TimeZoneUnavailableReason.Should().BeNull();
        payload.HourlyPercentiles.Should().HaveCount(24);
        payload.HourlyPercentiles.Where(h => h.Count > 0).Select(h => h.Hour).Should().Equal(7);
    }

    [Theory]
    [MemberData(nameof(UnavailableZones))]
    public async Task GetBasalAnalysis_WithoutAZone_KeepsTheStatsAndBinsNoHours(
        string? timezoneId, bool share, TimeZoneUnavailableReason expected)
    {
        SetupInsulinAtSydneyMorning();

        var result = await ControllerOnClock(timezoneId, share).GetBasalAnalysis(WindowStart, WindowEnd);

        var payload = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<BasalAnalysisResponse>().Subject;
        payload.HourlyPercentiles.Should().BeEmpty();
        payload.TimeZoneUnavailableReason.Should().Be(expected);
        payload.Stats.Count.Should().Be(1);
        payload.Stats.AvgRate.Should().Be(1.2);
    }

    [Fact]
    public async Task GetHourlyInsulinDelivery_BucketsOnTheTenantsLocalHour()
    {
        SetupInsulinAtSydneyMorning();

        var result = await ControllerOnClock("Australia/Sydney", share: false)
            .GetHourlyInsulinDelivery(WindowStart, WindowEnd);

        var payload = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<HourlyInsulinDeliveryResponse>().Subject;
        payload.TimeZoneUnavailableReason.Should().BeNull();
        payload.Hours.Should().HaveCount(24);
        payload.Hours.Where(h => h.Total > 0).Select(h => h.Hour).Should().Equal(7);
    }

    [Theory]
    [MemberData(nameof(UnavailableZones))]
    public async Task GetHourlyInsulinDelivery_WithoutAZone_BinsNothingAndSaysWhy(
        string? timezoneId, bool share, TimeZoneUnavailableReason expected)
    {
        SetupInsulinAtSydneyMorning();

        var result = await ControllerOnClock(timezoneId, share).GetHourlyInsulinDelivery(WindowStart, WindowEnd);

        var payload = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<HourlyInsulinDeliveryResponse>().Subject;
        payload.Hours.Should().BeEmpty();
        payload.TimeZoneUnavailableReason.Should().Be(expected);
        _tempBasalRepoMock.Invocations.Should().BeEmpty();
    }
}
