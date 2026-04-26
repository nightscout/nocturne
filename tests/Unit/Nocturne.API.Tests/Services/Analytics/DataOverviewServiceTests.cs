using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Services.Analytics;

/// <summary>
/// Unit tests for DataOverviewService.
/// Uses InMemory EF Core via TestDbContextFactory for realistic query execution.
/// </summary>
public class DataOverviewServiceTests : IDisposable
{
    private readonly NocturneDbContext _dbContext;
    private readonly DataOverviewService _service;

    // Well-known timestamps (UTC)
    // 2023-06-15 12:00:00 UTC = 1686830400000
    private const long June15_2023_Noon = 1686830400000L;
    // 2024-01-01 00:00:00 UTC = 1704067200000
    private const long Jan1_2024_Midnight = 1704067200000L;
    // 2024-06-15 12:00:00 UTC = 1718452800000
    private const long June15_2024_Noon = 1718452800000L;
    // 2024-12-31 23:00:00 UTC = 1735686000000
    private const long Dec31_2024_23h = 1735686000000L;
    // 2025-01-01 01:00:00 UTC = 1735693200000
    private const long Jan1_2025_01h = 1735693200000L;

    public DataOverviewServiceTests()
    {
        _dbContext = TestDbContextFactory.CreateInMemoryContext();
        _dbContext.TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var mockTherapySettingsResolver = new Mock<ITherapySettingsResolver>();
        mockTherapySettingsResolver.Setup(p => p.GetTimezoneAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        var mockStatisticsService = new Mock<IStatisticsService>();
        _service = new DataOverviewService(
            _dbContext,
            mockTherapySettingsResolver.Object,
            mockStatisticsService.Object,
            NullLogger<DataOverviewService>.Instance
        );
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }

    #region GetAvailableYearsAsync Tests

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetAvailableYearsAsync_EmptyDb_ReturnsEmptyYearsAndSources()
    {
        var result = await _service.GetAvailableYearsAsync();

        result.Years.Should().BeEmpty();
        result.AvailableDataSources.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetAvailableYearsAsync_SingleTableWithData_ReturnsCorrectYear()
    {
        _dbContext.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon).UtcDateTime,
            Mgdl = 120.0,
            DataSource = "dexcom"
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetAvailableYearsAsync();

        result.Years.Should().ContainSingle().Which.Should().Be(2024);
        result.AvailableDataSources.Should().ContainSingle().Which.Should().Be("dexcom");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetAvailableYearsAsync_MultipleTablesSpanningYears_ReturnsFullRange()
    {
        _dbContext.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2023_Noon).UtcDateTime,
            Mgdl = 100.0,
            DataSource = "dexcom"
        });
        _dbContext.Boluses.Add(new BolusEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(Jan1_2025_01h).UtcDateTime,
            Insulin = 5.0,
            DataSource = "glooko"
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetAvailableYearsAsync();

        result.Years.Should().BeEquivalentTo([2023, 2024, 2025]);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetAvailableYearsAsync_DataSourcesCollectedCorrectly()
    {
        _dbContext.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon).UtcDateTime,
            Mgdl = 120.0,
            DataSource = "dexcom"
        });
        _dbContext.Boluses.Add(new BolusEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon + 1000).UtcDateTime,
            Insulin = 3.0,
            DataSource = "glooko"
        });
        _dbContext.CarbIntakes.Add(new CarbIntakeEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon + 2000).UtcDateTime,
            Carbs = 30.0,
            DataSource = "dexcom"
        });
        _dbContext.StateSpans.Add(new StateSpanEntity
        {
            Id = Guid.NewGuid(),
            Category = "PumpMode",
            State = "Automatic",
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon + 3000).UtcDateTime,
            Source = "medtronic"
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetAvailableYearsAsync();

        result.AvailableDataSources.Should().HaveCount(3);
        result.AvailableDataSources.Should().ContainInOrder("dexcom", "glooko", "medtronic");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetAvailableYearsAsync_NullDataSourcesNotIncluded()
    {
        _dbContext.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon).UtcDateTime,
            Mgdl = 120.0,
            DataSource = null
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetAvailableYearsAsync();

        result.Years.Should().ContainSingle().Which.Should().Be(2024);
        result.AvailableDataSources.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetAvailableYearsAsync_StateSpansUseStartMills()
    {
        _dbContext.StateSpans.Add(new StateSpanEntity
        {
            Id = Guid.NewGuid(),
            Category = "PumpMode",
            State = "Automatic",
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2023_Noon).UtcDateTime,
            EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2023_Noon + 3600000).UtcDateTime,
            Source = "glooko"
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetAvailableYearsAsync();

        result.Years.Should().ContainSingle().Which.Should().Be(2023);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetAvailableYearsAsync_LegacyEntriesIncluded()
    {
        _dbContext.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2023_Noon).UtcDateTime,
            Mgdl = 150.0,
            DataSource = "nightscout"
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetAvailableYearsAsync();

        result.Years.Should().ContainSingle().Which.Should().Be(2023);
        result.AvailableDataSources.Should().ContainSingle().Which.Should().Be("nightscout");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetAvailableYearsAsync_ApsSnapshotsIncludedInYears()
    {
        _dbContext.ApsSnapshots.Add(new ApsSnapshotEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon).UtcDateTime,
            Device = "test-device",
            AidAlgorithm = "OpenAPS",
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow,
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetAvailableYearsAsync();

        result.Years.Should().ContainSingle().Which.Should().Be(2024);
        result.AvailableDataSources.Should().BeEmpty();
    }

    #endregion

    #region GetDailySummaryAsync Tests

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetDailySummaryAsync_EmptyDb_ReturnsEmptyDays()
    {
        var result = await _service.GetDailySummaryAsync(2024);

        result.Year.Should().Be(2024);
        result.DataSources.Should().BeNull();
        result.Days.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetDailySummaryAsync_SingleGlucoseReading_ReturnsCorrectCountAndAverage()
    {
        _dbContext.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon).UtcDateTime,
            Mgdl = 150.0,
            DataSource = "dexcom"
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetDailySummaryAsync(2024);

        result.Days.Should().ContainSingle();
        var day = result.Days[0];
        day.Date.Should().Be("2024-06-15");
        day.Counts["Glucose"].Should().Be(1);
        day.TotalCount.Should().Be(1);
        day.AverageGlucoseMgdl.Should().Be(150.0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetDailySummaryAsync_MultipleDataTypesOnSameDay_ReturnsAllCountsAndCorrectTotal()
    {
        _dbContext.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon).UtcDateTime,
            Mgdl = 120.0,
            DataSource = "dexcom"
        });
        _dbContext.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon + 300000).UtcDateTime,
            Mgdl = 130.0,
            DataSource = "dexcom"
        });
        _dbContext.Boluses.Add(new BolusEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon + 600000).UtcDateTime,
            Insulin = 5.0,
            DataSource = "dexcom"
        });
        _dbContext.CarbIntakes.Add(new CarbIntakeEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon + 900000).UtcDateTime,
            Carbs = 45.0,
            DataSource = "dexcom"
        });
        _dbContext.Notes.Add(new NoteEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon + 1200000).UtcDateTime,
            Text = "Feeling good",
            DataSource = "dexcom"
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetDailySummaryAsync(2024);

        result.Days.Should().ContainSingle();
        var day = result.Days[0];
        day.Date.Should().Be("2024-06-15");
        day.Counts["Glucose"].Should().Be(2);
        day.Counts["Boluses"].Should().Be(1);
        day.Counts["CarbIntake"].Should().Be(1);
        day.Counts["Notes"].Should().Be(1);
        day.TotalCount.Should().Be(5);
        day.AverageGlucoseMgdl.Should().Be(125.0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetDailySummaryAsync_DataSourceFilter_OnlyMatchingRecords()
    {
        _dbContext.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon).UtcDateTime,
            Mgdl = 120.0,
            DataSource = "dexcom"
        });
        _dbContext.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon + 300000).UtcDateTime,
            Mgdl = 200.0,
            DataSource = "glooko"
        });
        _dbContext.Boluses.Add(new BolusEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon + 600000).UtcDateTime,
            Insulin = 3.0,
            DataSource = "dexcom"
        });
        _dbContext.Boluses.Add(new BolusEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon + 900000).UtcDateTime,
            Insulin = 7.0,
            DataSource = "glooko"
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetDailySummaryAsync(2024, ["dexcom"]);

        result.DataSources.Should().BeEquivalentTo(["dexcom"]);
        result.Days.Should().ContainSingle();
        var day = result.Days[0];
        day.Counts["Glucose"].Should().Be(1);
        day.Counts["Boluses"].Should().Be(1);
        day.TotalCount.Should().Be(2);
        day.AverageGlucoseMgdl.Should().Be(120.0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetDailySummaryAsync_MultipleDataSourceFilter_MatchesAll()
    {
        _dbContext.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon).UtcDateTime,
            Mgdl = 120.0,
            DataSource = "dexcom"
        });
        _dbContext.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon + 300000).UtcDateTime,
            Mgdl = 200.0,
            DataSource = "glooko"
        });
        _dbContext.Boluses.Add(new BolusEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon + 600000).UtcDateTime,
            Insulin = 3.0,
            DataSource = "medtronic"
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetDailySummaryAsync(2024, ["dexcom", "glooko"]);

        result.Days.Should().ContainSingle();
        var day = result.Days[0];
        day.Counts["Glucose"].Should().Be(2);
        day.Counts.Should().NotContainKey("Boluses");
        day.AverageGlucoseMgdl.Should().Be(160.0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetDailySummaryAsync_StateSpansUseStartMillsAndSource()
    {
        _dbContext.StateSpans.Add(new StateSpanEntity
        {
            Id = Guid.NewGuid(),
            Category = "PumpMode",
            State = "Automatic",
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon).UtcDateTime,
            Source = "glooko"
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetDailySummaryAsync(2024);

        result.Days.Should().ContainSingle();
        var day = result.Days[0];
        day.Date.Should().Be("2024-06-15");
        day.Counts["StateSpans"].Should().Be(1);
        day.TotalCount.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetDailySummaryAsync_StateSpansFilteredBySource()
    {
        _dbContext.StateSpans.Add(new StateSpanEntity
        {
            Id = Guid.NewGuid(),
            Category = "PumpMode",
            State = "Automatic",
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon).UtcDateTime,
            Source = "glooko"
        });
        _dbContext.StateSpans.Add(new StateSpanEntity
        {
            Id = Guid.NewGuid(),
            Category = "PumpMode",
            State = "Manual",
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon + 300000).UtcDateTime,
            Source = "medtronic"
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetDailySummaryAsync(2024, ["glooko"]);

        result.Days.Should().ContainSingle();
        result.Days[0].Counts["StateSpans"].Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetDailySummaryAsync_ApsSnapshotsExcludedWhenDataSourceFilterActive()
    {
        _dbContext.ApsSnapshots.Add(new ApsSnapshotEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon).UtcDateTime,
            Device = "test-device",
            AidAlgorithm = "OpenAPS",
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow,
        });
        _dbContext.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon + 1000).UtcDateTime,
            Mgdl = 100.0,
            DataSource = "dexcom"
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetDailySummaryAsync(2024, ["dexcom"]);

        result.Days.Should().ContainSingle();
        var day = result.Days[0];
        day.Counts.Should().NotContainKey("DeviceStatus");
        day.TotalCount.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetDailySummaryAsync_ApsSnapshotsIncludedWithoutFilter()
    {
        _dbContext.ApsSnapshots.Add(new ApsSnapshotEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon).UtcDateTime,
            Device = "test-device",
            AidAlgorithm = "OpenAPS",
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow,
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetDailySummaryAsync(2024);

        result.Days.Should().ContainSingle();
        var day = result.Days[0];
        day.Counts["DeviceStatus"].Should().Be(1);
        day.TotalCount.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetDailySummaryAsync_LegacyEntrySgv_CountedAsGlucose()
    {
        _dbContext.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon).UtcDateTime,
            Mgdl = 140.0,
            DataSource = "nightscout"
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetDailySummaryAsync(2024);

        result.Days.Should().ContainSingle();
        var day = result.Days[0];
        day.Counts["Glucose"].Should().Be(1);
        day.AverageGlucoseMgdl.Should().Be(140.0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetDailySummaryAsync_LegacyEntryMbg_CountedAsManualBGAndContributesToAverage()
    {
        _dbContext.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon).UtcDateTime,
            Mgdl = 160.0,
            DataSource = "nightscout"
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetDailySummaryAsync(2024);

        result.Days.Should().ContainSingle();
        var day = result.Days[0];
        // Former mbg entries now come through as SensorGlucose
        day.Counts["Glucose"].Should().Be(1);
        day.AverageGlucoseMgdl.Should().Be(160.0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetDailySummaryAsync_MixedSensorAndLegacySgv_CombinedGlucoseAverage()
    {
        _dbContext.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon).UtcDateTime,
            Mgdl = 100.0,
            DataSource = "dexcom"
        });
        _dbContext.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon + 300000).UtcDateTime,
            Mgdl = 200.0,
            DataSource = "nightscout"
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetDailySummaryAsync(2024);

        var day = result.Days[0];
        day.Counts["Glucose"].Should().Be(2);
        day.AverageGlucoseMgdl.Should().Be(150.0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetDailySummaryAsync_DataOutsideYearExcluded()
    {
        _dbContext.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2023_Noon).UtcDateTime,
            Mgdl = 100.0,
            DataSource = "dexcom"
        });
        _dbContext.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon).UtcDateTime,
            Mgdl = 200.0,
            DataSource = "dexcom"
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetDailySummaryAsync(2024);

        result.Days.Should().ContainSingle();
        result.Days[0].Date.Should().Be("2024-06-15");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetDailySummaryAsync_DataSourceFilterWithNoMatches_ReturnsEmptyDays()
    {
        _dbContext.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon).UtcDateTime,
            Mgdl = 120.0,
            DataSource = "dexcom"
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetDailySummaryAsync(2024, ["nonexistent-source"]);

        result.Days.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetDailySummaryAsync_YearBoundary_Dec31ToJan1()
    {
        _dbContext.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(Dec31_2024_23h).UtcDateTime,
            Mgdl = 110.0,
            DataSource = "dexcom"
        });
        _dbContext.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(Jan1_2025_01h).UtcDateTime,
            Mgdl = 130.0,
            DataSource = "dexcom"
        });
        await _dbContext.SaveChangesAsync();

        var result2024 = await _service.GetDailySummaryAsync(2024);
        var result2025 = await _service.GetDailySummaryAsync(2025);

        result2024.Days.Should().ContainSingle();
        result2024.Days[0].Date.Should().Be("2024-12-31");
        result2024.Days[0].AverageGlucoseMgdl.Should().Be(110.0);

        result2025.Days.Should().ContainSingle();
        result2025.Days[0].Date.Should().Be("2025-01-01");
        result2025.Days[0].AverageGlucoseMgdl.Should().Be(130.0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetDailySummaryAsync_MultipleDays_OrderedByDate()
    {
        _dbContext.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon).UtcDateTime,
            Mgdl = 100.0
        });
        _dbContext.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(Jan1_2024_Midnight).UtcDateTime,
            Mgdl = 200.0
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetDailySummaryAsync(2024);

        result.Days.Should().HaveCount(2);
        result.Days[0].Date.Should().Be("2024-01-01");
        result.Days[1].Date.Should().Be("2024-06-15");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetDailySummaryAsync_GlucoseAverageRoundedToOneDecimal()
    {
        _dbContext.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon).UtcDateTime,
            Mgdl = 100.0
        });
        _dbContext.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon + 300000).UtcDateTime,
            Mgdl = 133.0
        });
        _dbContext.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon + 600000).UtcDateTime,
            Mgdl = 150.0
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetDailySummaryAsync(2024);

        // Average of (100 + 133 + 150) / 3 = 127.666... -> rounded to 127.7
        result.Days[0].AverageGlucoseMgdl.Should().Be(127.7);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetDailySummaryAsync_MeterGlucoseContributesToGlucoseAverage()
    {
        _dbContext.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon).UtcDateTime,
            Mgdl = 100.0,
            DataSource = "dexcom"
        });
        _dbContext.MeterGlucose.Add(new MeterGlucoseEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon + 300000).UtcDateTime,
            Mgdl = 200.0,
            DataSource = "dexcom"
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetDailySummaryAsync(2024);

        result.Days.Should().ContainSingle();
        var day = result.Days[0];
        day.Counts["Glucose"].Should().Be(1);
        day.Counts["ManualBG"].Should().Be(1);
        // Average includes both sensor and meter: (100 + 200) / 2 = 150
        day.AverageGlucoseMgdl.Should().Be(150.0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetDailySummaryAsync_BolusCalculationsAndDeviceEvents_CountedCorrectly()
    {
        _dbContext.BolusCalculations.Add(new BolusCalculationEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon).UtcDateTime,
            DataSource = "glooko"
        });
        _dbContext.DeviceEvents.Add(new DeviceEventEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon + 1000).UtcDateTime,
            EventType = "SiteChange",
            DataSource = "glooko"
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetDailySummaryAsync(2024);

        result.Days.Should().ContainSingle();
        var day = result.Days[0];
        day.Counts["BolusCalculations"].Should().Be(1);
        day.Counts["DeviceEvents"].Should().Be(1);
        day.TotalCount.Should().Be(2);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetDailySummaryAsync_LegacyEntriesFilteredByDataSource()
    {
        _dbContext.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon).UtcDateTime,
            Mgdl = 120.0,
            DataSource = "nightscout"
        });
        _dbContext.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon + 300000).UtcDateTime,
            Mgdl = 180.0,
            DataSource = "dexcom"
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetDailySummaryAsync(2024, ["nightscout"]);

        result.Days.Should().ContainSingle();
        var day = result.Days[0];
        day.Counts["Glucose"].Should().Be(1);
        day.AverageGlucoseMgdl.Should().Be(120.0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetDailySummaryAsync_NoGlucoseData_AverageIsNull()
    {
        _dbContext.Boluses.Add(new BolusEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon).UtcDateTime,
            Insulin = 5.0,
            DataSource = "glooko"
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetDailySummaryAsync(2024);

        result.Days.Should().ContainSingle();
        var day = result.Days[0];
        day.AverageGlucoseMgdl.Should().BeNull();
        day.Counts["Boluses"].Should().Be(1);
    }

    #endregion

    #region Insulin Totals Tests

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetDailySummaryAsync_BolusInsulin_CalculatedCorrectly()
    {
        _dbContext.Boluses.Add(new BolusEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon).UtcDateTime,
            Insulin = 5.5,
            DataSource = "glooko"
        });
        _dbContext.Boluses.Add(new BolusEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon + 300000).UtcDateTime,
            Insulin = 3.2,
            DataSource = "glooko"
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetDailySummaryAsync(2024);

        result.Days.Should().ContainSingle();
        var day = result.Days[0];
        day.TotalBolusUnits.Should().Be(8.7);
        day.TotalBasalUnits.Should().BeNull();
        day.TotalDailyDose.Should().Be(8.7);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetDailySummaryAsync_BasalFromAlgorithmBoluses_CalculatedCorrectly()
    {
        // Two algorithm boluses: 0.3U + 0.5U = 0.8U total basal
        _dbContext.Boluses.Add(new BolusEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon).UtcDateTime,
            Insulin = 0.3,
            BolusKind = "Algorithm",
            Automatic = true,
            DataSource = "glooko"
        });
        _dbContext.Boluses.Add(new BolusEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon + 300000).UtcDateTime,
            Insulin = 0.5,
            BolusKind = "Algorithm",
            Automatic = true,
            DataSource = "glooko"
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetDailySummaryAsync(2024);

        result.Days.Should().ContainSingle();
        var day = result.Days[0];
        day.TotalBasalUnits.Should().Be(0.8);
        day.TotalDailyDose.Should().Be(0.8);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetDailySummaryAsync_BasalFromTempBasals_CalculatedFromRateAndDuration()
    {
        // TempBasal: 1.0 U/hr for 1 hour (3600000ms) = 1.0U
        // TempBasal: 0.5 U/hr for 30 min (1800000ms) = 0.25U
        // Total: 1.25U
        _dbContext.TempBasals.Add(new TempBasalEntity
        {
            Id = Guid.NewGuid(),
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon).UtcDateTime,
            EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon + 3600000).UtcDateTime,
            Rate = 1.0,
            Origin = "Scheduled",
            DataSource = "glooko"
        });
        _dbContext.TempBasals.Add(new TempBasalEntity
        {
            Id = Guid.NewGuid(),
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon + 3600000).UtcDateTime,
            EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon + 5400000).UtcDateTime,
            Rate = 0.5,
            Origin = "Algorithm",
            DataSource = "glooko"
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetDailySummaryAsync(2024);

        result.Days.Should().ContainSingle();
        var day = result.Days[0];
        day.TotalBasalUnits.Should().Be(1.25);
        day.TotalDailyDose.Should().Be(1.25);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetDailySummaryAsync_TempBasalWithoutEndMills_UsesDefaultFiveMinuteDuration()
    {
        // TempBasal with no EndMills: 1.2 U/hr for default 5 min = 1.2 * (5*60*1000) / (1000*60*60) = 0.1U
        _dbContext.TempBasals.Add(new TempBasalEntity
        {
            Id = Guid.NewGuid(),
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon).UtcDateTime,
            EndTimestamp = null,
            Rate = 1.2,
            Origin = "Algorithm",
            DataSource = "glooko"
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetDailySummaryAsync(2024);

        result.Days.Should().ContainSingle();
        var day = result.Days[0];
        day.TotalBasalUnits.Should().Be(0.1);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetDailySummaryAsync_AlgorithmBolusAndTempBasal_CombinedForTotalBasal()
    {
        // Algorithm Bolus: 0.5U + TempBasal: 1.0 U/hr for 1hr = 1.0U -> Total basal = 1.5U
        _dbContext.Boluses.Add(new BolusEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon).UtcDateTime,
            Insulin = 0.5,
            BolusKind = "Algorithm",
            Automatic = true,
            DataSource = "glooko"
        });
        _dbContext.TempBasals.Add(new TempBasalEntity
        {
            Id = Guid.NewGuid(),
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon).UtcDateTime,
            EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon + 3600000).UtcDateTime,
            Rate = 1.0,
            Origin = "Scheduled",
            DataSource = "glooko"
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetDailySummaryAsync(2024);

        result.Days.Should().ContainSingle();
        var day = result.Days[0];
        day.TotalBasalUnits.Should().Be(1.5);
        day.TotalDailyDose.Should().Be(1.5);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetDailySummaryAsync_MultipleBoluses_AllCountAsBolus()
    {
        _dbContext.Boluses.Add(new BolusEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon).UtcDateTime,
            Insulin = 5.0,
            DataSource = "glooko"
        });
        _dbContext.Boluses.Add(new BolusEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon + 300000).UtcDateTime,
            Insulin = 10.0,
            DataSource = "glooko"
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetDailySummaryAsync(2024);

        result.Days.Should().ContainSingle();
        var day = result.Days[0];
        day.TotalBolusUnits.Should().Be(15.0);
        day.TotalDailyDose.Should().Be(15.0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetDailySummaryAsync_NoInsulinData_InsulinFieldsNull()
    {
        _dbContext.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon).UtcDateTime,
            Mgdl = 120.0,
            DataSource = "dexcom"
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetDailySummaryAsync(2024);

        result.Days.Should().ContainSingle();
        var day = result.Days[0];
        day.TotalBolusUnits.Should().BeNull();
        day.TotalBasalUnits.Should().BeNull();
        day.TotalDailyDose.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetDailySummaryAsync_InsulinFilteredByDataSource()
    {
        _dbContext.Boluses.Add(new BolusEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon).UtcDateTime,
            Insulin = 5.0,
            DataSource = "dexcom"
        });
        _dbContext.Boluses.Add(new BolusEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon + 300000).UtcDateTime,
            Insulin = 10.0,
            DataSource = "glooko"
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetDailySummaryAsync(2024, ["dexcom"]);

        result.Days.Should().ContainSingle();
        var day = result.Days[0];
        day.TotalBolusUnits.Should().Be(5.0);
        day.TotalDailyDose.Should().Be(5.0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetDailySummaryAsync_BasalFromAlgorithmBoluses_CombinedWithBolusForTdd()
    {
        // Bolus: 5U, Algorithm Bolus basal: 2.5U -> TDD = 7.5U
        _dbContext.Boluses.Add(new BolusEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon).UtcDateTime,
            Insulin = 5.0,
            DataSource = "glooko"
        });
        _dbContext.Boluses.Add(new BolusEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon + 300000).UtcDateTime,
            Insulin = 2.5,
            BolusKind = "Algorithm",
            Automatic = true,
            DataSource = "glooko"
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetDailySummaryAsync(2024);

        result.Days.Should().ContainSingle();
        var day = result.Days[0];
        day.TotalBolusUnits.Should().Be(5.0);
        day.TotalBasalUnits.Should().Be(2.5);
        day.TotalDailyDose.Should().Be(7.5);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetDailySummaryAsync_BasalFromTempBasals_CombinedWithBolusForTdd()
    {
        // Bolus: 5U, TempBasal: 2.0 U/hr for 5hr = 10U -> TDD = 15U
        _dbContext.Boluses.Add(new BolusEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon).UtcDateTime,
            Insulin = 5.0,
            DataSource = "glooko"
        });
        _dbContext.TempBasals.Add(new TempBasalEntity
        {
            Id = Guid.NewGuid(),
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon).UtcDateTime,
            EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon + 18000000).UtcDateTime, // 5 hours
            Rate = 2.0,
            Origin = "Scheduled",
            DataSource = "glooko"
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetDailySummaryAsync(2024);

        result.Days.Should().ContainSingle();
        var day = result.Days[0];
        day.TotalBolusUnits.Should().Be(5.0);
        day.TotalBasalUnits.Should().Be(10.0);
        day.TotalDailyDose.Should().Be(15.0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetDailySummaryAsync_BasalAlgorithmBoluses_FilteredByDataSource()
    {
        _dbContext.Boluses.Add(new BolusEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon).UtcDateTime,
            Insulin = 0.5,
            BolusKind = "Algorithm",
            Automatic = true,
            DataSource = "glooko"
        });
        _dbContext.Boluses.Add(new BolusEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon + 300000).UtcDateTime,
            Insulin = 0.3,
            BolusKind = "Algorithm",
            Automatic = true,
            DataSource = "medtronic"
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetDailySummaryAsync(2024, ["glooko"]);

        result.Days.Should().ContainSingle();
        result.Days[0].TotalBasalUnits.Should().Be(0.5);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetDailySummaryAsync_BasalTempBasals_FilteredByDataSource()
    {
        _dbContext.TempBasals.Add(new TempBasalEntity
        {
            Id = Guid.NewGuid(),
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon).UtcDateTime,
            EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon + 3600000).UtcDateTime,
            Rate = 1.0,
            Origin = "Scheduled",
            DataSource = "glooko"
        });
        _dbContext.TempBasals.Add(new TempBasalEntity
        {
            Id = Guid.NewGuid(),
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon + 300000).UtcDateTime,
            EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon + 3900000).UtcDateTime,
            Rate = 0.8,
            Origin = "Algorithm",
            DataSource = "medtronic"
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetDailySummaryAsync(2024, ["glooko"]);

        result.Days.Should().ContainSingle();
        result.Days[0].TotalBasalUnits.Should().Be(1.0);
    }

    #endregion

    #region Carb Totals Tests

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetDailySummaryAsync_CarbTotals_CalculatedCorrectly()
    {
        _dbContext.CarbIntakes.Add(new CarbIntakeEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon).UtcDateTime,
            Carbs = 45.0,
            DataSource = "mylife"
        });
        _dbContext.CarbIntakes.Add(new CarbIntakeEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon + 3600000).UtcDateTime, // 1 hour later, same day
            Carbs = 30.5,
            DataSource = "mylife"
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetDailySummaryAsync(2024);

        result.Days.Should().ContainSingle();
        var day = result.Days[0];
        day.TotalCarbs.Should().Be(75.5);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetDailySummaryAsync_NoCarbData_CarbFieldNull()
    {
        _dbContext.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon).UtcDateTime,
            Mgdl = 120,
            DataSource = "dexcom"
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetDailySummaryAsync(2024);

        result.Days.Should().ContainSingle();
        result.Days[0].TotalCarbs.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetDailySummaryAsync_CarbsFilteredByDataSource()
    {
        _dbContext.CarbIntakes.Add(new CarbIntakeEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon).UtcDateTime,
            Carbs = 50.0,
            DataSource = "mylife"
        });
        _dbContext.CarbIntakes.Add(new CarbIntakeEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon + 300000).UtcDateTime,
            Carbs = 25.0,
            DataSource = "glooko"
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetDailySummaryAsync(2024, ["mylife"]);

        result.Days.Should().ContainSingle();
        result.Days[0].TotalCarbs.Should().Be(50.0);
    }

    #endregion

    #region Time In Range Tests

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetDailySummaryAsync_WithGlucoseReadings_ComputesTimeInRangePercent()
    {
        // Arrange: 4 readings on June 15, 2024 — 3 in range (70-180), 1 high (250)
        var baseTime = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon).UtcDateTime;
        _dbContext.SensorGlucose.AddRange(
            new SensorGlucoseEntity { Id = Guid.NewGuid(), Timestamp = baseTime, Mgdl = 100.0, DataSource = "dexcom" },
            new SensorGlucoseEntity { Id = Guid.NewGuid(), Timestamp = baseTime.AddMinutes(5), Mgdl = 120.0, DataSource = "dexcom" },
            new SensorGlucoseEntity { Id = Guid.NewGuid(), Timestamp = baseTime.AddMinutes(10), Mgdl = 150.0, DataSource = "dexcom" },
            new SensorGlucoseEntity { Id = Guid.NewGuid(), Timestamp = baseTime.AddMinutes(15), Mgdl = 250.0, DataSource = "dexcom" }
        );
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _service.GetDailySummaryAsync(2024);

        // Assert
        var june15 = result.Days.FirstOrDefault(d => d.Date == "2024-06-15");
        june15.Should().NotBeNull();
        june15!.TimeInRangePercent.Should().BeApproximately(75.0, 0.1); // 3 of 4 readings in range
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetDailySummaryAsync_DayWithNoGlucose_TimeInRangeIsNull()
    {
        // Arrange: only a bolus, no glucose
        _dbContext.Boluses.Add(new BolusEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(June15_2024_Noon).UtcDateTime,
            Insulin = 5.0,
            DataSource = "glooko"
        });
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _service.GetDailySummaryAsync(2024);

        // Assert
        var june15 = result.Days.FirstOrDefault(d => d.Date == "2024-06-15");
        june15.Should().NotBeNull();
        june15!.TimeInRangePercent.Should().BeNull();
    }

    #endregion
}
