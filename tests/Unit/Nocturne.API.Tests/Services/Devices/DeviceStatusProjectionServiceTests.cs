using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Devices;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Devices;

public class DeviceStatusProjectionServiceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly Mock<IApsSnapshotRepository> _apsRepo = new();
    private readonly Mock<IPumpSnapshotRepository> _pumpRepo = new();
    private readonly Mock<IUploaderSnapshotRepository> _uploaderRepo = new();
    private readonly Mock<IStateSpanRepository> _stateSpanRepo = new();
    private readonly Mock<IDeviceStatusExtrasRepository> _extrasRepo = new();
    private readonly DeviceStatusProjectionService _service;

    private static readonly DateTime ReferenceTime = new(2024, 1, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly long ReferenceMillis = new DateTimeOffset(ReferenceTime).ToUnixTimeMilliseconds();

    public DeviceStatusProjectionServiceTests()
    {
        SetupDefaultEmptyReturns();

        _service = new DeviceStatusProjectionService(
            _apsRepo.Object,
            _pumpRepo.Object,
            _uploaderRepo.Object,
            _stateSpanRepo.Object,
            _extrasRepo.Object,
            NullLogger<DeviceStatusProjectionService>.Instance);
    }

    private void SetupDefaultEmptyReturns()
    {
        _apsRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<ApsSnapshot>());

        _pumpRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<PumpSnapshot>());

        _uploaderRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<UploaderSnapshot>());

        _pumpRepo
            .Setup(r => r.GetByCorrelationIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<PumpSnapshot>());

        _uploaderRepo
            .Setup(r => r.GetByCorrelationIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<UploaderSnapshot>());

        _extrasRepo
            .Setup(r => r.GetByCorrelationIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<DeviceStatusExtras>());

        _stateSpanRepo
            .Setup(r => r.GetByCategory(
                It.IsAny<StateSpanCategory>(),
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<StateSpan>());
    }

    #region ProjectFromSnapshots — OpenAPS

    [Fact]
    public void ProjectAsync_WithOpenApsSnapshot_ReassemblesOpenApsDeviceStatus()
    {
        // Arrange
        var suggested = new OpenApsSuggested
        {
            Bg = 120, EventualBG = 95, TargetBG = 100,
            InsulinReq = 0.5, SensitivityRatio = 1.1,
            Timestamp = "2024-01-15T12:00:00Z",
        };
        var enacted = new OpenApsEnacted
        {
            Bg = 120, Rate = 1.5, Duration = 30,
            Received = true,
        };

        var aps = CreateApsSnapshot(AidAlgorithm.OpenAps);
        aps.SuggestedJson = JsonSerializer.Serialize(suggested, JsonOptions);
        aps.EnactedJson = JsonSerializer.Serialize(enacted, JsonOptions);
        aps.Iob = 2.5;
        aps.BasalIob = 1.0;
        aps.BolusIob = 1.5;
        aps.Cob = 30.0;
        aps.AidVersion = "0.7.1";

        // Act
        var result = DeviceStatusProjectionService.ProjectFromSnapshots(aps, null, null, null, null);

        // Assert
        result.OpenAps.Should().NotBeNull();
        result.OpenAps!.Suggested.Should().NotBeNull();
        result.OpenAps.Suggested!.Bg.Should().Be(120);
        result.OpenAps.Enacted.Should().NotBeNull();
        result.OpenAps.Enacted!.Received.Should().BeTrue();
        result.OpenAps.Iob.Should().NotBeNull();
        result.OpenAps.Iob!.Iob.Should().Be(2.5);
        result.OpenAps.Iob.BasalIob.Should().Be(1.0);
        result.OpenAps.Iob.BolusIob.Should().Be(1.5);
        result.OpenAps.Cob.Should().Be(30.0);
        result.OpenAps.Version.Should().Be("0.7.1");
        result.Loop.Should().BeNull();
    }

    #endregion

    #region ProjectFromSnapshots — Loop

    [Fact]
    public void ProjectAsync_WithLoopSnapshot_ReassemblesLoopDeviceStatus()
    {
        // Arrange
        var loopStatus = new LoopStatus
        {
            Iob = new LoopIob { Iob = 1.2, BasalIob = 0.8 },
            Cob = new LoopCob { Cob = 15.0, Timestamp = "2024-01-15T12:00:00Z" },
            RecommendedBolus = 0.3,
            Version = "3.4.1",
            Name = "Loop",
            Predicted = new LoopPredicted
            {
                Values = new double[] { 120, 115, 110 },
                StartDate = "2024-01-15T12:00:00Z",
            },
        };

        var aps = CreateApsSnapshot(AidAlgorithm.Loop);
        aps.LoopJson = JsonSerializer.Serialize(loopStatus, JsonOptions);
        aps.Iob = 1.2;
        aps.BasalIob = 0.8;
        aps.Cob = 15.0;

        // Act
        var result = DeviceStatusProjectionService.ProjectFromSnapshots(aps, null, null, null, null);

        // Assert
        result.Loop.Should().NotBeNull();
        result.Loop!.Iob.Should().NotBeNull();
        result.Loop.Iob!.Iob.Should().Be(1.2);
        result.Loop.Cob.Should().NotBeNull();
        result.Loop.Cob!.Cob.Should().Be(15.0);
        result.Loop.Version.Should().Be("3.4.1");
        result.Loop.Predicted.Should().NotBeNull();
        result.Loop.Predicted!.Values.Should().HaveCount(3);
        result.OpenAps.Should().BeNull();
    }

    #endregion

    #region ProjectFromSnapshots — Pump

    [Fact]
    public void ProjectAsync_WithPumpSnapshot_ReassemblesPumpObject()
    {
        // Arrange
        var pump = CreatePumpSnapshot();
        pump.Manufacturer = "Insulet";
        pump.Model = "Omnipod DASH";
        pump.Reservoir = 85.5;
        pump.ReservoirDisplay = "85 U";
        pump.BatteryPercent = 72;
        pump.BatteryVoltage = 1.35;
        pump.Bolusing = false;
        pump.Suspended = false;
        pump.PumpStatus = "normal";
        pump.Clock = "2024-01-15T12:00:00Z";
        pump.Iob = 3.2;
        pump.BolusIob = 2.1;
        pump.AdditionalProperties = new Dictionary<string, object?>
        {
            ["pumpSerial"] = "12345",
            ["firmware"] = "v2.0",
        };

        // Act
        var result = DeviceStatusProjectionService.ProjectFromSnapshots(null, pump, null, null, null);

        // Assert
        result.Pump.Should().NotBeNull();
        result.Pump!.Manufacturer.Should().Be("Insulet");
        result.Pump.Model.Should().Be("Omnipod DASH");
        result.Pump.Reservoir.Should().Be(85.5);
        result.Pump.ReservoirDisplayOverride.Should().Be("85 U");
        result.Pump.Battery.Should().NotBeNull();
        result.Pump.Battery!.Percent.Should().Be(72);
        result.Pump.Battery.Voltage.Should().Be(1.35);
        result.Pump.Status.Should().NotBeNull();
        result.Pump.Status!.Bolusing.Should().BeFalse();
        result.Pump.Status.Suspended.Should().BeFalse();
        result.Pump.Status.Status.Should().Be("normal");
        result.Pump.Clock.Should().Be("2024-01-15T12:00:00Z");
        result.Pump.Iob.Should().NotBeNull();
        result.Pump.Iob!.Iob.Should().Be(3.2);
        result.Pump.Iob.BolusIob.Should().Be(2.1);
        result.Pump.Extended.Should().NotBeNull();
        result.Pump.Extended!["pumpSerial"].ToString().Should().Be("12345");
        result.Pump.Extended["firmware"].ToString().Should().Be("v2.0");
    }

    #endregion

    #region ProjectFromSnapshots — Uploader

    [Fact]
    public void ProjectAsync_WithUploaderSnapshot_ReassemblesUploaderObject()
    {
        // Arrange
        var uploader = CreateUploaderSnapshot();
        uploader.Battery = 85;
        uploader.BatteryVoltage = 4.1;
        uploader.Temperature = 32.5;
        uploader.Name = "Pixel 7";
        uploader.Type = "phone";
        uploader.IsCharging = true;

        var aps = CreateApsSnapshot(AidAlgorithm.OpenAps);
        aps.SuggestedJson = JsonSerializer.Serialize(new OpenApsSuggested { Bg = 120 }, JsonOptions);

        // Act
        var result = DeviceStatusProjectionService.ProjectFromSnapshots(aps, null, uploader, null, null);

        // Assert
        result.Uploader.Should().NotBeNull();
        result.Uploader!.Battery.Should().Be(85);
        result.Uploader.BatteryVoltage.Should().Be(4.1);
        result.Uploader.Temperature.Should().Be(32.5);
        result.Uploader.Name.Should().Be("Pixel 7");
        result.Uploader.Type.Should().Be("phone");
        result.IsCharging.Should().BeTrue();
        result.UploaderBattery.Should().Be(85);
    }

    #endregion

    #region ProjectFromSnapshots — Override

    [Fact]
    public void ProjectAsync_WithOverrideStateSpan_ReassemblesOverrideObject()
    {
        // Arrange
        var aps = CreateApsSnapshot(AidAlgorithm.Loop);
        aps.LoopJson = JsonSerializer.Serialize(new LoopStatus { Version = "3.0" }, JsonOptions);

        var overrideSpan = new StateSpan
        {
            Category = StateSpanCategory.Override,
            State = "Custom",
            StartTimestamp = ReferenceTime,
            EndTimestamp = ReferenceTime.AddMinutes(60),
            Metadata = new Dictionary<string, object>
            {
                ["name"] = "Exercise",
                ["multiplier"] = 0.8,
                ["currentCorrectionRange.minValue"] = 140.0,
                ["currentCorrectionRange.maxValue"] = 160.0,
            },
        };

        // Act
        var result = DeviceStatusProjectionService.ProjectFromSnapshots(aps, null, null, overrideSpan, null);

        // Assert
        result.Override.Should().NotBeNull();
        result.Override!.Name.Should().Be("Exercise");
        result.Override.Active.Should().BeFalse(); // Has end timestamp, so not active
        result.Override.Multiplier.Should().Be(0.8);
        result.Override.Duration.Should().Be(60);
        result.Override.CurrentCorrectionRange.Should().NotBeNull();
        result.Override.CurrentCorrectionRange!.MinValue.Should().Be(140.0);
        result.Override.CurrentCorrectionRange.MaxValue.Should().Be(160.0);
    }

    #endregion

    #region ProjectFromSnapshots — Extras

    [Fact]
    public void ProjectAsync_WithExtras_SplatsOntoDocument()
    {
        // Arrange
        var aps = CreateApsSnapshot(AidAlgorithm.OpenAps);
        aps.SuggestedJson = JsonSerializer.Serialize(new OpenApsSuggested { Bg = 120 }, JsonOptions);

        var extras = new DeviceStatusExtras
        {
            Id = Guid.NewGuid(),
            CorrelationId = aps.CorrelationId!.Value,
            Timestamp = ReferenceTime,
            Extras = new Dictionary<string, object?>
            {
                ["xdripjs"] = JsonSerializer.SerializeToElement(new { state = 6, stateString = "OK" }, JsonOptions),
                ["configuration"] = JsonSerializer.SerializeToElement(new { units = "mg/dl" }, JsonOptions),
            },
        };

        // Act
        var result = DeviceStatusProjectionService.ProjectFromSnapshots(aps, null, null, null, extras);

        // Assert
        result.XDripJs.Should().NotBeNull();
        result.XDripJs!.State.Should().Be(6);
        result.XDripJs.StateString.Should().Be("OK");
        result.ExtensionData.Should().NotBeNull();
        result.ExtensionData.Should().ContainKey("configuration");
    }

    #endregion

    #region Correlation

    [Fact]
    public async Task ProjectAsync_CorrelatesByCorrelationId()
    {
        // Arrange
        var correlationId = Guid.NewGuid();
        var aps = CreateApsSnapshot(AidAlgorithm.OpenAps);
        aps.CorrelationId = correlationId;
        aps.SuggestedJson = JsonSerializer.Serialize(new OpenApsSuggested { Bg = 120 }, JsonOptions);

        var pump = CreatePumpSnapshot();
        pump.CorrelationId = correlationId;
        pump.Reservoir = 50.0;

        var uploader = CreateUploaderSnapshot();
        uploader.CorrelationId = correlationId;
        uploader.Battery = 80;

        _apsRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { aps });

        _pumpRepo
            .Setup(r => r.GetByCorrelationIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { pump });

        _uploaderRepo
            .Setup(r => r.GetByCorrelationIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { uploader });

        // Act
        var results = (await _service.GetAsync(10, 0, null, CancellationToken.None)).ToList();

        // Assert
        results.Should().HaveCount(1);
        var ds = results[0];
        ds.OpenAps.Should().NotBeNull();
        ds.Pump.Should().NotBeNull();
        ds.Pump!.Reservoir.Should().Be(50.0);
        ds.Uploader.Should().NotBeNull();
        ds.Uploader!.Battery.Should().Be(80);
    }

    #endregion

    #region Orphan Pump (xDrip+)

    [Fact]
    public async Task ProjectAsync_OrphanPumpSnapshot_ReturnsDeviceStatusWithPumpOnly()
    {
        // Arrange — no APS snapshots, only an orphan pump
        var pump = CreatePumpSnapshot();
        pump.CorrelationId = null; // No correlation = orphan
        pump.Reservoir = 42.0;
        pump.Manufacturer = "Medtronic";

        _pumpRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { pump });

        // Act
        var results = (await _service.GetAsync(10, 0, null, CancellationToken.None)).ToList();

        // Assert
        results.Should().HaveCount(1);
        var ds = results[0];
        ds.OpenAps.Should().BeNull();
        ds.Loop.Should().BeNull();
        ds.Pump.Should().NotBeNull();
        ds.Pump!.Reservoir.Should().Be(42.0);
        ds.Pump.Manufacturer.Should().Be("Medtronic");
    }

    #endregion

    #region GetAsync — Pagination

    [Fact]
    public async Task GetAsync_WithPagination_ReturnsPagedResults()
    {
        // Arrange — 3 APS snapshots, request page of 2
        var snapshots = Enumerable.Range(0, 2).Select(i =>
        {
            var s = CreateApsSnapshot(AidAlgorithm.OpenAps);
            s.Timestamp = ReferenceTime.AddMinutes(-i);
            s.SuggestedJson = JsonSerializer.Serialize(new OpenApsSuggested { Bg = 120 + i }, JsonOptions);
            return s;
        }).ToList();

        _apsRepo
            .Setup(r => r.GetAsync(null, null, null, null, 2, 1, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshots);

        // Act
        var results = (await _service.GetAsync(2, 1, null, CancellationToken.None)).ToList();

        // Assert
        results.Should().HaveCount(2);
        _apsRepo.Verify(r => r.GetAsync(null, null, null, null, 2, 1, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region GetAsync — Time Range

    [Fact]
    public async Task GetAsync_WithTimeRange_FiltersCorrectly()
    {
        // Arrange — the repo returns filtered results
        var snapshot = CreateApsSnapshot(AidAlgorithm.AndroidAps);
        snapshot.SuggestedJson = JsonSerializer.Serialize(new OpenApsSuggested { Bg = 130 }, JsonOptions);

        _apsRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { snapshot });

        // Act
        var results = (await _service.GetAsync(10, 0, null, CancellationToken.None)).ToList();

        // Assert
        results.Should().HaveCount(1);
        results[0].OpenAps.Should().NotBeNull();
    }

    #endregion

    #region GetModifiedSinceAsync

    [Fact]
    public async Task GetModifiedSinceAsync_ReturnsModifiedRecords()
    {
        // Arrange
        var correlationId = Guid.NewGuid();
        var aps = CreateApsSnapshot(AidAlgorithm.AndroidAps);
        aps.CorrelationId = correlationId;
        aps.ModifiedAt = ReferenceTime.AddMinutes(5);
        aps.SuggestedJson = JsonSerializer.Serialize(new OpenApsSuggested { Bg = 140 }, JsonOptions);

        var pump = CreatePumpSnapshot();
        pump.CorrelationId = correlationId;
        pump.Reservoir = 60.0;

        _apsRepo
            .Setup(r => r.GetModifiedSinceAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { aps });

        _pumpRepo
            .Setup(r => r.GetByCorrelationIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { pump });

        // Act
        var results = (await _service.GetModifiedSinceAsync(ReferenceMillis, 100, CancellationToken.None)).ToList();

        // Assert
        results.Should().HaveCount(1);
        results[0].OpenAps.Should().NotBeNull();
        results[0].Pump.Should().NotBeNull();
        results[0].Pump!.Reservoir.Should().Be(60.0);
    }

    #endregion

    #region GetByIdAsync — UUID

    [Fact]
    public async Task GetByIdAsync_WithUuid_QueriesByPrimaryKey()
    {
        // Arrange
        var id = Guid.NewGuid();
        var aps = CreateApsSnapshot(AidAlgorithm.OpenAps);
        aps.Id = id;
        aps.CorrelationId = null;
        aps.SuggestedJson = JsonSerializer.Serialize(new OpenApsSuggested { Bg = 110 }, JsonOptions);

        _apsRepo
            .Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(aps);

        // Act
        var result = await _service.GetByIdAsync(id.ToString(), CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.OpenAps.Should().NotBeNull();
        _apsRepo.Verify(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region GetByIdAsync — LegacyId

    [Fact]
    public async Task GetByIdAsync_WithLegacyId_QueriesByLegacyId()
    {
        // Arrange
        var legacyId = "5f1e2d3c4b5a6978";
        var aps = CreateApsSnapshot(AidAlgorithm.AndroidAps);
        aps.LegacyId = legacyId;
        aps.CorrelationId = null;
        aps.SuggestedJson = JsonSerializer.Serialize(new OpenApsSuggested { Bg = 115 }, JsonOptions);

        // UUID parse fails for this legacy ID, so fallback to legacy lookup
        _apsRepo
            .Setup(r => r.GetByLegacyIdAsync(legacyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(aps);

        // Act
        var result = await _service.GetByIdAsync(legacyId, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(legacyId);
        result.OpenAps.Should().NotBeNull();
        _apsRepo.Verify(r => r.GetByLegacyIdAsync(legacyId, It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region ParseFindQuery

    [Fact]
    public void ParseFindQuery_WithNull_ReturnsEmpty()
    {
        var (device, from, to) = DeviceStatusProjectionService.ParseFindQuery(null);

        device.Should().BeNull();
        from.Should().BeNull();
        to.Should().BeNull();
    }

    [Fact]
    public void ParseFindQuery_WithEmptyString_ReturnsEmpty()
    {
        var (device, from, to) = DeviceStatusProjectionService.ParseFindQuery("");

        device.Should().BeNull();
        from.Should().BeNull();
        to.Should().BeNull();
    }

    [Fact]
    public void ParseFindQuery_WithDevice_ExtractsDevice()
    {
        var (device, from, to) = DeviceStatusProjectionService.ParseFindQuery(
            "find[device]=openaps://rpi");

        device.Should().Be("openaps://rpi");
        from.Should().BeNull();
        to.Should().BeNull();
    }

    [Fact]
    public void ParseFindQuery_WithCreatedAtGte_ExtractsFrom()
    {
        var (device, from, to) = DeviceStatusProjectionService.ParseFindQuery(
            "find[created_at][$gte]=2024-01-15T00:00:00Z");

        device.Should().BeNull();
        from.Should().Be(new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc));
        to.Should().BeNull();
    }

    [Fact]
    public void ParseFindQuery_WithCreatedAtRange_ExtractsFromAndTo()
    {
        var (device, from, to) = DeviceStatusProjectionService.ParseFindQuery(
            "find[created_at][$gte]=2024-01-15T00:00:00Z&find[created_at][$lt]=2024-01-16T00:00:00Z");

        device.Should().BeNull();
        from.Should().Be(new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc));
        to.Should().Be(new DateTime(2024, 1, 16, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void ParseFindQuery_WithJsonDevice_ExtractsDevice()
    {
        var (device, from, to) = DeviceStatusProjectionService.ParseFindQuery(
            """{"device":"loop://iPhone"}""");

        device.Should().Be("loop://iPhone");
        from.Should().BeNull();
        to.Should().BeNull();
    }

    [Fact]
    public void ParseFindQuery_WithJsonCreatedAtRange_ExtractsFromAndTo()
    {
        var (device, from, to) = DeviceStatusProjectionService.ParseFindQuery(
            """{"created_at":{"$gte":"2024-01-15T00:00:00Z","$lt":"2024-01-16T00:00:00Z"}}""");

        device.Should().BeNull();
        from.Should().Be(new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc));
        to.Should().Be(new DateTime(2024, 1, 16, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void ParseFindQuery_WithDeviceAndDateRange_ExtractsAll()
    {
        var (device, from, to) = DeviceStatusProjectionService.ParseFindQuery(
            "find[device]=openaps://rpi&find[created_at][$gte]=2024-01-15T00:00:00Z&find[created_at][$lt]=2024-01-16T00:00:00Z");

        device.Should().Be("openaps://rpi");
        from.Should().Be(new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc));
        to.Should().Be(new DateTime(2024, 1, 16, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void ParseFindQuery_WithMalformedJson_ReturnsEmpty()
    {
        var (device, from, to) = DeviceStatusProjectionService.ParseFindQuery("{invalid json");

        device.Should().BeNull();
        from.Should().BeNull();
        to.Should().BeNull();
    }

    #endregion

    #region CountAsync

    [Fact]
    public async Task CountAsync_WithNoFilter_ReturnsSumOfApsAndOrphanPump()
    {
        _apsRepo
            .Setup(r => r.CountAsync(null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(10);
        _pumpRepo
            .Setup(r => r.CountAsync(null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(12);

        var count = await _service.CountAsync(null, CancellationToken.None);

        // 10 APS + max(0, 12 - 10) orphan pumps = 12
        count.Should().Be(12);
    }

    [Fact]
    public async Task CountAsync_WithDateFilter_PassesDatesToRepos()
    {
        _apsRepo
            .Setup(r => r.CountAsync(
                It.Is<DateTime?>(d => d.HasValue),
                It.Is<DateTime?>(d => d.HasValue),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(5);
        _pumpRepo
            .Setup(r => r.CountAsync(
                It.Is<DateTime?>(d => d.HasValue),
                It.Is<DateTime?>(d => d.HasValue),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);

        var count = await _service.CountAsync(
            "find[created_at][$gte]=2024-01-15T00:00:00Z&find[created_at][$lt]=2024-01-16T00:00:00Z",
            CancellationToken.None);

        // 5 APS + max(0, 3 - 5) orphan pumps = 5
        count.Should().Be(5);
    }

    [Fact]
    public async Task CountAsync_WhenPumpsExceedAps_IncludesOrphanEstimate()
    {
        _apsRepo
            .Setup(r => r.CountAsync(null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);
        _pumpRepo
            .Setup(r => r.CountAsync(null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(8);

        var count = await _service.CountAsync(null, CancellationToken.None);

        // 3 APS + max(0, 8 - 3) orphan pumps = 8
        count.Should().Be(8);
    }

    #endregion

    #region GetAsync — Find Filtering

    [Fact]
    public async Task GetAsync_WithDeviceFilter_PassesDeviceToRepository()
    {
        var aps = CreateApsSnapshot(AidAlgorithm.OpenAps);
        aps.SuggestedJson = JsonSerializer.Serialize(new OpenApsSuggested { Bg = 120 }, JsonOptions);

        _apsRepo
            .Setup(r => r.GetAsync(null, null, "openaps://rpi", null,
                It.IsAny<int>(), It.IsAny<int>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { aps });

        var results = (await _service.GetAsync(10, 0, "find[device]=openaps://rpi", CancellationToken.None)).ToList();

        results.Should().HaveCount(1);
        _apsRepo.Verify(r => r.GetAsync(null, null, "openaps://rpi", null,
            10, 0, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetAsync_WithDateRangeFilter_PassesDatesToRepository()
    {
        var aps = CreateApsSnapshot(AidAlgorithm.OpenAps);
        aps.SuggestedJson = JsonSerializer.Serialize(new OpenApsSuggested { Bg = 120 }, JsonOptions);

        var expectedFrom = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var expectedTo = new DateTime(2024, 1, 16, 0, 0, 0, DateTimeKind.Utc);

        _apsRepo
            .Setup(r => r.GetAsync(expectedFrom, expectedTo, null, null,
                It.IsAny<int>(), It.IsAny<int>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { aps });

        var results = (await _service.GetAsync(10, 0,
            "find[created_at][$gte]=2024-01-15T00:00:00Z&find[created_at][$lt]=2024-01-16T00:00:00Z",
            CancellationToken.None)).ToList();

        results.Should().HaveCount(1);
        _apsRepo.Verify(r => r.GetAsync(expectedFrom, expectedTo, null, null,
            10, 0, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region Helpers

    private static ApsSnapshot CreateApsSnapshot(AidAlgorithm algorithm)
    {
        return new ApsSnapshot
        {
            Id = Guid.NewGuid(),
            Timestamp = ReferenceTime,
            UtcOffset = 0,
            Device = "test-device",
            CorrelationId = Guid.NewGuid(),
            LegacyId = "legacy-" + Guid.NewGuid().ToString("N")[..8],
            CreatedAt = ReferenceTime,
            ModifiedAt = ReferenceTime,
            AidAlgorithm = algorithm,
        };
    }

    private static PumpSnapshot CreatePumpSnapshot()
    {
        return new PumpSnapshot
        {
            Id = Guid.NewGuid(),
            Timestamp = ReferenceTime,
            UtcOffset = 0,
            Device = "test-device",
            CorrelationId = Guid.NewGuid(),
            LegacyId = "legacy-" + Guid.NewGuid().ToString("N")[..8],
            CreatedAt = ReferenceTime,
            ModifiedAt = ReferenceTime,
        };
    }

    private static UploaderSnapshot CreateUploaderSnapshot()
    {
        return new UploaderSnapshot
        {
            Id = Guid.NewGuid(),
            Timestamp = ReferenceTime,
            UtcOffset = 0,
            Device = "test-device",
            CorrelationId = Guid.NewGuid(),
            LegacyId = "legacy-" + Guid.NewGuid().ToString("N")[..8],
            CreatedAt = ReferenceTime,
            ModifiedAt = ReferenceTime,
        };
    }

    #endregion
}
