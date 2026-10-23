using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Analytics;
using Nocturne.API.Services.Caching;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Analytics;

/// <summary>
/// Every report here groups by the tenant's local day, month or year, so none of them groups at all
/// without a zone. The readings sit at 06:00 on 16 June in Sydney and at 20:00 on 15 June in UTC,
/// and one more at 20:00 UTC on 31 December, which is already 2025 in Sydney.
/// </summary>
[Trait("Category", "Unit")]
public class DataOverviewServiceTimeZoneTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTime SydneyMorning = new(2024, 6, 15, 20, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SydneyNewYear = new(2024, 12, 31, 20, 0, 0, DateTimeKind.Utc);

    public static TheoryData<string?, bool, TimeZoneUnavailableReason> UnavailableZones => new()
    {
        { null, false, TimeZoneUnavailableReason.NotConfigured },
        { "Not/AZone", false, TimeZoneUnavailableReason.Unrecognised },
        { null, true, TimeZoneUnavailableReason.Share },
        { "Not/AZone", true, TimeZoneUnavailableReason.Share },
    };

    private readonly string _dbName = $"data_overview_zone_{Guid.NewGuid()}";
    private readonly NocturneDbContext _seed;

    public DataOverviewServiceTimeZoneTests()
    {
        _seed = TestDbContextFactory.CreateInMemoryContext(_dbName);
        _seed.TenantId = TenantId;
        for (var i = 0; i < 100; i++)
        {
            _seed.SensorGlucose.Add(new SensorGlucoseEntity
            {
                Id = Guid.NewGuid(),
                Timestamp = SydneyMorning.AddMinutes(i),
                Mgdl = 120,
                DataSource = "vendor-a",
            });
        }
        _seed.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = SydneyNewYear,
            Mgdl = 120,
            DataSource = "vendor-a",
        });
        _seed.SaveChanges();
    }

    public void Dispose() => _seed.Dispose();

    private DataOverviewService ServiceOn(string? timezoneId, bool share)
    {
        var factory = new Mock<INocturneDbContextFactory>();
        factory
            .Setup(f => f.CreateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var context = TestDbContextFactory.CreateInMemoryContext(_dbName);
                context.TenantId = TenantId;
                return context;
            });

        var settings = new Mock<ITherapySettingsResolver>();
        settings
            .Setup(s => s.GetTimezoneAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(timezoneId);
        var category = new CategoryReadContext();
        if (share)
            category.MarkShare();

        var statistics = new Mock<IStatisticsService>();
        statistics
            .Setup(s => s.CalculateGRI(It.IsAny<TimeInRangeMetrics>()))
            .Returns(new GlycemicRiskIndex());

        return new DataOverviewService(
            factory.Object,
            new TenantClockResolver(settings.Object, category, NullLogger<TenantClockResolver>.Instance),
            statistics.Object,
            Mock.Of<ITenantCache>(),
            NullLogger<DataOverviewService>.Instance);
    }

    [Fact]
    public async Task GetAvailableYearsAsync_ListsTheTenantsLocalYears()
    {
        var result = await ServiceOn("Australia/Sydney", share: false).GetAvailableYearsAsync();

        result.TimeZoneUnavailableReason.Should().BeNull();
        result.Years.Should().Equal(2024, 2025);
    }

    [Theory]
    [MemberData(nameof(UnavailableZones))]
    public async Task GetAvailableYearsAsync_WithoutAZone_ListsNoYearsButKeepsTheSources(
        string? timezoneId, bool share, TimeZoneUnavailableReason expected)
    {
        var result = await ServiceOn(timezoneId, share).GetAvailableYearsAsync();

        result.Years.Should().BeEmpty();
        result.AvailableDataSources.Should().Equal("vendor-a");
        result.TimeZoneUnavailableReason.Should().Be(expected);
    }

    [Fact]
    public async Task GetDailySummaryAsync_GroupsByTheTenantsLocalDay()
    {
        var result = await ServiceOn("Australia/Sydney", share: false).GetDailySummaryAsync(2024);

        result.TimeZoneUnavailableReason.Should().BeNull();
        result.Days.Select(d => d.Date).Should().Equal("2024-06-16");
    }

    [Theory]
    [MemberData(nameof(UnavailableZones))]
    public async Task GetDailySummaryAsync_WithoutAZone_GroupsNothingAndSaysWhy(
        string? timezoneId, bool share, TimeZoneUnavailableReason expected)
    {
        var result = await ServiceOn(timezoneId, share).GetDailySummaryAsync(2024);

        result.Year.Should().Be(2024);
        result.Days.Should().BeEmpty();
        result.TimeZoneUnavailableReason.Should().Be(expected);
    }

    [Fact]
    public async Task GetGriTimelineAsync_GroupsByTheTenantsLocalMonth()
    {
        var result = await ServiceOn("Australia/Sydney", share: false).GetGriTimelineAsync(2024);

        result.TimeZoneUnavailableReason.Should().BeNull();
        result.Periods.Should().ContainSingle().Which.ReadingCount.Should().Be(100);
    }

    [Theory]
    [MemberData(nameof(UnavailableZones))]
    public async Task GetGriTimelineAsync_WithoutAZone_GroupsNothingAndSaysWhy(
        string? timezoneId, bool share, TimeZoneUnavailableReason expected)
    {
        var result = await ServiceOn(timezoneId, share).GetGriTimelineAsync(2024);

        result.Periods.Should().BeEmpty();
        result.TimeZoneUnavailableReason.Should().Be(expected);
    }

    [Fact]
    public async Task GetEHbA1cTimelineAsync_EstimatesPerTheTenantsLocalDay()
    {
        var result = await ServiceOn("Australia/Sydney", share: false).GetEHbA1cTimelineAsync(2024);

        result.TimeZoneUnavailableReason.Should().BeNull();
        result.Points.Should().NotBeEmpty();
        result.Points[0].Date.Should().Be("2024-06-16");
    }

    [Theory]
    [MemberData(nameof(UnavailableZones))]
    public async Task GetEHbA1cTimelineAsync_WithoutAZone_EstimatesNothingAndSaysWhy(
        string? timezoneId, bool share, TimeZoneUnavailableReason expected)
    {
        var result = await ServiceOn(timezoneId, share).GetEHbA1cTimelineAsync(2024);

        result.Points.Should().BeEmpty();
        result.TimeZoneUnavailableReason.Should().Be(expected);
    }

    [Fact]
    public async Task GetYearSummaryAsync_BuildsBothReportsOnTheTenantsLocalClock()
    {
        var result = await ServiceOn("Australia/Sydney", share: false).GetYearSummaryAsync(2024);

        result.TimeZoneUnavailableReason.Should().BeNull();
        result.DailySummary!.Days.Select(d => d.Date).Should().Equal("2024-06-16");
        result.GriTimeline!.Periods.Should().ContainSingle();
    }

    [Theory]
    [MemberData(nameof(UnavailableZones))]
    public async Task GetYearSummaryAsync_WithoutAZone_BuildsNeitherReportAndSaysWhy(
        string? timezoneId, bool share, TimeZoneUnavailableReason expected)
    {
        var result = await ServiceOn(timezoneId, share).GetYearSummaryAsync(2024);

        result.DailySummary.Should().BeNull();
        result.GriTimeline.Should().BeNull();
        result.TimeZoneUnavailableReason.Should().Be(expected);
    }
}
