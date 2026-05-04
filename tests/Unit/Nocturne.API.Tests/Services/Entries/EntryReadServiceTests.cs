using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.Entries;
using Nocturne.API.Services.Platform;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Entries;

public class EntryReadServiceTests
{
    private readonly Mock<ISensorGlucoseRepository> _sgRepo = new();
    private readonly Mock<IMeterGlucoseRepository> _mgRepo = new();
    private readonly Mock<ICalibrationRepository> _calRepo = new();
    private readonly Mock<IDemoModeService> _demoMode = new();
    private readonly EntryReadService _sut;

    private static readonly DateTime Now = new(2025, 1, 15, 12, 0, 0, DateTimeKind.Utc);

    public EntryReadServiceTests()
    {
        _demoMode.Setup(d => d.IsEnabled).Returns(false);
        _sut = new EntryReadService(
            _sgRepo.Object,
            _mgRepo.Object,
            _calRepo.Object,
            _demoMode.Object,
            Mock.Of<ILogger<EntryReadService>>());
    }

    #region QueryAsync — type routing

    [Fact]
    [Trait("Category", "Unit")]
    public async Task QueryAsync_TypeSgv_QueriesOnlySensorGlucoseRepo()
    {
        var sg = MakeSg(Now, 120);
        _sgRepo.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { sg });

        var result = await _sut.QueryAsync(new EntryQuery { Type = "sgv", Count = 10 });

