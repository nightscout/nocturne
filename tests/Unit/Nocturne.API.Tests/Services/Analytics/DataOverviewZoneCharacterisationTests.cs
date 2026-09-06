using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Services.Analytics;

/// <summary>
/// Pins the glucose zone bucketing behind the GRI timeline, whose percentages reach the response
/// only through <see cref="IStatisticsService.CalculateGRI"/> and are therefore asserted on the
/// argument, and the local-midnight year bounds that decide which readings a year contains at all.
/// </summary>
public class DataOverviewZoneCharacterisationTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    /// <summary>
    /// One reading on each side of, and one exactly on, every GRI zone bound: 54, 70, 180 and 250.
    /// </summary>
    private static readonly double[] ZoneEdges =
    [
        53, 54, 69, 70, 139, 140, 141, 179, 180, 181, 250, 251,
    ];

    private readonly NocturneDbContext _dbContext;
    private readonly DataOverviewService _service;
    private readonly Mock<ITherapySettingsResolver> _therapySettings = new();
    private readonly List<TimeInRangeMetrics> _griInputs = [];

    public DataOverviewZoneCharacterisationTests()
    {
        var dbName = $"data_overview_zones_{Guid.NewGuid()}";
        _dbContext = TestDbContextFactory.CreateInMemoryContext(dbName);
        _dbContext.TenantId = TenantId;

        var factory = new Mock<ITenantDbContextFactory>();
        factory
            .Setup(f => f.CreateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var context = TestDbContextFactory.CreateInMemoryContext(dbName);
                context.TenantId = TenantId;
                return context;
            });

        _therapySettings
            .Setup(p => p.GetTimezoneAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var statistics = new Mock<IStatisticsService>();
        statistics
            .Setup(s => s.CalculateGRI(It.IsAny<TimeInRangeMetrics>()))
            .Callback<TimeInRangeMetrics>(_griInputs.Add)
            .Returns(new GlycemicRiskIndex());

        _service = new DataOverviewService(
            factory.Object,
            _therapySettings.Object,
            statistics.Object,
            NullLogger<DataOverviewService>.Instance
        );
    }

    public void Dispose() => _dbContext.Dispose();

    [Fact]
    public async Task GetGriTimelineAsync_PinsEveryZoneBoundAndItsInclusivity()
    {
        // Six passes over the edge series clears the seventy-two-reading floor for a valid month.
        var start = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var readings = Enumerable
            .Range(0, 6)
            .SelectMany(pass => ZoneEdges.Select((value, index) => (Value: value, Slot: pass * 12 + index)));

        foreach (var reading in readings)
        {
            _dbContext.SensorGlucose.Add(new SensorGlucoseEntity
            {
                Id = Guid.NewGuid(),
                Timestamp = start.AddMinutes(reading.Slot * 5),
                Mgdl = reading.Value,
                DataSource = "dexcom",
            });
        }

        await _dbContext.SaveChangesAsync();

        var result = await _service.GetGriTimelineAsync(2026);

        result.Periods.Should().ContainSingle().Which.ReadingCount.Should().Be(72);

        // 53 | 54, 69 | 70, 139, 140, 141, 179, 180 | 181, 250 | 251, six times over.
        var percentages = _griInputs.Should().ContainSingle().Subject.Percentages;
        percentages.VeryLow.Should().BeApproximately(8.333333333333332, 1e-12);
        percentages.Low.Should().BeApproximately(16.666666666666664, 1e-12);
        percentages.Target.Should().Be(50);
        percentages.High.Should().BeApproximately(16.666666666666664, 1e-12);
        percentages.VeryHigh.Should().BeApproximately(8.333333333333332, 1e-12);
    }

    [Fact]
    public async Task GetGriTimelineAsync_PinsTheYearBoundsAsLocalMidnight()
    {
        await SeedYearBoundaryProbesAsync();
        _therapySettings
            .Setup(p => p.GetTimezoneAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Australia/Brisbane");

        var result = await _service.GetGriTimelineAsync(2026);

        // Brisbane is UTC+10 year round, so 2026 runs from 2025-12-31T14:00Z to 2026-12-31T14:00Z.
        // The 13:00Z probe is still 2025 locally; the 15:00Z one is January 2026.
        result.Periods.Should().ContainSingle().Which.ReadingCount.Should().Be(72);
        _griInputs.Should().ContainSingle();
    }

    [Fact]
    public async Task GetDailySummaryAsync_PinsTheYearBoundsAsLocalMidnight()
    {
        await SeedYearBoundaryProbesAsync();
        _therapySettings
            .Setup(p => p.GetTimezoneAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Australia/Brisbane");

        var result = await _service.GetDailySummaryAsync(2026);

        result.Days.Should().ContainSingle().Which.Date.Should().Be("2026-01-01");
        result.Days[0].Counts["Glucose"].Should().Be(72);
    }

    /// <summary>
    /// A NaN reaches the series from Postgres rather than from the entity model, which the
    /// in-memory provider cannot reproduce, so the month's readings are handed to
    /// <see cref="DataOverviewService.BuildGriPeriod"/> directly.
    /// </summary>
    [Fact]
    public void BuildGriPeriod_ExcludesANonReadingFromEveryZoneAndFromTheDenominator()
    {
        var readings = Enumerable.Repeat(120d, 72).Append(double.NaN).ToList();

        var period = _service.BuildGriPeriod(
            3, 2026, 72,
            new Dictionary<int, List<double>> { [3] = readings },
            [], [], [], []
        );

        period.Should().NotBeNull();
        period!.ReadingCount.Should().Be(72);
        period.AverageGlucoseMgdl.Should().Be(120);

        var percentages = _griInputs.Should().ContainSingle().Subject.Percentages;
        percentages.Target.Should().Be(100);
        percentages.VeryHigh.Should().Be(0);
    }

    /// <summary>
    /// Seventy-two readings on 2026-01-01 Brisbane time, one probe an hour before the local year
    /// starts and one an hour after it ends.
    /// </summary>
    private async Task SeedYearBoundaryProbesAsync()
    {
        var localYearStart = new DateTime(2025, 12, 31, 14, 0, 0, DateTimeKind.Utc);

        for (var index = 0; index < 72; index++)
        {
            _dbContext.SensorGlucose.Add(new SensorGlucoseEntity
            {
                Id = Guid.NewGuid(),
                Timestamp = localYearStart.AddMinutes(index * 5),
                Mgdl = 120,
                DataSource = "dexcom",
            });
        }

        _dbContext.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = localYearStart.AddHours(-1),
            Mgdl = 120,
            DataSource = "dexcom",
        });
        _dbContext.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = new DateTime(2026, 12, 31, 15, 0, 0, DateTimeKind.Utc),
            Mgdl = 120,
            DataSource = "dexcom",
        });

        await _dbContext.SaveChangesAsync();
    }
}
