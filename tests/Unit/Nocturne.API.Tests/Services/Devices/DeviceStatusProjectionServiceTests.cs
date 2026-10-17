using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Devices;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Queries;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Devices;

public class DeviceStatusProjectionServiceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly Mock<IDeviceStatusGroupRepository> _groupRepo = new();
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
            _groupRepo.Object,
            _apsRepo.Object,
            _pumpRepo.Object,
            _uploaderRepo.Object,
            _stateSpanRepo.Object,
            _extrasRepo.Object,
            NullLogger<DeviceStatusProjectionService>.Instance);
    }

    private void SetupDefaultEmptyReturns()
    {
        _groupRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<DeviceStatusGroup>());

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
        result.Override.Duration.Should().Be(3600);
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

    [Fact]
    public void ProjectAsync_WithClientSrvTimestampsInExtras_ReportsServerClockAndDoesNotReEmit()
    {
        // The record reports the server clock, so client-supplied srvCreated/srvModified must not
        // override it. Re-emitting the extras would also serialize each key twice, which strict
        // client parsers reject.
        var aps = CreateApsSnapshot(AidAlgorithm.OpenAps);
        aps.CreatedAt = ReferenceTime.AddMinutes(3);
        aps.ModifiedAt = ReferenceTime.AddMinutes(5);
        aps.SuggestedJson = JsonSerializer.Serialize(new OpenApsSuggested { Bg = 120 }, JsonOptions);

        var extras = new DeviceStatusExtras
        {
            Id = Guid.NewGuid(),
            CorrelationId = aps.CorrelationId!.Value,
            Timestamp = ReferenceTime,
            Extras = new Dictionary<string, object?>
            {
                ["srvModified"] = JsonSerializer.SerializeToElement(1_722_945_600_000L, JsonOptions),
                ["srvCreated"] = JsonSerializer.SerializeToElement(1_722_945_500_000L, JsonOptions),
            },
        };

        var result = DeviceStatusProjectionService.ProjectFromSnapshots(aps, null, null, null, extras);

        result.SrvModified.Should().Be(Mills(aps.ModifiedAt));
        result.SrvCreated.Should().Be(Mills(aps.CreatedAt));
        result.ExtensionData.Should().NotContainKey("srvModified");
        result.ExtensionData.Should().NotContainKey("srvCreated");
    }

    [Fact]
    public void ProjectAsync_WrittenLongAfterTheEvent_ReportsCreatedAtAsTheEventTime()
    {
        var aps = CreateApsSnapshot(AidAlgorithm.OpenAps);
        aps.CreatedAt = ReferenceTime.AddDays(400);
        aps.ModifiedAt = ReferenceTime.AddDays(400);
        var pump = CreatePumpSnapshot();
        pump.CreatedAt = ReferenceTime.AddDays(30);

        var fromAps = DeviceStatusProjectionService.ProjectFromSnapshots(aps, null, null, null, null);
        fromAps.CreatedAt.Should().Be("2024-01-15T12:00:00.000Z");
        fromAps.Mills.Should().Be(ReferenceMillis);
        fromAps.SrvCreated.Should().Be(Mills(aps.CreatedAt));

        DeviceStatusProjectionService.ProjectFromSnapshots(null, pump, null, null, null)
            .CreatedAt.Should().Be("2024-01-15T12:00:00.000Z");
    }

    [Fact]
    public void ProjectAsync_WithoutApsSnapshot_ReportsAnchorServerClock()
    {
        // Orphan pump/uploader records (xDrip+) have no APS anchor; the server clock comes from the
        // same anchor the timestamp uses, in the same precedence.
        var pump = CreatePumpSnapshot();
        pump.CreatedAt = ReferenceTime.AddMinutes(1);
        pump.ModifiedAt = ReferenceTime.AddMinutes(2);
        var uploader = CreateUploaderSnapshot();
        uploader.CreatedAt = ReferenceTime.AddMinutes(8);
        uploader.ModifiedAt = ReferenceTime.AddMinutes(9);

        var fromPump = DeviceStatusProjectionService.ProjectFromSnapshots(null, pump, uploader, null, null);
        fromPump.SrvCreated.Should().Be(Mills(pump.CreatedAt));
        fromPump.SrvModified.Should().Be(Mills(pump.ModifiedAt));

        var fromUploader = DeviceStatusProjectionService.ProjectFromSnapshots(null, null, uploader, null, null);
        fromUploader.SrvCreated.Should().Be(Mills(uploader.CreatedAt));
        fromUploader.SrvModified.Should().Be(Mills(uploader.ModifiedAt));
    }

    [Fact]
    public void ProjectAsync_WithOnlyExtras_IsIdentifiedByTheExtrasRow()
    {
        var extras = new DeviceStatusExtras
        {
            Id = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            Timestamp = ReferenceTime,
            CreatedAt = ReferenceTime.AddMinutes(1),
            ModifiedAt = ReferenceTime.AddMinutes(2),
            Extras = new Dictionary<string, object?> { ["xdripjs"] = new { state = 6 } },
        };

        var ds = DeviceStatusProjectionService.ProjectFromSnapshots(null, null, null, null, extras);

        ds.Id.Should().Be(extras.Id.ToString());
        ds.Mills.Should().Be(ReferenceMillis);
        ds.CreatedAt.Should().Be("2024-01-15T12:00:00.000Z");
        ds.SrvCreated.Should().Be(Mills(extras.CreatedAt));
        ds.SrvModified.Should().Be(Mills(extras.ModifiedAt));
        ds.Device.Should().BeEmpty();
        ds.XDripJs.Should().NotBeNull();
    }

    [Fact]
    public void ProjectAsync_WithNothing_Throws()
    {
        var project = () => DeviceStatusProjectionService.ProjectFromSnapshots(null, null, null, null, null);

        project.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ProjectAsync_WithClientIdentifierInExtras_DoesNotReEmitIt()
    {
        var aps = CreateApsSnapshot(AidAlgorithm.OpenAps);
        aps.SuggestedJson = JsonSerializer.Serialize(new OpenApsSuggested { Bg = 120 }, JsonOptions);

        var extras = new DeviceStatusExtras
        {
            Id = Guid.NewGuid(),
            CorrelationId = aps.CorrelationId!.Value,
            Timestamp = ReferenceTime,
            Extras = new Dictionary<string, object?>
            {
                ["identifier"] = JsonSerializer.SerializeToElement("CLIENT-SUPPLIED", JsonOptions),
            },
        };

        var result = DeviceStatusProjectionService.ProjectFromSnapshots(aps, null, null, null, extras);

        result.ExtensionData.Should().NotContainKey("identifier");
    }

    #endregion

    #region GetAsync

    [Fact]
    public async Task GetAsync_ProjectsEachGroupWithItsMembers()
    {
        var aps = CreateApsSnapshot(AidAlgorithm.OpenAps);
        aps.SuggestedJson = JsonSerializer.Serialize(new OpenApsSuggested { Bg = 120 }, JsonOptions);
        var pump = CreatePumpSnapshot();
        pump.Reservoir = 50.0;
        var uploader = CreateUploaderSnapshot();
        uploader.Battery = 80;
        ReturnGroups(new DeviceStatusGroup(aps, pump, uploader, null, ReferenceTime));

        var results = (await _service.GetAsync(10, 0, null, CancellationToken.None)).ToList();

        var ds = results.Should().ContainSingle().Subject;
        ds.Id.Should().Be(aps.LegacyId);
        ds.OpenAps.Should().NotBeNull();
        ds.Pump!.Reservoir.Should().Be(50.0);
        ds.Uploader!.Battery.Should().Be(80);
    }

    [Fact]
    public async Task GetAsync_PumpOnlyGroup_ProjectsThePumpAlone()
    {
        var pump = CreatePumpSnapshot();
        pump.CorrelationId = null;
        pump.Reservoir = 42.0;
        pump.Manufacturer = "Medtronic";
        ReturnGroups(new DeviceStatusGroup(null, pump, null, null, ReferenceTime));

        var ds = (await _service.GetAsync(10, 0, null, CancellationToken.None)).Should().ContainSingle().Subject;

        ds.OpenAps.Should().BeNull();
        ds.Loop.Should().BeNull();
        ds.Pump!.Reservoir.Should().Be(42.0);
        ds.Pump.Manufacturer.Should().Be("Medtronic");
    }

    [Fact]
    public async Task GetAsync_GivesTheOverrideOnlyToStatusesWithAps()
    {
        var aps = CreateApsSnapshot(AidAlgorithm.Loop);
        aps.LoopJson = "{}";
        var uploader = CreateUploaderSnapshot();
        ReturnGroups(
            new DeviceStatusGroup(aps, null, null, null, ReferenceTime),
            new DeviceStatusGroup(null, null, uploader, null, ReferenceTime));
        _stateSpanRepo
            .Setup(r => r.GetByCategory(
                StateSpanCategory.Override, It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new StateSpan { StartTimestamp = ReferenceTime.AddMinutes(-5) }]);

        var results = (await _service.GetAsync(10, 0, null, CancellationToken.None)).ToList();

        results.Select(r => r.Override is not null).Should().Equal(true, false);
    }

    [Fact]
    public async Task GetAsync_PassesThePageToTheRepository()
    {
        await _service.GetAsync(2, 1, null, CancellationToken.None);

        _groupRepo.Verify(r => r.GetAsync(null, null, null, 2, 1, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetAsync_WithDeviceFilter_PassesDeviceToRepository()
    {
        await _service.GetAsync(10, 0, "find[device]=openaps://rpi", CancellationToken.None);

        _groupRepo.Verify(r => r.GetAsync(null, null, "openaps://rpi", 10, 0, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetAsync_WithDateRangeFilter_PassesDatesToRepository()
    {
        var expectedFrom = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var expectedTo = new DateTime(2024, 1, 16, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(-1);

        await _service.GetAsync(10, 0,
            "find[created_at][$gte]=2024-01-15T00:00:00Z&find[created_at][$lt]=2024-01-16T00:00:00Z",
            CancellationToken.None);

        _groupRepo.Verify(r => r.GetAsync(expectedFrom, expectedTo, null, 10, 0, It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region GetModifiedSinceAsync

    [Fact]
    public async Task GetModifiedSinceAsync_ProjectsEachGroupStampedByItsNewestRow()
    {
        var aps = CreateApsSnapshot(AidAlgorithm.AndroidAps);
        aps.ModifiedAt = ReferenceTime.AddMinutes(5);
        aps.SuggestedJson = JsonSerializer.Serialize(new OpenApsSuggested { Bg = 140 }, JsonOptions);
        var pump = CreatePumpSnapshot();
        pump.Reservoir = 60.0;
        var pumpChangedLater = ReferenceTime.AddMinutes(7);
        ReturnHistory(new HistoryRecord<DeviceStatusGroup>(new(aps, pump, null, null, pumpChangedLater), Deleted: false));

        var results = (await _service.GetModifiedSinceAsync(ReferenceMillis, 100, CancellationToken.None)).ToList();

        var ds = results.Should().ContainSingle().Subject;
        ds.OpenAps.Should().NotBeNull();
        ds.Pump!.Reservoir.Should().Be(60.0);
        ds.SrvModified.Should().Be(Mills(pumpChangedLater), "the history pages on the status's newest row");
        ds.IsValid.Should().BeNull();
    }

    [Fact]
    public async Task GetModifiedSinceAsync_DeletedStatus_ProjectsWithIsValidFalse()
    {
        var live = CreateUploaderSnapshot();
        live.ModifiedAt = ReferenceTime.AddMinutes(1);
        var deleted = CreateApsSnapshot(AidAlgorithm.AndroidAps);
        deleted.LegacyId = "65f000000000000000000def";
        deleted.ModifiedAt = ReferenceTime.AddMinutes(2);
        ReturnHistory(
            new HistoryRecord<DeviceStatusGroup>(new(null, null, live, null, live.ModifiedAt), Deleted: false),
            new HistoryRecord<DeviceStatusGroup>(new(deleted, null, null, null, deleted.ModifiedAt), Deleted: true));

        var results = (await _service.GetModifiedSinceAsync(ReferenceMillis, 100, CancellationToken.None)).ToList();

        results.Select(r => (r.Id, r.IsValid, r.SrvModified)).Should().Equal(
            (live.LegacyId, (bool?)null, Mills(live.ModifiedAt)),
            ("65f000000000000000000def", (bool?)false, Mills(deleted.ModifiedAt)));
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

    [Fact]
    public async Task GetByIdAsync_WithTheIdAnExtrasOnlyStatusIsListedUnder_ProjectsIt()
    {
        var extras = new DeviceStatusExtras
        {
            Id = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            Timestamp = ReferenceTime,
            Extras = new Dictionary<string, object?> { ["xdripjs"] = new { state = 6 } },
        };
        _extrasRepo.Setup(r => r.GetByIdAsync(extras.Id, It.IsAny<CancellationToken>())).ReturnsAsync(extras);
        _extrasRepo
            .Setup(r => r.GetByCorrelationIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([extras]);
        _apsRepo
            .Setup(r => r.GetByCorrelationIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _stateSpanRepo
            .Setup(r => r.GetByCategory(
                It.IsAny<StateSpanCategory>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var result = await _service.GetByIdAsync(extras.Id.ToString(), CancellationToken.None);

        result!.Id.Should().Be(extras.Id.ToString());
        result.XDripJs.Should().NotBeNull();
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
        to.Should().Be(new DateTime(2024, 1, 16, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(-1));
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
        to.Should().Be(new DateTime(2024, 1, 16, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(-1));
    }

    [Fact]
    public void ParseFindQuery_WithDeviceAndDateRange_ExtractsAll()
    {
        var (device, from, to) = DeviceStatusProjectionService.ParseFindQuery(
            "find[device]=openaps://rpi&find[created_at][$gte]=2024-01-15T00:00:00Z&find[created_at][$lt]=2024-01-16T00:00:00Z");

        device.Should().Be("openaps://rpi");
        from.Should().Be(new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc));
        to.Should().Be(new DateTime(2024, 1, 16, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(-1));
    }

    [Fact]
    public void ParseFindQuery_WithMalformedJson_ReturnsEmpty()
    {
        var (device, from, to) = DeviceStatusProjectionService.ParseFindQuery("{invalid json");

        device.Should().BeNull();
        from.Should().BeNull();
        to.Should().BeNull();
    }

    [Theory]
    [InlineData("""{"created_at":{"$gte":1705276800000,"$lte":1705363200000}}""")]
    [InlineData("""{"created_at":{"$lte":1705363200000,"$gte":1705276800000}}""")]
    public void ParseFindQuery_WithJsonMillsWindow_ExtractsBothBounds(string find)
    {
        var (_, from, to) = DeviceStatusProjectionService.ParseFindQuery(find);

        from.Should().Be(new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc));
        to.Should().Be(new DateTime(2024, 1, 16, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void ParseFindQuery_WithJsonBareMills_IsAnExactInstant()
    {
        var (_, from, to) = DeviceStatusProjectionService.ParseFindQuery(
            """{"created_at":1705320000000}""");

        var instant = new DateTime(2024, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        from.Should().Be(instant);
        to.Should().Be(instant);
    }

    [Theory]
    [InlineData("""{"created_at":{"$eq":1705320000000,"$gte":1705276800000}}""")]
    [InlineData("""{"created_at":{"$gte":1705276800000,"$eq":1705320000000}}""")]
    [InlineData("""{"created_at":{"$eq":1705320000000,"$lte":1705363200000}}""")]
    [InlineData("""{"created_at":{"$lte":1705363200000,"$eq":1705320000000}}""")]
    public void ParseFindQuery_WithEqualityInsideBound_NarrowsToTheInstant(string find)
    {
        var (_, from, to) = DeviceStatusProjectionService.ParseFindQuery(find);

        var instant = new DateTime(2024, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        from.Should().Be(instant);
        to.Should().Be(instant);
    }

    [Theory]
    [InlineData("""{"created_at":{"$eq":1705276800000,"$gte":1705320000000}}""")]
    [InlineData("""{"created_at":{"$gte":1705320000000,"$eq":1705276800000}}""")]
    public void ParseFindQuery_WithEqualityOutsideBound_KeepsTheEmptyIntersection(string find)
    {
        var (_, from, to) = DeviceStatusProjectionService.ParseFindQuery(find);

        from.Should().Be(new DateTime(2024, 1, 15, 12, 0, 0, DateTimeKind.Utc));
        to.Should().Be(new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void ParseFindQuery_WithJsonDeviceEqualityOperator_ExtractsDevice()
    {
        var (device, _, _) = DeviceStatusProjectionService.ParseFindQuery(
            """{"device":{"$eq":"loop://iPhone"}}""");

        device.Should().Be("loop://iPhone");
    }

    [Theory]
    [InlineData("""{"device":{"$eq":"loop://iPhone","$ne":"openaps://rpi"}}""")]
    [InlineData("""{"device":{"$ne":"openaps://rpi"}}""")]
    [InlineData("""{"created_at":{"$gte":1705276800000,"$ne":1705320000000}}""")]
    [InlineData("""{"created_at":{"$gte":true}}""")]
    [InlineData("""{"created_at":{"$gte":9223372036854775807}}""")]
    [InlineData("""{"created_at":{"$lte":-9223372036854775808}}""")]
    [InlineData("""{"created_at":{"$gte":1e300}}""")]
    [InlineData("""{"created_at":{"$gte":"9223372036854775807"}}""")]
    [InlineData("""{"created_at":99999999999999999}""")]
    [InlineData("""{"device":{"$ne":"openaps://rpi","$eq":"loop://iPhone"}}""")]
    [InlineData("""{"device":{},"created_at":{"$gte":1705276800000}}""")]
    public void ParseDeleteFind_WithUnsupportedJsonCondition_Refuses(string find)
    {
        DeviceStatusProjectionService.ParseDeleteFind(find).Should().BeNull();
    }

    [Theory]
    [InlineData("""{"device":{"$eq":"loop://iPhone","$ne":"openaps://rpi"}}""")]
    [InlineData("""{"device":{"$ne":"openaps://rpi","$eq":"loop://iPhone"}}""")]
    public void ParseFindQuery_WithDeviceEqualityBesideAnotherOperator_KeepsTheDevice(string find)
    {
        var (device, _, _) = DeviceStatusProjectionService.ParseFindQuery(find);

        device.Should().Be("loop://iPhone");
    }

    [Theory]
    [InlineData("""{"created_at":{"$gt":1705276800000,"$lt":1705363200000}}""")]
    [InlineData("""{"created_at":{"$lt":1705363200000,"$gt":1705276800000}}""")]
    [InlineData("find[created_at][$gt]=1705276800000&find[created_at][$lt]=1705363200000")]
    [InlineData("find[created_at][$lt]=2024-01-16T00:00:00Z&find[created_at][$gt]=2024-01-15T00:00:00Z")]
    public void ParseFindQuery_WithStrictBounds_ExcludesTheBoundaryInstants(string find)
    {
        var (_, from, to) = DeviceStatusProjectionService.ParseFindQuery(find);

        from.Should().Be(new DateTime(2024, 1, 15, 0, 0, 0, 1, DateTimeKind.Utc));
        to.Should().Be(new DateTime(2024, 1, 15, 23, 59, 59, 999, DateTimeKind.Utc));
    }

    [Theory]
    [InlineData("find[created_at][$gte]=2024-01-15T00:00:00Z&find[created_at][$gt]=2024-01-15T12:00:00Z"
        + "&find[created_at][$lte]=2024-01-16T00:00:00Z&find[created_at][$lt]=2024-01-15T18:00:00Z")]
    [InlineData("find[created_at][$lt]=2024-01-15T18:00:00Z&find[created_at][$lte]=2024-01-16T00:00:00Z"
        + "&find[created_at][$gt]=2024-01-15T12:00:00Z&find[created_at][$gte]=2024-01-15T00:00:00Z")]
    public void ParseFindQuery_WithQueryStringBoundsOnBothSides_Intersects(string find)
    {
        var (_, from, to) = DeviceStatusProjectionService.ParseFindQuery(find);

        from.Should().Be(new DateTime(2024, 1, 15, 12, 0, 0, 1, DateTimeKind.Utc));
        to.Should().Be(new DateTime(2024, 1, 15, 17, 59, 59, 999, DateTimeKind.Utc));
    }

    [Fact]
    public void ParseDeleteFind_WithStrictQueryStringBounds_DeletesTheExactWindow()
    {
        var filters = DeviceStatusProjectionService.ParseDeleteFind(
            "find[device]=openaps://rpi&find[created_at][$gt]=1705276800000&find[created_at][$lt]=1705363200000");

        filters.Should().Be(("openaps://rpi",
            (DateTime?)new DateTime(2024, 1, 15, 0, 0, 0, 1, DateTimeKind.Utc),
            (DateTime?)new DateTime(2024, 1, 15, 23, 59, 59, 999, DateTimeKind.Utc)));
    }

    [Theory]
    [InlineData("""{"created_at":{"$gt":253402300799999}}""")]
    [InlineData("""{"created_at":{"$lt":-62135596800000}}""")]
    [InlineData("""{"created_at":{"$gt":"9999-12-31T23:59:59.9999999Z"}}""")]
    [InlineData("find[created_at][$gt]=253402300799999")]
    [InlineData("find[created_at][$lt]=0001-01-01T00:00:00Z")]
    public void ParseDeleteFind_WithStrictBoundBeyondTheDateRange_Refuses(string find)
    {
        DeviceStatusProjectionService.ParseDeleteFind(find).Should().BeNull();
    }

    [Theory]
    [InlineData("""{"created_at":{"$gte":253402300799999}}""")]
    [InlineData("find[created_at][$lte]=0001-01-01T00:00:00Z")]
    public void ParseDeleteFind_WithInclusiveBoundAtTheDateRangeEdge_Applies(string find)
    {
        DeviceStatusProjectionService.ParseDeleteFind(find).Should().NotBeNull();
    }

    #endregion

    #region CountAsync

    [Fact]
    public async Task CountAsync_ReturnsTheRepositoryCount()
    {
        _groupRepo.Setup(r => r.CountAsync(null, null, null, It.IsAny<CancellationToken>())).ReturnsAsync(12);

        (await _service.CountAsync(null, CancellationToken.None)).Should().Be(12);
    }

    [Fact]
    public async Task CountAsync_WithDateFilter_PassesDatesToRepository()
    {
        var expectedFrom = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var expectedTo = new DateTime(2024, 1, 16, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(-1);
        _groupRepo.Setup(r => r.CountAsync(expectedFrom, expectedTo, null, It.IsAny<CancellationToken>())).ReturnsAsync(8);

        var count = await _service.CountAsync(
            "find[created_at][$gte]=2024-01-15T00:00:00Z&find[created_at][$lt]=2024-01-16T00:00:00Z",
            CancellationToken.None);

        count.Should().Be(8);
    }

    [Fact]
    public async Task CountAsync_WithDeviceFilter_PassesDeviceToRepository()
    {
        _groupRepo.Setup(r => r.CountAsync(null, null, "openaps://rpi", It.IsAny<CancellationToken>())).ReturnsAsync(4);

        (await _service.CountAsync("find[device]=openaps://rpi", CancellationToken.None)).Should().Be(4);
    }

    #endregion

    #region Helpers

    private void ReturnGroups(params DeviceStatusGroup[] groups) =>
        _groupRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(groups);

    private void ReturnHistory(params HistoryRecord<DeviceStatusGroup>[] page) =>
        _groupRepo
            .Setup(r => r.GetModifiedSinceAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(page);

    private static long Mills(DateTime value) =>
        new DateTimeOffset(value, TimeSpan.Zero).ToUnixTimeMilliseconds();

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
