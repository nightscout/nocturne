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
            TestDoubles.CanonicalGlucosePassThrough.Create(),
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
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
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
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
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
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
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
                It.IsAny<int>(), It.IsAny<int>(), true, false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
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
    public async Task QueryAsync_SingleType_OverFetchesForCanonicalSelectionThenPages()
    {
        // Canonical stream selection runs after the DB query, so sgv fetches over-fetch
        // (count+skip)×3 from offset 0 and page in memory.
        var entries = Enumerable.Range(0, 4)
            .Select(i => MakeSg(Now.AddMinutes(-i), 100 + i))
            .ToArray();

        _sgRepo.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                9, 0, true, false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entries);

        var result = await _sut.QueryAsync(new EntryQuery { Type = "sgv", Count = 2, Skip = 1 });

        Assert.Equal(2, result.Count);
        // Page starts after the skipped newest reading
        Assert.Equal(entries[1].Mgdl, result[0].Sgv);
        _sgRepo.Verify(r => r.GetAsync(
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            9, 0, true, false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task QueryAsync_AllTypes_OverFetchesForMergePagination()
    {
        // Multi-type merge needs count+skip from each repo (sgv over-fetches ×3 on top for
        // canonical selection)
        var sg = MakeSg(Now, 120);
        var mg = MakeMg(Now.AddMinutes(-1), 150);
        var cal = MakeCal(Now.AddMinutes(-2));

        _sgRepo.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                9, 0, true, false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
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
        _sgRepo.Verify(r => r.GetAsync(
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            9, 0, true, false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
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
                It.IsAny<int>(), 0, true, false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
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
                It.IsAny<int>(), 0, true, false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
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
        _sgRepo.Setup(r => r.FindStoredDuplicateAsync(
                "xdrip", 120, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(sg);

        var result = await _sut.CheckDuplicateAsync("xdrip", "sgv", 120, sg.Mills, 5);

        Assert.NotNull(result);
        Assert.Equal("sgv", result.Type);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckDuplicateAsync_NoMatch_ReturnsNull()
    {
        _sgRepo.Setup(r => r.FindStoredDuplicateAsync(
                "xdrip", 120, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SensorGlucose?)null);

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

    #region CheckDuplicatesAsync — one query per batch

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckDuplicatesAsync_ContiguousBatch_ProbesStorageOnce()
    {
        StubNoSgvCandidates();
        var probes = FiveMinutelyProbes(200, "xdrip");

        var results = await _sut.CheckDuplicatesAsync(probes);

        Assert.Equal(200, results.Count);
        Assert.All(results, Assert.Null);
        _sgRepo.Verify(r => r.FindStoredDuplicateCandidatesAsync(
            It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Once);
        _sgRepo.Verify(r => r.FindStoredDuplicateAsync(
            It.IsAny<string?>(), It.IsAny<double?>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckDuplicatesAsync_ProbesFarApart_QueriesEachClusterSeparately()
    {
        // Two entries a week apart must not make one query read a week of readings.
        StubNoSgvCandidates();
        var mills = new DateTimeOffset(Now, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var probes = new[]
        {
            new EntryDuplicateProbe("xdrip", "sgv", 120, mills),
            new EntryDuplicateProbe("xdrip", "sgv", 130, mills + (long)TimeSpan.FromDays(7).TotalMilliseconds),
        };

        await _sut.CheckDuplicatesAsync(probes);

        _sgRepo.Verify(r => r.FindStoredDuplicateCandidatesAsync(
            It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckDuplicatesAsync_QueryWindowCoversEveryProbesOwnWindow()
    {
        var captured = new List<(DateTime From, DateTime To)>();
        _sgRepo.Setup(r => r.FindStoredDuplicateCandidatesAsync(
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyCollection<string>?, DateTime, DateTime, CancellationToken>(
                (_, from, to, _) => captured.Add((from, to)))
            .ReturnsAsync(Array.Empty<SensorGlucose>());

        var probes = FiveMinutelyProbes(10, "xdrip");

        await _sut.CheckDuplicatesAsync(probes, windowMinutes: 5);

        var window = TimeSpan.FromMinutes(5);
        Assert.All(probes, probe =>
        {
            var at = DateTimeOffset.FromUnixTimeMilliseconds(probe.Mills).UtcDateTime;
            Assert.Contains(captured, w => w.From <= at - window && w.To >= at + window);
        });
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckDuplicatesAsync_AllProbesShareADevice_FiltersToThatDevice()
    {
        IReadOnlyCollection<string>? devices = null;
        _sgRepo.Setup(r => r.FindStoredDuplicateCandidatesAsync(
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyCollection<string>?, DateTime, DateTime, CancellationToken>(
                (d, _, _, _) => devices = d)
            .ReturnsAsync(Array.Empty<SensorGlucose>());

        await _sut.CheckDuplicatesAsync(FiveMinutelyProbes(5, "xdrip"));

        Assert.NotNull(devices);
        Assert.Equal(["xdrip"], devices);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckDuplicatesAsync_AnyProbeWithoutADevice_FetchesEveryDevice()
    {
        // A probe with no device matches a stored reading from any device, so the fetch cannot
        // be narrowed to the devices the other probes named.
        IReadOnlyCollection<string>? devices = new[] { "sentinel" };
        _sgRepo.Setup(r => r.FindStoredDuplicateCandidatesAsync(
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyCollection<string>?, DateTime, DateTime, CancellationToken>(
                (d, _, _, _) => devices = d)
            .ReturnsAsync(Array.Empty<SensorGlucose>());

        var mills = new DateTimeOffset(Now, TimeSpan.Zero).ToUnixTimeMilliseconds();
        await _sut.CheckDuplicatesAsync(new[]
        {
            new EntryDuplicateProbe("xdrip", "sgv", 120, mills),
            new EntryDuplicateProbe(null, "sgv", 130, mills + 300_000),
        });

        Assert.Null(devices);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckDuplicatesAsync_UnknownType_IsNeverADuplicateAndCostsNoQuery()
    {
        var mills = new DateTimeOffset(Now, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var results = await _sut.CheckDuplicatesAsync(
            [new EntryDuplicateProbe("xdrip", "food", null, mills)]);

        Assert.Null(Assert.Single(results));
        _sgRepo.VerifyNoOtherCalls();
        _mgRepo.VerifyNoOtherCalls();
        _calRepo.VerifyNoOtherCalls();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckDuplicatesAsync_ResultsAlignWithSubmissionOrder()
    {
        var mills = new DateTimeOffset(Now, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var stored = MakeSg(Now.AddMinutes(-30), 99);
        _sgRepo.Setup(r => r.FindStoredDuplicateCandidatesAsync(
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([stored]);

        // Submitted newest-first, so the stored reading answers the *last* probe: chunking sorts
        // by time internally and must not reorder the results.
        var results = await _sut.CheckDuplicatesAsync(new[]
        {
            new EntryDuplicateProbe("test-device", "sgv", 120, mills),
            new EntryDuplicateProbe("test-device", "sgv", 99, stored.Mills),
        });

        Assert.Null(results[0]);
        Assert.NotNull(results[1]);
        Assert.Equal(stored.Mills, results[1]!.Mills);
    }

    private void StubNoSgvCandidates() =>
        _sgRepo.Setup(r => r.FindStoredDuplicateCandidatesAsync(
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SensorGlucose>());

    private static EntryDuplicateProbe[] FiveMinutelyProbes(int count, string? device)
    {
        var start = new DateTimeOffset(Now, TimeSpan.Zero).ToUnixTimeMilliseconds();
        return Enumerable.Range(0, count)
            .Select(i => new EntryDuplicateProbe(device, "sgv", 100 + (i % 50), start + (i * 300_000L)))
            .ToArray();
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
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
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
            TestDoubles.CanonicalGlucosePassThrough.Create(),
            _demoMode.Object, Mock.Of<ILogger<EntryReadService>>());

        var demoSg = MakeSg(Now, 100, dataSource: "demo-service");

        _sgRepo.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), "demo-service",
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { demoSg });

        var result = await sut.QueryAsync(new EntryQuery { Type = "sgv", Count = 10 });

        Assert.Single(result);
        // Verify source=demo-service was passed to the repo
        _sgRepo.Verify(r => r.GetAsync(
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), "demo-service",
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetCurrentAsync_DemoDisabled_SkipsDemoEntries()
    {
        var demoSg = MakeSg(Now, 100, dataSource: "demo-service");
        var realSg = MakeSg(Now.AddMinutes(-1), 120, dataSource: "xdrip");

        _sgRepo.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), true, false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
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
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
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
                It.IsAny<int>(), It.IsAny<int>(), false, false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<SensorGlucose>());

        await _sut.QueryAsync(new EntryQuery { Type = "sgv", ReverseResults = true, Count = 10 });

        _sgRepo.Verify(r => r.GetAsync(
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<int>(), It.IsAny<int>(), false, false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
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