        Assert.Single(result);
        Assert.Equal("sgv", result[0].Type);
        _mgRepo.Verify(r => r.GetAsync(
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        _calRepo.Verify(r => r.GetAsync(
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task QueryAsync_TypeMbg_QueriesOnlyMeterGlucoseRepo()
    {
        var mg = MakeMg(Now, 150);
        _mgRepo.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { mg });

        var result = await _sut.QueryAsync(new EntryQuery { Type = "mbg", Count = 10 });

        Assert.Single(result);
        Assert.Equal("mbg", result[0].Type);
        _sgRepo.Verify(r => r.GetAsync(
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        _calRepo.Verify(r => r.GetAsync(
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task QueryAsync_TypeCal_QueriesOnlyCalibrationRepo()
    {
        var cal = MakeCal(Now);
        _calRepo.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { cal });

        var result = await _sut.QueryAsync(new EntryQuery { Type = "cal", Count = 10 });

        Assert.Single(result);
        Assert.Equal("cal", result[0].Type);
        _sgRepo.Verify(r => r.GetAsync(
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        _mgRepo.Verify(r => r.GetAsync(
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task QueryAsync_NoTypeFilter_MergesAllThreeByTimestampDesc()
    {
        var sg = MakeSg(Now.AddMinutes(-1), 120);
        var mg = MakeMg(Now, 150);
        var cal = MakeCal(Now.AddMinutes(-2));

        _sgRepo.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), true, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { sg });
        _mgRepo.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { mg });
        _calRepo.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { cal });

        var result = await _sut.QueryAsync(new EntryQuery { Type = null, Count = 10 });

        Assert.Equal(3, result.Count);
        // Newest first: mg (Now), sg (Now-1m), cal (Now-2m)
        Assert.Equal("mbg", result[0].Type);
        Assert.Equal("sgv", result[1].Type);
        Assert.Equal("cal", result[2].Type);
    }

    #endregion

    #region QueryAsync — pagination

    [Fact]
    [Trait("Category", "Unit")]
    public async Task QueryAsync_SingleType_PushesLimitAndOffsetToRepo()
    {
        // Single-type queries push limit/offset directly to the database
        var entries = Enumerable.Range(0, 2)
            .Select(i => MakeSg(Now.AddMinutes(-i), 100 + i))
            .ToArray();

        _sgRepo.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                2, 1, true, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(entries);

        var result = await _sut.QueryAsync(new EntryQuery { Type = "sgv", Count = 2, Skip = 1 });

        Assert.Equal(2, result.Count);
        // Verify the repo was called with limit=count, offset=skip (not count+skip, 0)
        _sgRepo.Verify(r => r.GetAsync(
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            2, 1, true, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task QueryAsync_AllTypes_OverFetchesForMergePagination()
    {
        // Multi-type merge still needs count+skip from each repo
        var sg = MakeSg(Now, 120);
        var mg = MakeMg(Now.AddMinutes(-1), 150);
        var cal = MakeCal(Now.AddMinutes(-2));

        _sgRepo.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                3, 0, true, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { sg });
        _mgRepo.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                3, 0, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { mg });
        _calRepo.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                3, 0, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { cal });

        var result = await _sut.QueryAsync(new EntryQuery { Type = null, Count = 2, Skip = 1 });

        // Skip 1, take 2 from the merged 3
        Assert.Equal(2, result.Count);
        // Verify repos were called with fetchCount = count+skip = 3, offset = 0
        _sgRepo.Verify(r => r.GetAsync(
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            3, 0, true, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region GetCurrentAsync

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetCurrentAsync_ReturnsMostRecentSgv()
    {
        var sg = MakeSg(Now, 120);
        _sgRepo.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), 0, true, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { sg });

        var result = await _sut.GetCurrentAsync();

        Assert.NotNull(result);
        Assert.Equal("sgv", result.Type);
        Assert.Equal(120, result.Sgv);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetCurrentAsync_NoEntries_ReturnsNull()
    {
        _sgRepo.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), 0, true, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<SensorGlucose>());

        var result = await _sut.GetCurrentAsync();

        Assert.Null(result);
    }

    #endregion

    #region GetByIdAsync

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetByIdAsync_WithUuid_QueriesByPrimaryKey()
    {
        var id = Guid.NewGuid();
        var sg = MakeSg(Now, 120, id);
        _sgRepo.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(sg);

        var result = await _sut.GetByIdAsync(id.ToString());

        Assert.NotNull(result);
        Assert.Equal("sgv", result.Type);
        _sgRepo.Verify(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetByIdAsync_WithLegacyId_QueriesByLegacyId()
    {
        var legacyId = "507f1f77bcf86cd799439011";
        var sg = MakeSg(Now, 120, legacyId: legacyId);
        _sgRepo.Setup(r => r.GetByLegacyIdAsync(legacyId, It.IsAny<CancellationToken>())).ReturnsAsync(sg);

        var result = await _sut.GetByIdAsync(legacyId);

        Assert.NotNull(result);
        Assert.Equal("sgv", result.Type);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetByIdAsync_WithUuid_FallsThroughToMeterGlucose()
    {
        var id = Guid.NewGuid();
        _sgRepo.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync((SensorGlucose?)null);
        var mg = MakeMg(Now, 150, id);
        _mgRepo.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(mg);

        var result = await _sut.GetByIdAsync(id.ToString());

        Assert.NotNull(result);
        Assert.Equal("mbg", result.Type);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetByIdAsync_WithUuid_FallsThroughToCalibration()
    {
        var id = Guid.NewGuid();
        _sgRepo.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync((SensorGlucose?)null);
        _mgRepo.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync((MeterGlucose?)null);
        var cal = MakeCal(Now, id);
        _calRepo.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(cal);

        var result = await _sut.GetByIdAsync(id.ToString());

        Assert.NotNull(result);
        Assert.Equal("cal", result.Type);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetByIdAsync_NotFound_ReturnsNull()
    {
        var id = Guid.NewGuid();
        _sgRepo.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync((SensorGlucose?)null);
        _mgRepo.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync((MeterGlucose?)null);
        _calRepo.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync((Calibration?)null);

        var result = await _sut.GetByIdAsync(id.ToString());

        Assert.Null(result);
    }

    #endregion

    #region CheckDuplicateAsync

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckDuplicateAsync_MatchFound_ReturnsEntry()
    {
        var sg = MakeSg(Now, 120);
        _sgRepo.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), "xdrip", It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { sg });

        var result = await _sut.CheckDuplicateAsync("xdrip", "sgv", 120, sg.Mills, 5);

        Assert.NotNull(result);
        Assert.Equal("sgv", result.Type);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckDuplicateAsync_NoMatch_ReturnsNull()
    {
        _sgRepo.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), "xdrip", It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<SensorGlucose>());

        var mills = new DateTimeOffset(Now, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var result = await _sut.CheckDuplicateAsync("xdrip", "sgv", 120, mills, 5);

        Assert.Null(result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckDuplicateAsync_TypeMbg_QueriesMeterGlucoseRepo()
    {
        var mg = MakeMg(Now, 150);
        _mgRepo.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), "meter", It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { mg });

        var result = await _sut.CheckDuplicateAsync("meter", "mbg", 150, mg.Mills, 5);

        Assert.NotNull(result);
        Assert.Equal("mbg", result.Type);
    }

    #endregion

    #region QueryAsync — demo mode filtering

    [Fact]
    [Trait("Category", "Unit")]
    public async Task QueryAsync_DemoDisabled_ExcludesDemoSourcedEntries()
    {
        // Default setup has demo mode disabled
        var realSg = MakeSg(Now, 120, dataSource: "xdrip");
        var demoSg = MakeSg(Now.AddMinutes(-1), 100, dataSource: "demo-service");

        _sgRepo.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { realSg, demoSg });

        var result = await _sut.QueryAsync(new EntryQuery { Type = "sgv", Count = 10 });

        Assert.Single(result);
        Assert.Equal(120, result[0].Sgv);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task QueryAsync_DemoEnabled_ReturnsOnlyDemoEntries()
    {
        _demoMode.Setup(d => d.IsEnabled).Returns(true);
        var sut = new EntryReadService(
            _sgRepo.Object, _mgRepo.Object, _calRepo.Object,
            _demoMode.Object, Mock.Of<ILogger<EntryReadService>>());

        var demoSg = MakeSg(Now, 100, dataSource: "demo-service");

        _sgRepo.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), "demo-service",
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { demoSg });

        var result = await sut.QueryAsync(new EntryQuery { Type = "sgv", Count = 10 });

        Assert.Single(result);
        // Verify source=demo-service was passed to the repo
        _sgRepo.Verify(r => r.GetAsync(
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), "demo-service",
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetCurrentAsync_DemoDisabled_SkipsDemoEntries()
    {
        var demoSg = MakeSg(Now, 100, dataSource: "demo-service");
        var realSg = MakeSg(Now.AddMinutes(-1), 120, dataSource: "xdrip");

        _sgRepo.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), true, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { demoSg, realSg });

        var result = await _sut.GetCurrentAsync();

        Assert.NotNull(result);
        Assert.Equal(120, result.Sgv);
    }

    #endregion

    #region QueryAsync — DateString filter

    [Fact]
    [Trait("Category", "Unit")]
    public async Task QueryAsync_WithDateString_ParsesIntoTimestampFilter()
    {
        var sg = MakeSg(Now, 120);
        _sgRepo.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { sg });

        var result = await _sut.QueryAsync(new EntryQuery
        {
            Type = "sgv",
            DateString = "2025-01-15",
            Count = 10
        });

        Assert.Single(result);
    }

    #endregion

    #region QueryAsync — ReverseResults

    [Fact]
    [Trait("Category", "Unit")]
    public async Task QueryAsync_ReverseResults_PassesDescendingFalse()
    {
        _sgRepo.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), false, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<SensorGlucose>());

        await _sut.QueryAsync(new EntryQuery { Type = "sgv", ReverseResults = true, Count = 10 });

        _sgRepo.Verify(r => r.GetAsync(
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<int>(), It.IsAny<int>(), false, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region Helpers

    private static SensorGlucose MakeSg(DateTime ts, double mgdl, Guid? id = null, string? legacyId = null, string? dataSource = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Timestamp = ts,
        Mgdl = mgdl,
        Device = "test-device",
        LegacyId = legacyId,
        DataSource = dataSource,
        CreatedAt = ts,
        ModifiedAt = ts,
    };

    private static MeterGlucose MakeMg(DateTime ts, double mgdl, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Timestamp = ts,
        Mgdl = mgdl,
        Device = "test-meter",
        CreatedAt = ts,
        ModifiedAt = ts,
    };

    private static Calibration MakeCal(DateTime ts, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Timestamp = ts,
        Slope = 1000,
        Intercept = 25000,
        Scale = 1,
        Device = "test-cal",
        CreatedAt = ts,
        ModifiedAt = ts,
    };

    #endregion
}
