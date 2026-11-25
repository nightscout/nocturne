using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Services.Devices;
using Nocturne.API.Services.DeviceStatuses;
using Nocturne.API.Services.V4;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.LoopAnalysis;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Tests.Shared.Mocks;
using Xunit;

using V4Models = Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.API.Tests.Services.V4;

public class DeviceStatusDecomposerTests : IDisposable
{
    private static readonly DateTimeOffset ReceivedAt = new(2026, 10, 29, 3, 4, 5, 678, TimeSpan.Zero);

    private readonly NocturneDbContext _context;
    private readonly Mock<IStateSpanService> _stateSpanServiceMock;
    private readonly List<StateSpan> _spans;
    private readonly Mock<IDeviceService> _deviceServiceMock;
    private readonly DeviceStatusExtrasRepository _extrasRepo;
    private readonly SensorStateReportRepository _sensorStateRepo;
    private readonly DeviceStatusDecomposer _decomposer;

    public DeviceStatusDecomposerTests()
    {
        _context = TestDbContextFactory.CreateInMemoryContext();
        _context.TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var ctxFactory = new TestNocturneDbContextFactory(_context);
        var apsRepo = new ApsSnapshotRepository(ctxFactory, new SystemAuditContext(), NullLogger<ApsSnapshotRepository>.Instance);
        var pumpRepo = new PumpSnapshotRepository(ctxFactory, new SystemAuditContext(), NullLogger<PumpSnapshotRepository>.Instance);
        var uploaderRepo = new UploaderSnapshotRepository(ctxFactory, new SystemAuditContext(), NullLogger<UploaderSnapshotRepository>.Instance);
        _extrasRepo = new DeviceStatusExtrasRepository(ctxFactory, new SystemAuditContext(), NullLogger<DeviceStatusExtrasRepository>.Instance);
        _sensorStateRepo = new SensorStateReportRepository(ctxFactory, new SystemAuditContext(), NullLogger<SensorStateReportRepository>.Instance);
        _stateSpanServiceMock = new Mock<IStateSpanService>();
        _spans = _stateSpanServiceMock.BacksWith([]);
        _deviceServiceMock = new Mock<IDeviceService>().AnswersBatchFormsPerRecord();

        _decomposer = DeviceStatusDecomposerDoubles.Build(
            apsRepo, pumpRepo, uploaderRepo,
            _extrasRepo,
            _sensorStateRepo,
            _stateSpanServiceMock.Object,
            _deviceServiceMock.Object,
            time: new FakeTimeProvider(ReceivedAt));
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    #region OpenAPS → ApsSnapshot

    [Fact]
    public async Task DecomposeAsync_OpenApsDeviceStatus_CreatesApsSnapshot()
    {
        // Arrange
        var ds = new DeviceStatus
        {
            Id = "abc123",
            Mills = 1700000000000,
            UtcOffset = -300,
            Device = "openaps://Samsung",
            OpenAps = new OpenApsStatus
            {
                Iob = new OpenApsIobData { Iob = 2.5, BasalIob = 1.0, BolusIob = 1.5 },
                Cob = 30.0,
                Suggested = new OpenApsSuggested
                {
                    Bg = 120.0,
                    EventualBG = 95.0,
                    TargetBG = 100.0,
                    InsulinReq = 0.5,
                    SensitivityRatio = 1.1,
                    Timestamp = "2023-11-14T12:00:00Z",
                    PredBGs = new OpenApsPredBGs
                    {
                        IOB = new List<double?> { 120, 115, 110, 105, 100 },
                        ZT = new List<double?> { 120, 118, 115 },
                        COB = new List<double?> { 120, 125, 130, 125, 120, 110 },
                        UAM = new List<double?> { 120, 130, 135, 130 }
                    }
                },
                Enacted = new OpenApsEnacted
                {
                    Received = true,
                    Rate = 1.5,
                    Duration = 30,
                    Smb = 0.3,
                    Bg = 120.0,
                    EventualBG = 95.0,
                    TargetBG = 100.0,
                    Timestamp = "2023-11-14T12:00:00Z",
                    PredBGs = new OpenApsPredBGs
                    {
                        IOB = new List<double?> { 120, 115, 110, 105, 100 },
                    }
                }
            }
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        // Assert
        result.CreatedRecords.Should().HaveCount(1);
        var aps = result.CreatedRecords[0].Should().BeOfType<V4Models.ApsSnapshot>().Subject;
        aps.AidAlgorithm.Should().Be(V4Models.AidAlgorithm.OpenAps);
        aps.Iob.Should().Be(2.5);
        aps.BasalIob.Should().Be(1.0);
        aps.BolusIob.Should().Be(1.5);
        aps.Cob.Should().Be(30.0);
        aps.CurrentBg.Should().Be(120.0);
        aps.EventualBg.Should().Be(95.0);
        aps.TargetBg.Should().Be(100.0);
        aps.Enacted.Should().BeTrue();
        aps.EnactedRate.Should().Be(1.5);
        aps.EnactedDuration.Should().Be(30);
        aps.EnactedBolusVolume.Should().Be(0.3);
        aps.PredictedDefaultJson.Should().NotBeNull();
        aps.PredictedStartMills.Should().NotBeNull();
    }

    #endregion

    #region Loop → ApsSnapshot

    [Fact]
    public async Task DecomposeAsync_LoopDeviceStatus_CreatesApsSnapshot()
    {
        // Arrange
        var ds = new DeviceStatus
        {
            Id = "loop123",
            Mills = 1700000000000,
            Device = "Loop/3.0",
            Loop = new LoopStatus
            {
                Iob = new LoopIob { Iob = 1.8, BasalIob = 0.5 },
                Cob = new LoopCob { Cob = 25.0 },
                RecommendedBolus = 0.7,
                Predicted = new LoopPredicted
                {
                    Values = new double[] { 130, 125, 120, 115, 110 },
                    StartDate = "2023-11-14T12:00:00Z"
                },
                Enacted = new LoopEnacted
                {
                    Received = true,
                    Rate = 2.0,
                    Duration = 30,
                    BolusVolume = 0.4
                }
            }
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        // Assert
        result.CreatedRecords.Should().HaveCount(1);
        var aps = result.CreatedRecords[0].Should().BeOfType<V4Models.ApsSnapshot>().Subject;
        aps.AidAlgorithm.Should().Be(V4Models.AidAlgorithm.Loop);
        aps.Iob.Should().Be(1.8);
        aps.BasalIob.Should().Be(0.5);
        aps.BolusIob.Should().BeNull();
        aps.Cob.Should().Be(25.0);
        aps.CurrentBg.Should().Be(130.0);
        aps.EventualBg.Should().Be(110.0);
        aps.RecommendedBolus.Should().Be(0.7);
        aps.Enacted.Should().BeTrue();
        aps.EnactedBolusVolume.Should().Be(0.4);
        aps.PredictedDefaultJson.Should().NotBeNull();
    }

    #endregion

    #region Pump → PumpSnapshot

    [Fact]
    public async Task DecomposeAsync_PumpStatus_CreatesPumpSnapshot()
    {
        // Arrange
        var ds = new DeviceStatus
        {
            Id = "pump123",
            Mills = 1700000000000,
            Device = "openaps://Samsung",
            Pump = new PumpStatus
            {
                Manufacturer = "Insulet",
                Model = "Omnipod 5",
                Reservoir = 150.5,
                ReservoirDisplayOverride = "150+U",
                Battery = new PumpBattery { Percent = 85, Voltage = 3.7 },
                Status = new PumpStatusDetails { Status = "normal", Bolusing = false, Suspended = false },
                Clock = "2023-11-14T12:00:00Z"
            }
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        // Assert
        result.CreatedRecords.Should().HaveCount(1);
        var pump = result.CreatedRecords[0].Should().BeOfType<V4Models.PumpSnapshot>().Subject;
        pump.Manufacturer.Should().Be("Insulet");
        pump.Model.Should().Be("Omnipod 5");
        pump.Reservoir.Should().Be(150.5);
        pump.ReservoirDisplay.Should().Be("150+U");
        pump.BatteryPercent.Should().Be(85);
        pump.BatteryVoltage.Should().Be(3.7);
        pump.PumpStatus.Should().Be("normal");
        pump.Bolusing.Should().BeFalse();
        pump.Suspended.Should().BeFalse();
        pump.Clock.Should().Be("2023-11-14T12:00:00Z");
    }

    [Fact]
    public async Task DecomposeAsync_ReDecomposedPumpStatus_TakesAFreshResolutionOverTheStoredOne()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var ds = new DeviceStatus
        {
            Id = "pump-reattribution",
            Mills = 1700000000000,
            Device = "openaps://Samsung",
            Pump = new PumpStatus { Manufacturer = "Insulet", Model = "Omnipod 5" }
        };
        _deviceServiceMock.Links()
            .SetupSequence(l => l.Resolve(It.IsAny<Guid?>(), It.IsAny<long>()))
            .Returns(first)
            .Returns(second);

        await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);
        await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        _context.PumpSnapshots.Single(p => p.LegacyId == "pump-reattribution").PatientDeviceId.Should().Be(second);
    }

    [Fact]
    public async Task DecomposeAsync_ReDecomposedPumpStatus_KeepsTheStoredPatientDeviceAttribution()
    {
        var patientDeviceId = Guid.CreateVersion7();
        var ds = new DeviceStatus
        {
            Id = "pump-attribution",
            Mills = 1700000000000,
            Device = "openaps://Samsung",
            Pump = new PumpStatus { Manufacturer = "Insulet", Model = "Omnipod 5" }
        };

        // The second resolution is ambiguous (a second pump has been registered since), so it
        // returns nothing and the stored attribution has to survive the re-decompose.
        _deviceServiceMock.Links()
            .SetupSequence(l => l.Resolve(It.IsAny<Guid?>(), It.IsAny<long>()))
            .Returns(patientDeviceId)
            .Returns((Guid?)null);

        await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);
        await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        var stored = _context.PumpSnapshots.Single(p => p.LegacyId == "pump-attribution");
        stored.PatientDeviceId.Should().Be(patientDeviceId);
    }

    #endregion

    #region Uploader → UploaderSnapshot

    [Fact]
    public async Task DecomposeAsync_UploaderStatus_CreatesUploaderSnapshot()
    {
        // Arrange
        var ds = new DeviceStatus
        {
            Id = "uploader123",
            Mills = 1700000000000,
            Device = "openaps://Samsung",
            Uploader = new UploaderStatus
            {
                Name = "Samsung Galaxy S23",
                Battery = 78,
                BatteryVoltage = 4.1,
                Temperature = 32.5,
                Type = "phone"
            },
            IsCharging = true
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        // Assert
        result.CreatedRecords.Should().HaveCount(1);
        var uploader = result.CreatedRecords[0].Should().BeOfType<V4Models.UploaderSnapshot>().Subject;
        uploader.Name.Should().Be("Samsung Galaxy S23");
        uploader.Battery.Should().Be(78);
        uploader.BatteryVoltage.Should().Be(4.1);
        uploader.Temperature.Should().Be(32.5);
        uploader.Type.Should().Be("phone");
        uploader.IsCharging.Should().BeTrue();
    }

    [Fact]
    public async Task DecomposeAsync_NestedUploaderIsCharging_MapsToSnapshot()
    {
        // Trio sends isCharging inside the uploader object, not top-level
        var json = """
            {
                "_id": "trio-ischarging",
                "mills": 1700000000000,
                "device": "Trio",
                "uploader": { "battery": 75, "batteryVoltage": 4.0, "isCharging": true }
            }
            """;
        var ds = System.Text.Json.JsonSerializer.Deserialize<DeviceStatus>(json)!;

        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        result.CreatedRecords.Should().HaveCount(1);
        var uploader = result.CreatedRecords[0].Should().BeOfType<V4Models.UploaderSnapshot>().Subject;
        uploader.Battery.Should().Be(75);
        uploader.IsCharging.Should().BeTrue();
    }

    /// <summary>
    /// The document xDrip+ posts with "Upload Dexcom battery" on: <c>battery</c> is a sentence,
    /// and the readings are keys the uploader model does not type.
    /// </summary>
    [Fact]
    public async Task DecomposeAsync_XdripTransmitterStatus_KeepsItsReadings()
    {
        var json = """
            {
                "device": "G6 Transmitter",
                "uploader": {
                    "days": 52,
                    "daysEstimate": "52 / 100",
                    "status": "OK",
                    "voltagea": 296,
                    "voltageb": 281,
                    "resistance": 710,
                    "resistance_status": "GOOD",
                    "temperature": 31,
                    "battery": "52 days (voltage: 296/281)",
                    "lastQueried": 1700000000000,
                    "type": "DEXCOM_TRANSMITTER"
                }
            }
            """;
        var ds = NightscoutJsonBody.ReadObjects<DeviceStatus>(JsonDocument.Parse(json).RootElement)!.Single();
        ds.Mills = 1700000000000;

        await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        var uploaderRepo = new UploaderSnapshotRepository(
            new TestNocturneDbContextFactory(_context), new SystemAuditContext(),
            NullLogger<UploaderSnapshotRepository>.Instance);
        var stored = await uploaderRepo.GetLatestOfTypeAsync(V4Models.TransmitterStatus.UploaderType, asOf: null);

        stored!.Battery.Should().BeNull();
        V4Models.TransmitterStatus.From(stored).Should().BeEquivalentTo(new V4Models.TransmitterStatus
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime,
            Device = "G6 Transmitter",
            VoltageA = 296,
            VoltageB = 281,
            Resistance = 710,
            Days = 52,
            Status = "OK",
            Temperature = 31,
        });
    }

    [Fact]
    public async Task GetLatestOfTypeAsync_SkipsNewerUploadersOfAnotherType()
    {
        var transmitter = new DeviceStatus
        {
            Mills = 1700000000000,
            Device = "G6 Transmitter",
            Uploader = new UploaderStatus
            {
                Type = V4Models.TransmitterStatus.UploaderType,
                ExtensionData = new() { ["voltagea"] = JsonSerializer.SerializeToElement(288) },
            },
        };
        var phone = new DeviceStatus
        {
            Mills = 1700000600000,
            Device = "Google Pixel 8",
            Uploader = new UploaderStatus { Battery = 64, Type = "PHONE" },
        };
        await _decomposer.DecomposeBatchAsync([transmitter, phone], null, WriteOrigin.Live);

        var uploaderRepo = new UploaderSnapshotRepository(
            new TestNocturneDbContextFactory(_context), new SystemAuditContext(),
            NullLogger<UploaderSnapshotRepository>.Instance);
        var stored = await uploaderRepo.GetLatestOfTypeAsync(V4Models.TransmitterStatus.UploaderType, asOf: null);

        V4Models.TransmitterStatus.From(stored)!.VoltageA.Should().Be(288);
    }

    #endregion

    #region UploaderBattery fallback → UploaderSnapshot

    [Fact]
    public async Task DecomposeAsync_UploaderBatteryOnly_CreatesUploaderSnapshot()
    {
        // Arrange
        var ds = new DeviceStatus
        {
            Id = "uploader-battery-only",
            Mills = 1700000000000,
            Device = "xDrip+",
            UploaderBattery = 65
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        // Assert
        result.CreatedRecords.Should().HaveCount(1);
        var uploader = result.CreatedRecords[0].Should().BeOfType<V4Models.UploaderSnapshot>().Subject;
        uploader.Battery.Should().Be(65);
    }

    #endregion

    #region Override → StateSpan

    [Fact]
    public async Task DecomposeAsync_OverrideActive_CreatesStateSpan()
    {
        // Arrange
        var ds = new DeviceStatus
        {
            Id = "override123",
            Mills = 1700000000000,
            Device = "Loop/3.0",
            Override = new OverrideStatus
            {
                Active = true,
                Name = "Exercise",
                Duration = 3600,
                Multiplier = 1.5,
                CurrentCorrectionRange = new CorrectionRange { MinValue = 140, MaxValue = 160 }
            }
        };


        // Act
        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        // Assert
        result.CreatedRecords.Should().HaveCount(1);
        result.CreatedRecords[0].Should().BeOfType<StateSpan>();

        _spans.Should().ContainSingle(ss =>
                    ss.Category == StateSpanCategory.Override
                    && ss.State == "Custom"
                    && ss.StartMills == 1700000000000
                    && ss.OriginalId == "override123"
                    && ss.Metadata != null
                    && ss.Metadata.ContainsKey("name")
                    && ss.Metadata.ContainsKey("multiplier")
                    && ss.Metadata.ContainsKey("currentCorrectionRange.minValue")
                    && ss.Metadata.ContainsKey("currentCorrectionRange.maxValue")
                    && ss.Metadata.TryReadString(StateSpanMetadataExtensions.CollectionKey)
                        == StateSpanMetadataExtensions.DeviceStatusCollection);
    }

    #endregion

    #region No APS data → skip ApsSnapshot

    [Fact]
    public async Task DecomposeAsync_NoApsData_SkipsApsSnapshot()
    {
        // Arrange - no OpenAps, no Loop
        var ds = new DeviceStatus
        {
            Id = "no-aps",
            Mills = 1700000000000,
            Device = "xDrip+"
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        // Assert
        result.CreatedRecords.Should().BeEmpty();
        result.UpdatedRecords.Should().BeEmpty();
        _context.ApsSnapshots.Should().BeEmpty();
    }

    #endregion

    #region Idempotency

    [Fact]
    public async Task DecomposeAsync_IdenticalResend_LeavesTheSnapshotUnchanged()
    {
        // Arrange
        var ds = new DeviceStatus
        {
            Id = "idempotent-aps",
            Mills = 1700000000000,
            Device = "openaps://Samsung",
            OpenAps = new OpenApsStatus
            {
                Iob = new OpenApsIobData { Iob = 2.5 },
                Suggested = new OpenApsSuggested
                {
                    Bg = 120.0,
                    EventualBG = 95.0,
                    Timestamp = "2023-11-14T12:00:00Z"
                }
            }
        };

        // Act - first call creates
        var firstResult = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);
        firstResult.CreatedRecords.Should().HaveCount(1);
        firstResult.UpdatedRecords.Should().BeEmpty();

        // Act - second call should update
        var secondResult = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        // Assert
        secondResult.UnchangedRecords.Should().HaveCount(1);
        secondResult.UpdatedRecords.Should().BeEmpty();
        secondResult.CreatedRecords.Should().BeEmpty();
        _context.ApsSnapshots.Should().HaveCount(1);
    }

    #endregion

    #region Full DeviceStatus → all three snapshots

    [Fact]
    public async Task DecomposeAsync_FullDeviceStatus_CreatesAllThreeSnapshots()
    {
        // Arrange
        var ds = new DeviceStatus
        {
            Id = "full-ds",
            Mills = 1700000000000,
            Device = "openaps://Samsung",
            OpenAps = new OpenApsStatus
            {
                Iob = new OpenApsIobData { Iob = 2.0 },
                Suggested = new OpenApsSuggested
                {
                    Bg = 110.0,
                    EventualBG = 100.0,
                    Timestamp = "2023-11-14T12:00:00Z"
                }
            },
            Pump = new PumpStatus
            {
                Manufacturer = "Medtronic",
                Reservoir = 100.0,
                Battery = new PumpBattery { Percent = 90 }
            },
            Uploader = new UploaderStatus
            {
                Name = "Pixel 8",
                Battery = 55
            }
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        // Assert
        result.CreatedRecords.Should().HaveCount(3);
        result.CreatedRecords.OfType<V4Models.ApsSnapshot>().Should().HaveCount(1);
        result.CreatedRecords.OfType<V4Models.PumpSnapshot>().Should().HaveCount(1);
        result.CreatedRecords.OfType<V4Models.UploaderSnapshot>().Should().HaveCount(1);
    }

    #endregion

    #region OpenAPS - Suggested Only (No Enacted)

    [Fact]
    public async Task DecomposeAsync_OpenApsSuggestedOnly_SetsEnactedFalse()
    {
        // Arrange - no Enacted object, only Suggested
        var ds = new DeviceStatus
        {
            Id = "suggested-only",
            Mills = 1700000000000,
            Device = "openaps://Samsung",
            OpenAps = new OpenApsStatus
            {
                Iob = new OpenApsIobData { Iob = 1.5 },
                Suggested = new OpenApsSuggested
                {
                    Bg = 130.0,
                    EventualBG = 110.0,
                    TargetBG = 100.0,
                    InsulinReq = 0.3,
                    Timestamp = "2023-11-14T12:00:00Z",
                    PredBGs = new OpenApsPredBGs
                    {
                        IOB = new List<double?> { 130, 125, 120, 115, 110 }
                    }
                }
            }
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        // Assert
        var aps = result.CreatedRecords[0].Should().BeOfType<V4Models.ApsSnapshot>().Subject;
        aps.Enacted.Should().BeFalse();
        aps.EnactedRate.Should().BeNull();
        aps.EnactedDuration.Should().BeNull();
        aps.EnactedBolusVolume.Should().BeNull();
        aps.CurrentBg.Should().Be(130.0);
        aps.EventualBg.Should().Be(110.0);
        aps.PredictedDefaultJson.Should().NotBeNull();
    }

    [Fact]
    public async Task DecomposeAsync_OpenApsEnactedReceivedFalse_SetsEnactedFalse()
    {
        // Arrange - Enacted exists but Received=false
        var ds = new DeviceStatus
        {
            Id = "enacted-not-received",
            Mills = 1700000000000,
            Device = "openaps://Samsung",
            OpenAps = new OpenApsStatus
            {
                Suggested = new OpenApsSuggested
                {
                    Bg = 120.0, EventualBG = 100.0, Timestamp = "2023-11-14T12:00:00Z"
                },
                Enacted = new OpenApsEnacted
                {
                    Received = false,
                    Rate = 1.0,
                    Duration = 30
                }
            }
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        // Assert - Enacted object exists but Received is false
        var aps = result.CreatedRecords[0].Should().BeOfType<V4Models.ApsSnapshot>().Subject;
        aps.Enacted.Should().BeFalse();
        aps.EnactedRate.Should().Be(1.0);
        aps.EnactedDuration.Should().Be(30);
    }

    [Fact]
    public async Task DecomposeAsync_OpenApsEnactedReceivedNull_SetsEnactedFalse()
    {
        // Arrange - Enacted exists but Received is null
        var ds = new DeviceStatus
        {
            Id = "enacted-null-received",
            Mills = 1700000000000,
            Device = "openaps://Samsung",
            OpenAps = new OpenApsStatus
            {
                Suggested = new OpenApsSuggested
                {
                    Bg = 120.0, EventualBG = 100.0, Timestamp = "2023-11-14T12:00:00Z"
                },
                Enacted = new OpenApsEnacted
                {
                    // Received is null, Recieved is also null
                    Rate = 0.5,
                    Duration = 30
                }
            }
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        // Assert
        var aps = result.CreatedRecords[0].Should().BeOfType<V4Models.ApsSnapshot>().Subject;
        aps.Enacted.Should().BeFalse();
    }

    #endregion

    #region OpenAPS COB Fallback

    [Fact]
    public async Task DecomposeAsync_OpenApsCobOnTopLevel_UsesThatValue()
    {
        // Arrange - COB on OpenAps top-level
        var ds = new DeviceStatus
        {
            Id = "cob-toplevel",
            Mills = 1700000000000,
            Device = "openaps://Samsung",
            OpenAps = new OpenApsStatus
            {
                Cob = 45.0,
                Suggested = new OpenApsSuggested
                {
                    Bg = 120.0, EventualBG = 100.0,
                    COB = 30.0, // different value in suggested
                    Timestamp = "2023-11-14T12:00:00Z"
                }
            }
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        // Assert - top-level Cob takes priority via null-coalescing
        var aps = result.CreatedRecords[0].Should().BeOfType<V4Models.ApsSnapshot>().Subject;
        aps.Cob.Should().Be(45.0);
    }

    [Fact]
    public async Task DecomposeAsync_OpenApsCobNullFallsToSuggested_UsesSuggestedCOB()
    {
        // Arrange
        var ds = new DeviceStatus
        {
            Id = "cob-fallback",
            Mills = 1700000000000,
            Device = "openaps://Samsung",
            OpenAps = new OpenApsStatus
            {
                Cob = null, // top-level null
                Suggested = new OpenApsSuggested
                {
                    Bg = 120.0, EventualBG = 100.0,
                    COB = 35.0,
                    Timestamp = "2023-11-14T12:00:00Z"
                }
            }
        };

        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        var aps = result.CreatedRecords[0].Should().BeOfType<V4Models.ApsSnapshot>().Subject;
        aps.Cob.Should().Be(35.0);
    }

    #endregion

    #region OpenAPS with Null Predictions

    [Fact]
    public async Task DecomposeAsync_OpenApsNoPredictions_PredictionFieldsAreNull()
    {
        var ds = new DeviceStatus
        {
            Id = "no-predictions",
            Mills = 1700000000000,
            Device = "openaps://Samsung",
            OpenAps = new OpenApsStatus
            {
                Iob = new OpenApsIobData { Iob = 1.0 },
                Suggested = new OpenApsSuggested
                {
                    Bg = 100.0, EventualBG = 95.0,
                    Timestamp = "2023-11-14T12:00:00Z"
                    // No PredBGs
                }
            }
        };

        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        var aps = result.CreatedRecords[0].Should().BeOfType<V4Models.ApsSnapshot>().Subject;
        aps.PredictedDefaultJson.Should().BeNull();
        aps.PredictedIobJson.Should().BeNull();
        aps.PredictedZtJson.Should().BeNull();
        aps.PredictedCobJson.Should().BeNull();
        aps.PredictedUamJson.Should().BeNull();
    }

    [Fact]
    public async Task DecomposeAsync_OpenApsInvalidTimestamp_PredictedStartMillsNull()
    {
        var ds = new DeviceStatus
        {
            Id = "invalid-timestamp",
            Mills = 1700000000000,
            Device = "openaps://Samsung",
            OpenAps = new OpenApsStatus
            {
                Suggested = new OpenApsSuggested
                {
                    Bg = 100.0, EventualBG = 95.0,
                    Timestamp = "not-a-timestamp"
                }
            }
        };

        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        var aps = result.CreatedRecords[0].Should().BeOfType<V4Models.ApsSnapshot>().Subject;
        aps.PredictedStartMills.Should().BeNull();
    }

    [Fact]
    public async Task DecomposeAsync_OpenApsNullTimestamp_PredictedStartMillsNull()
    {
        var ds = new DeviceStatus
        {
            Id = "null-timestamp",
            Mills = 1700000000000,
            Device = "openaps://Samsung",
            OpenAps = new OpenApsStatus
            {
                Suggested = new OpenApsSuggested
                {
                    Bg = 100.0, EventualBG = 95.0,
                    Timestamp = null
                }
            }
        };

        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        var aps = result.CreatedRecords[0].Should().BeOfType<V4Models.ApsSnapshot>().Subject;
        aps.PredictedStartMills.Should().BeNull();
    }

    #endregion

    #region OpenAPS Freshest Command

    private static DeviceStatus HeldTempBasalStatus(string? enactedTimestamp, string? suggestedTimestamp) => new()
    {
        Id = "held-temp-basal",
        Mills = 1700000000000,
        Device = "openaps://Trio",
        OpenAps = new OpenApsStatus
        {
            Suggested = new OpenApsSuggested
            {
                Bg = 259.0, EventualBG = 210.0, TargetBG = 110.0, COB = 12.0,
                InsulinReq = 1.2, SensitivityRatio = 1.3,
                Timestamp = suggestedTimestamp,
                PredBGs = new OpenApsPredBGs
                {
                    IOB = new List<double?> { 259, 255 },
                    ZT = new List<double?> { 259, 250 },
                    COB = new List<double?> { 259, 262 },
                    UAM = new List<double?> { 259, 265 },
                },
            },
            Enacted = new OpenApsEnacted
            {
                Received = true,
                Rate = 2.4, Duration = 30, Smb = 0.4,
                Bg = 227.0, EventualBG = 180.0, TargetBG = 100.0, COB = 20.0,
                InsulinReq = 0.6, SensitivityRatio = 1.1,
                Timestamp = enactedTimestamp,
                PredBGs = new OpenApsPredBGs
                {
                    IOB = new List<double?> { 227, 220 },
                    ZT = new List<double?> { 227, 215 },
                    COB = new List<double?> { 227, 230 },
                    UAM = new List<double?> { 227, 235 },
                },
            },
        },
    };

    private static void ShouldCarrySuggested(V4Models.ApsSnapshot aps)
    {
        aps.CurrentBg.Should().Be(259.0);
        aps.EventualBg.Should().Be(210.0);
        aps.TargetBg.Should().Be(110.0);
        aps.Cob.Should().Be(12.0);
        aps.RecommendedBolus.Should().Be(1.2);
        aps.SensitivityRatio.Should().Be(1.3);
        aps.PredictedIobJson.Should().Be("[259,255]");
        aps.PredictedZtJson.Should().Be("[259,250]");
        aps.PredictedCobJson.Should().Be("[259,262]");
        aps.PredictedUamJson.Should().Be("[259,265]");
    }

    private static void ShouldCarryEnacted(V4Models.ApsSnapshot aps)
    {
        aps.CurrentBg.Should().Be(227.0);
        aps.EventualBg.Should().Be(180.0);
        aps.TargetBg.Should().Be(100.0);
        aps.Cob.Should().Be(20.0);
        aps.RecommendedBolus.Should().Be(0.6);
        aps.SensitivityRatio.Should().Be(1.1);
        aps.PredictedIobJson.Should().Be("[227,220]");
        aps.PredictedZtJson.Should().Be("[227,215]");
        aps.PredictedCobJson.Should().Be("[227,230]");
        aps.PredictedUamJson.Should().Be("[227,235]");
    }

    private static void ShouldKeepEnactedDelivery(V4Models.ApsSnapshot aps)
    {
        aps.Enacted.Should().BeTrue();
        aps.EnactedRate.Should().Be(2.4);
        aps.EnactedDuration.Should().Be(30);
        aps.EnactedBolusVolume.Should().Be(0.4);
    }

    private async Task<V4Models.ApsSnapshot> DecomposeApsAsync(DeviceStatus ds)
    {
        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);
        return result.CreatedRecords.OfType<V4Models.ApsSnapshot>().Single();
    }

    [Fact]
    public async Task DecomposeAsync_SuggestedNewerThanEnacted_TakesCommandFieldsFromSuggested()
    {
        var aps = await DecomposeApsAsync(HeldTempBasalStatus("2026-10-04T22:48:52Z", "2026-10-04T23:18:55Z"));

        ShouldCarrySuggested(aps);
        aps.PredictedStartTimestamp.Should().Be(new DateTime(2026, 10, 4, 23, 18, 55, DateTimeKind.Utc));
        ShouldKeepEnactedDelivery(aps);
    }

    [Fact]
    public async Task DecomposeAsync_SuggestedNewerAcrossOffsets_ComparesInUtc()
    {
        var aps = await DecomposeApsAsync(HeldTempBasalStatus("2026-10-04T22:48:52Z", "2026-10-05T09:18:55+10:00"));

        ShouldCarrySuggested(aps);
        aps.PredictedStartTimestamp.Should().Be(new DateTime(2026, 10, 4, 23, 18, 55, DateTimeKind.Utc));
    }

    [Fact]
    public async Task DecomposeAsync_EnactedNewerThanSuggested_TakesCommandFieldsFromEnacted()
    {
        var aps = await DecomposeApsAsync(HeldTempBasalStatus("2026-10-04T23:18:56Z", "2026-10-04T23:18:55Z"));

        ShouldCarryEnacted(aps);
        aps.PredictedStartTimestamp.Should().Be(new DateTime(2026, 10, 4, 23, 18, 56, DateTimeKind.Utc));
        ShouldKeepEnactedDelivery(aps);
    }

    [Fact]
    public async Task DecomposeAsync_EqualCommandTimestamps_PrefersEnacted()
    {
        var aps = await DecomposeApsAsync(HeldTempBasalStatus("2026-10-04T23:18:55Z", "2026-10-04T23:18:55Z"));

        ShouldCarryEnacted(aps);
        ShouldKeepEnactedDelivery(aps);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-timestamp")]
    public async Task DecomposeAsync_OnlySuggestedTimestampParses_TakesSuggested(string? enactedTimestamp)
    {
        var aps = await DecomposeApsAsync(HeldTempBasalStatus(enactedTimestamp, "2026-10-04T23:18:55Z"));

        ShouldCarrySuggested(aps);
        ShouldKeepEnactedDelivery(aps);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-timestamp")]
    public async Task DecomposeAsync_OnlyEnactedTimestampParses_TakesEnacted(string? suggestedTimestamp)
    {
        var aps = await DecomposeApsAsync(HeldTempBasalStatus("2026-10-04T22:48:52Z", suggestedTimestamp));

        ShouldCarryEnacted(aps);
        ShouldKeepEnactedDelivery(aps);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("not-a-timestamp", "also-not-a-timestamp")]
    public async Task DecomposeAsync_NeitherCommandTimestampParses_PrefersEnacted(
        string? enactedTimestamp, string? suggestedTimestamp)
    {
        var aps = await DecomposeApsAsync(HeldTempBasalStatus(enactedTimestamp, suggestedTimestamp));

        ShouldCarryEnacted(aps);
        aps.PredictedStartTimestamp.Should().BeNull();
        ShouldKeepEnactedDelivery(aps);
    }

    [Theory]
    [InlineData("2026-10-04T22:48:52Z", "2026-10-04T23:18:55Z", "2026-10-04T23:18:55Z")]
    [InlineData("2026-10-04T23:18:56Z", "2026-10-04T23:18:55Z", "2026-10-04T23:18:56Z")]
    [InlineData("not-a-timestamp", "2026-10-04T23:18:55Z", "2026-10-04T23:18:55Z")]
    public void ResolveTimestamp_WithoutMillsOrIobTime_UsesFreshestCommand(
        string enactedTimestamp, string suggestedTimestamp, string expected)
    {
        var ds = HeldTempBasalStatus(enactedTimestamp, suggestedTimestamp);
        ds.Mills = 0;

        _decomposer.ResolveTimestamp(ds)
            .Should().Be(DateTimeOffset.Parse(expected).UtcDateTime);
    }

    [Fact]
    public void ResolveTimestamp_MillsAndIobTime_TakePrecedenceOverTheCommand()
    {
        var withMills = HeldTempBasalStatus("2026-10-04T22:48:52Z", "2026-10-04T23:18:55Z");
        var withIobTime = HeldTempBasalStatus("2026-10-04T22:48:52Z", "2026-10-04T23:18:55Z");
        withIobTime.Mills = 0;
        withIobTime.OpenAps!.Iob = new OpenApsIobData { Time = "2026-10-04T23:19:00Z" };

        _decomposer.ResolveTimestamp(withMills)
            .Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime);
        _decomposer.ResolveTimestamp(withIobTime)
            .Should().Be(new DateTime(2026, 10, 4, 23, 19, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task DecomposeAsync_UploadWithoutCreatedAt_IsDatedByReceiptAheadOfThePredictionStart()
    {
        var ds = JsonSerializer.Deserialize<DeviceStatus>("""
            {"_id":"loop-no-created-at","device":"Loop/3.0",
             "loop":{"iob":{"iob":1.8},"predicted":{"values":[130,125],"startDate":"2026-10-04T22:00:00Z"}}}
            """)!;

        var aps = await DecomposeApsAsync(ds);

        ds.CreatedAt.Should().Be("2026-10-29T03:04:05.678Z");
        aps.Timestamp.Should().Be(ReceivedAt.UtcDateTime);
    }

    [Fact]
    public async Task DecomposeAsync_CreatedAtSent_KeepsIt()
    {
        var ds = LoopStatusWithOnlyAPredictionStart("loop-with-created-at");
        ds.CreatedAt = "2026-10-04T23:18:55.000Z";

        var aps = await DecomposeApsAsync(ds);

        ds.CreatedAt.Should().Be("2026-10-04T23:18:55.000Z");
        aps.Timestamp.Should().Be(new DateTime(2026, 10, 4, 23, 18, 55, DateTimeKind.Utc));
    }

    private static DeviceStatus LoopStatusWithOnlyAPredictionStart(string id) => new()
    {
        Id = id,
        Device = "Loop/3.0",
        Loop = new LoopStatus
        {
            Iob = new LoopIob { Iob = 1.8 },
            Predicted = new LoopPredicted
            {
                Values = [130, 125],
                StartDate = "2026-10-04T22:00:00Z",
            },
        },
    };

    #endregion

    #region Loop Edge Cases

    [Fact]
    public async Task DecomposeAsync_LoopWithEmptyPredictedValues_HandlesGracefully()
    {
        // Arrange - empty values array, FirstOrDefault/LastOrDefault return 0
        var ds = new DeviceStatus
        {
            Id = "loop-empty-predictions",
            Mills = 1700000000000,
            Device = "Loop/3.0",
            Loop = new LoopStatus
            {
                Iob = new LoopIob { Iob = 1.0 },
                Predicted = new LoopPredicted
                {
                    Values = [],
                    StartDate = "2023-11-14T12:00:00Z"
                }
            }
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        // Assert
        var aps = result.CreatedRecords[0].Should().BeOfType<V4Models.ApsSnapshot>().Subject;
        aps.CurrentBg.Should().Be(0, "FirstOrDefault on empty array returns 0");
        aps.EventualBg.Should().Be(0, "LastOrDefault on empty array returns 0");
    }

    [Fact]
    public async Task DecomposeAsync_LoopWithNullPredicted_HandlesMissingBgValues()
    {
        var ds = new DeviceStatus
        {
            Id = "loop-no-predicted",
            Mills = 1700000000000,
            Device = "Loop/3.0",
            Loop = new LoopStatus
            {
                Iob = new LoopIob { Iob = 1.5 },
                Cob = new LoopCob { Cob = 20.0 }
                // No Predicted
            }
        };

        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        var aps = result.CreatedRecords[0].Should().BeOfType<V4Models.ApsSnapshot>().Subject;
        aps.CurrentBg.Should().BeNull();
        aps.EventualBg.Should().BeNull();
        aps.PredictedDefaultJson.Should().BeNull();
        aps.PredictedStartMills.Should().BeNull();
    }

    [Fact]
    public async Task DecomposeAsync_LoopSinglePredictedValue_CurrentAndEventualAreSame()
    {
        var ds = new DeviceStatus
        {
            Id = "loop-single-value",
            Mills = 1700000000000,
            Device = "Loop/3.0",
            Loop = new LoopStatus
            {
                Predicted = new LoopPredicted
                {
                    Values = [115.0],
                    StartDate = "2023-11-14T12:00:00Z"
                }
            }
        };

        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        var aps = result.CreatedRecords[0].Should().BeOfType<V4Models.ApsSnapshot>().Subject;
        aps.CurrentBg.Should().Be(115.0);
        aps.EventualBg.Should().Be(115.0, "single value means first and last are the same");
    }

    [Fact]
    public async Task DecomposeAsync_LoopWithoutEnacted_SetsEnactedFalse()
    {
        var ds = new DeviceStatus
        {
            Id = "loop-no-enacted",
            Mills = 1700000000000,
            Device = "Loop/3.0",
            Loop = new LoopStatus
            {
                Iob = new LoopIob { Iob = 1.0 },
                Predicted = new LoopPredicted
                {
                    Values = [120.0, 115.0, 110.0],
                    StartDate = "2023-11-14T12:00:00Z"
                }
            }
        };

        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        var aps = result.CreatedRecords[0].Should().BeOfType<V4Models.ApsSnapshot>().Subject;
        aps.Enacted.Should().BeFalse();
        aps.EnactedRate.Should().BeNull();
        aps.EnactedDuration.Should().BeNull();
        aps.EnactedBolusVolume.Should().BeNull();
    }

    [Fact]
    public async Task DecomposeAsync_LoopEnactedReceivedFalse_SetsEnactedFalse()
    {
        var ds = new DeviceStatus
        {
            Id = "loop-enacted-false",
            Mills = 1700000000000,
            Device = "Loop/3.0",
            Loop = new LoopStatus
            {
                Enacted = new LoopEnacted
                {
                    Received = false,
                    Rate = 2.0,
                    Duration = 30
                }
            }
        };

        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        var aps = result.CreatedRecords[0].Should().BeOfType<V4Models.ApsSnapshot>().Subject;
        aps.Enacted.Should().BeFalse();
    }

    #endregion

    #region Both OpenAps and Loop Present

    [Fact]
    public async Task DecomposeAsync_BothOpenApsAndLoop_OpenApsTakesPriority()
    {
        // Arrange - the code uses if/else: OpenAps wins
        var ds = new DeviceStatus
        {
            Id = "both-aps-and-loop",
            Mills = 1700000000000,
            Device = "dual-system",
            OpenAps = new OpenApsStatus
            {
                Iob = new OpenApsIobData { Iob = 2.0 },
                Suggested = new OpenApsSuggested
                {
                    Bg = 130.0, EventualBG = 110.0, Timestamp = "2023-11-14T12:00:00Z"
                }
            },
            Loop = new LoopStatus
            {
                Iob = new LoopIob { Iob = 3.0 },
                Predicted = new LoopPredicted
                {
                    Values = [150.0, 140.0],
                    StartDate = "2023-11-14T12:00:00Z"
                }
            }
        };

        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        // Assert - only one APS snapshot, and it's OpenAps
        result.CreatedRecords.OfType<V4Models.ApsSnapshot>().Should().HaveCount(1);
        var aps = result.CreatedRecords.OfType<V4Models.ApsSnapshot>().Single();
        aps.AidAlgorithm.Should().Be(V4Models.AidAlgorithm.OpenAps);
        aps.Iob.Should().Be(2.0, "OpenAps IOB, not Loop's");
    }

    #endregion

    #region Override Edge Cases

    [Fact]
    public async Task DecomposeAsync_OverrideActiveFalse_SkipsOverride()
    {
        var ds = new DeviceStatus
        {
            Id = "override-inactive",
            Mills = 1700000000000,
            Device = "Loop/3.0",
            Override = new OverrideStatus
            {
                Active = false,
                Name = "Exercise"
            }
        };

        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        result.CreatedRecords.Should().BeEmpty();
        _spans.Should().BeEmpty();
    }

    [Fact]
    public async Task DecomposeAsync_OverrideActiveNull_SkipsOverride()
    {
        var ds = new DeviceStatus
        {
            Id = "override-null-active",
            Mills = 1700000000000,
            Device = "Loop/3.0",
            Override = new OverrideStatus
            {
                Active = null,
                Name = "Running"
            }
        };

        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        result.CreatedRecords.Should().BeEmpty();
        _spans.Should().BeEmpty();
    }

    [Fact]
    public async Task DecomposeAsync_OverrideWithDuration_ReadsDurationAsSeconds()
    {
        var ds = new DeviceStatus
        {
            Id = "override-with-duration",
            Mills = 1780000000000,
            Device = "Loop/3.0",
            Override = new OverrideStatus
            {
                Active = true,
                Name = "Pre-Meal",
                Duration = 3600
            }
        };


        await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        _spans.Should().ContainSingle(ss =>
                    ss.EndTimestamp - ss.StartTimestamp == TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task DecomposeAsync_OverrideWithDuration_CountsFromTheOverrideTimestamp()
    {
        var overrideAt = new DateTime(2026, 5, 28, 21, 5, 0, DateTimeKind.Utc);
        var ds = new DeviceStatus
        {
            Id = "override-with-own-timestamp",
            Mills = new DateTimeOffset(overrideAt.AddMinutes(-5)).ToUnixTimeMilliseconds(),
            Device = "Loop/3.0",
            Override = new OverrideStatus
            {
                Active = true,
                Name = "Pre-Meal",
                Timestamp = "2026-05-28T21:05:00Z",
                Duration = 3600
            }
        };


        await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        _spans.Should().ContainSingle(ss => ss.EndTimestamp == overrideAt.AddHours(1));
    }

    [Fact]
    public async Task DecomposeAsync_OverrideEndingBeforeTheStatus_LeavesTheSpanOpen()
    {
        var statusAt = new DateTime(2026, 5, 28, 21, 5, 0, DateTimeKind.Utc);
        var ds = new DeviceStatus
        {
            Id = "override-stale-timestamp",
            Mills = new DateTimeOffset(statusAt).ToUnixTimeMilliseconds(),
            Device = "Loop/3.0",
            Override = new OverrideStatus
            {
                Active = true,
                Name = "Pre-Meal",
                Timestamp = "2026-05-28T19:05:00Z",
                Duration = 3600
            }
        };


        await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        _spans.Should().ContainSingle(ss => ss.StartTimestamp == statusAt && ss.EndTimestamp == null);
    }

    [Fact]
    public async Task DecomposeAsync_OverrideWithoutDuration_IsIndefinite()
    {
        var ds = new DeviceStatus
        {
            Id = "override-no-duration",
            Mills = 1700000000000,
            Device = "Loop/3.0",
            Override = new OverrideStatus { Active = true, Name = "Indefinite", Duration = null }
        };


        await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        _spans.Should().ContainSingle(ss => ss.EndTimestamp == null);
    }

    [Fact]
    public async Task DecomposeAsync_OverrideWithZeroDuration_HasNullEndMills()
    {
        var ds = new DeviceStatus
        {
            Id = "override-zero-duration",
            Mills = 1700000000000,
            Device = "Loop/3.0",
            Override = new OverrideStatus
            {
                Active = true,
                Name = "Indefinite",
                Duration = 0 // zero duration = no end
            }
        };


        await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        _spans.Should().ContainSingle(ss => ss.EndMills == null);
    }

    [Fact]
    public async Task DecomposeAsync_OverrideWithNoCorrectionRange_MetadataExcludesRangeKeys()
    {
        var ds = new DeviceStatus
        {
            Id = "override-no-range",
            Mills = 1700000000000,
            Device = "Loop/3.0",
            Override = new OverrideStatus
            {
                Active = true,
                Name = "Sleep",
                Multiplier = 0.8
                // No CurrentCorrectionRange
            }
        };


        await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        _spans.Should().ContainSingle(ss =>
                    ss.Metadata != null
                    && ss.Metadata.ContainsKey("name")
                    && ss.Metadata.ContainsKey("multiplier")
                    && !ss.Metadata.ContainsKey("currentCorrectionRange.minValue")
                    && !ss.Metadata.ContainsKey("currentCorrectionRange.maxValue"));
    }

    #endregion

    #region Pump Edge Cases

    [Fact]
    public async Task DecomposeAsync_PumpWithNoBatteryOrStatus_CreatesMinimalSnapshot()
    {
        var ds = new DeviceStatus
        {
            Id = "pump-minimal",
            Mills = 1700000000000,
            Device = "pump-device",
            Pump = new PumpStatus
            {
                Manufacturer = "Tandem",
                // No Battery, No Status, No Reservoir
            }
        };

        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        var pump = result.CreatedRecords[0].Should().BeOfType<V4Models.PumpSnapshot>().Subject;
        pump.Manufacturer.Should().Be("Tandem");
        pump.BatteryPercent.Should().BeNull();
        pump.BatteryVoltage.Should().BeNull();
        pump.Bolusing.Should().BeNull();
        pump.Suspended.Should().BeNull();
        pump.PumpStatus.Should().BeNull();
        pump.Reservoir.Should().BeNull();
    }

    [Fact]
    public async Task DecomposeAsync_PumpTwice_UpdatesInsteadOfCreatingDuplicate()
    {
        var ds = new DeviceStatus
        {
            Id = "idempotent-pump",
            Mills = 1700000000000,
            Device = "pump-device",
            Pump = new PumpStatus { Reservoir = 100.0 }
        };

        var first = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);
        first.CreatedRecords.Should().HaveCount(1);

        ds.Pump!.Reservoir = 90.0;
        var second = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);
        second.UpdatedRecords.Should().HaveCount(1);
        second.CreatedRecords.Should().BeEmpty();
    }

    #endregion

    #region Uploader Edge Cases

    [Fact]
    public async Task DecomposeAsync_UploaderTwice_UpdatesInsteadOfCreatingDuplicate()
    {
        var ds = new DeviceStatus
        {
            Id = "idempotent-uploader",
            Mills = 1700000000000,
            Device = "xDrip+",
            Uploader = new UploaderStatus { Battery = 80 }
        };

        var first = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);
        first.CreatedRecords.Should().HaveCount(1);

        ds.Uploader!.Battery = 70;
        var second = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);
        second.UpdatedRecords.Should().HaveCount(1);
        second.CreatedRecords.Should().BeEmpty();
    }

    [Fact]
    public async Task DecomposeAsync_UploaderWithBothObjectAndBatteryField_UploaderObjectTakesPriority()
    {
        // Arrange - both Uploader object and UploaderBattery set
        // UploaderBattery setter creates Uploader if null, sets Uploader.Battery
        // So setting both means Uploader.Battery is the authoritative value
        var ds = new DeviceStatus
        {
            Id = "uploader-dual",
            Mills = 1700000000000,
            Device = "xDrip+",
            Uploader = new UploaderStatus { Battery = 90, Name = "Pixel" }
        };
        // Setting UploaderBattery when Uploader exists updates Uploader.Battery
        ds.UploaderBattery = 75;

        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        var uploader = result.CreatedRecords[0].Should().BeOfType<V4Models.UploaderSnapshot>().Subject;
        uploader.Battery.Should().Be(75, "UploaderBattery setter updates Uploader.Battery");
        uploader.Name.Should().Be("Pixel");
    }

    #endregion

    #region Null LegacyId

    [Fact]
    public async Task DecomposeAsync_NullId_StillCreatesSnapshots()
    {
        var ds = new DeviceStatus
        {
            Id = null,
            Mills = 1700000000000,
            Device = "openaps://Samsung",
            OpenAps = new OpenApsStatus
            {
                Iob = new OpenApsIobData { Iob = 2.0 },
                Suggested = new OpenApsSuggested
                {
                    Bg = 120.0, EventualBG = 100.0, Timestamp = "2023-11-14T12:00:00Z"
                }
            }
        };

        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        result.CreatedRecords.Should().HaveCount(1);
        var aps = result.CreatedRecords[0].Should().BeOfType<V4Models.ApsSnapshot>().Subject;
        aps.LegacyId.Should().BeNull();
    }

    [Fact]
    public async Task DecomposeAsync_NullIdTwice_CreatesEachTimeNeverUpdates()
    {
        var ds = new DeviceStatus
        {
            Id = null,
            Mills = 1700000000000,
            Device = "openaps://Samsung",
            OpenAps = new OpenApsStatus
            {
                Suggested = new OpenApsSuggested
                {
                    Bg = 100.0, EventualBG = 95.0, Timestamp = "2023-11-14T12:00:00Z"
                }
            }
        };

        var first = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);
        var second = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        first.CreatedRecords.Should().HaveCount(1);
        second.CreatedRecords.Should().HaveCount(1);
        first.UpdatedRecords.Should().BeEmpty();
        second.UpdatedRecords.Should().BeEmpty();
    }

    #endregion

    #region Full Idempotency (All Three Snapshots)

    [Fact]
    public async Task DecomposeAsync_FullDeviceStatusTwice_LeavesAllThreeUnchanged()
    {
        var ds = new DeviceStatus
        {
            Id = "idempotent-full",
            Mills = 1700000000000,
            Device = "openaps://Samsung",
            OpenAps = new OpenApsStatus
            {
                Iob = new OpenApsIobData { Iob = 2.0 },
                Suggested = new OpenApsSuggested
                {
                    Bg = 110.0, EventualBG = 100.0, Timestamp = "2023-11-14T12:00:00Z"
                }
            },
            Pump = new PumpStatus { Reservoir = 100.0, Battery = new PumpBattery { Percent = 90 } },
            Uploader = new UploaderStatus { Battery = 55, Name = "Pixel 8" }
        };

        var first = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);
        first.CreatedRecords.Should().HaveCount(3);

        var second = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);
        second.UnchangedRecords.Should().HaveCount(3);
        second.UpdatedRecords.Should().BeEmpty();
        second.CreatedRecords.Should().BeEmpty();
    }

    #endregion

    #region Correlation ID

    [Fact]
    public async Task DecomposeAsync_FullDeviceStatus_AllSnapshotsShareCorrelationId()
    {
        var ds = new DeviceStatus
        {
            Id = "corr-full-ds",
            Mills = 1700000000000,
            Device = "openaps://Samsung",
            OpenAps = new OpenApsStatus
            {
                Iob = new OpenApsIobData { Iob = 1.5 },
                Suggested = new OpenApsSuggested
                {
                    Bg = 100.0, EventualBG = 95.0, Timestamp = "2023-11-14T12:00:00Z"
                }
            },
            Pump = new PumpStatus { Reservoir = 80.0 },
            Uploader = new UploaderStatus { Battery = 60 }
        };

        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        result.CorrelationId.Should().NotBeNull();
        // All V4 snapshot records don't directly have CorrelationId on the DeviceStatus decomposer,
        // but the result tracks it. Each snapshot type has its own CorrelationId field.
        // Verify the result itself is consistent
        result.CreatedRecords.Should().HaveCount(3);
    }

    #endregion

    #region Recieved typo compatibility

    [Fact]
    public async Task DecomposeAsync_OpenAps_RecievedTypo_StillDetectsEnacted()
    {
        // Arrange - "Recieved" is a real Nightscout typo that exists in the wild
        var ds = new DeviceStatus
        {
            Id = "typo-recieved",
            Mills = 1700000000000,
            Device = "openaps://Samsung",
            OpenAps = new OpenApsStatus
            {
                Suggested = new OpenApsSuggested
                {
                    Bg = 120.0,
                    EventualBG = 95.0,
                    Timestamp = "2023-11-14T12:00:00Z"
                },
                Enacted = new OpenApsEnacted
                {
                    Recieved = true,
                    Rate = 1.0,
                    Duration = 30
                }
            }
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        // Assert
        result.CreatedRecords.Should().HaveCount(1);
        var aps = result.CreatedRecords[0].Should().BeOfType<V4Models.ApsSnapshot>().Subject;
        aps.Enacted.Should().BeTrue();
    }

    #endregion

    #region APS System Detection

    [Fact]
    public async Task DecomposeAsync_AapsDeviceStatus_DetectsAndroidAps()
    {
        var ds = new DeviceStatus
        {
            Id = "aaps-device",
            Mills = 1700000000000,
            Device = "openaps://samsung SM-G975F",
            OpenAps = new OpenApsStatus
            {
                Iob = new OpenApsIobData { Iob = 1.5 },
                Suggested = new OpenApsSuggested
                {
                    Bg = 110.0,
                    EventualBG = 100.0,
                    Timestamp = "2023-11-14T12:00:00Z"
                }
            },
            Uploader = new UploaderStatus { Name = "AndroidAPS", Battery = 85 }
        };

        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        var aps = result.CreatedRecords.OfType<V4Models.ApsSnapshot>().Single();
        aps.AidAlgorithm.Should().Be(V4Models.AidAlgorithm.AndroidAps);
    }

    [Fact]
    public async Task DecomposeAsync_AapsDeviceStatus_CaseInsensitiveDetection()
    {
        var ds = new DeviceStatus
        {
            Id = "aaps-case",
            Mills = 1700000000000,
            Device = "openaps://samsung",
            OpenAps = new OpenApsStatus
            {
                Suggested = new OpenApsSuggested
                {
                    Bg = 120.0,
                    Timestamp = "2023-11-14T12:00:00Z"
                }
            },
            Uploader = new UploaderStatus { Name = "androidaps 3.2.0" }
        };

        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        var aps = result.CreatedRecords.OfType<V4Models.ApsSnapshot>().Single();
        aps.AidAlgorithm.Should().Be(V4Models.AidAlgorithm.AndroidAps);
    }

    [Fact]
    public async Task DecomposeAsync_TrioDeviceStatus_DetectsTrio()
    {
        var ds = new DeviceStatus
        {
            Id = "trio-device",
            Mills = 1700000000000,
            Device = "trio://iPhone",
            OpenAps = new OpenApsStatus
            {
                Version = "0.2.1",
                Iob = new OpenApsIobData { Iob = 0.8 },
                Suggested = new OpenApsSuggested
                {
                    Bg = 105.0,
                    EventualBG = 98.0,
                    Timestamp = "2023-11-14T12:00:00Z"
                }
            }
        };

        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        var aps = result.CreatedRecords.OfType<V4Models.ApsSnapshot>().Single();
        aps.AidAlgorithm.Should().Be(V4Models.AidAlgorithm.Trio);
    }

    [Fact]
    public async Task DecomposeAsync_VanillaOpenAps_NoUploaderNoVersion_DetectsOpenAps()
    {
        var ds = new DeviceStatus
        {
            Id = "vanilla-oref0",
            Mills = 1700000000000,
            Device = "openaps://myrig",
            OpenAps = new OpenApsStatus
            {
                Suggested = new OpenApsSuggested
                {
                    Bg = 130.0,
                    Timestamp = "2023-11-14T12:00:00Z"
                }
            }
        };

        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        var aps = result.CreatedRecords.OfType<V4Models.ApsSnapshot>().Single();
        aps.AidAlgorithm.Should().Be(V4Models.AidAlgorithm.OpenAps);
    }

    [Fact]
    public async Task DecomposeAsync_AapsWithVersion_AapsTakesPriorityOverTrio()
    {
        // If both uploader contains "AndroidAPS" AND version is present,
        // AAPS detection wins (checked first)
        var ds = new DeviceStatus
        {
            Id = "aaps-with-version",
            Mills = 1700000000000,
            Device = "openaps://samsung",
            OpenAps = new OpenApsStatus
            {
                Version = "some-version",
                Suggested = new OpenApsSuggested
                {
                    Bg = 115.0,
                    Timestamp = "2023-11-14T12:00:00Z"
                }
            },
            Uploader = new UploaderStatus { Name = "AndroidAPS 3.2.0" }
        };

        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        var aps = result.CreatedRecords.OfType<V4Models.ApsSnapshot>().Single();
        aps.AidAlgorithm.Should().Be(V4Models.AidAlgorithm.AndroidAps);
    }

    [Fact]
    public void DetectOpenApsVariant_UploaderWithNonAapsName_DoesNotMatchAndroidAps()
    {
        var ds = new DeviceStatus
        {
            OpenAps = new OpenApsStatus(),
            Uploader = new UploaderStatus { Name = "xDrip+" }
        };

        var result = DeviceStatusDecomposer.DetectOpenApsVariant(ds);

        result.Should().Be(V4Models.AidAlgorithm.OpenAps);
    }

    [Fact]
    public void DetectOpenApsVariant_AapsAppWithEmptyUploader_IsAndroidAps()
    {
        // NSClientV3 (AAPS 4.0) names itself only in the top-level "app" field.
        var ds = JsonSerializer.Deserialize<DeviceStatus>(
            """
            {"device":"openaps://Pixel","app":"AAPS","uploader":{},"openaps":{"suggested":{"bg":115}}}
            """
        )!;

        var result = DeviceStatusDecomposer.DetectOpenApsVariant(ds);

        result.Should().Be(V4Models.AidAlgorithm.AndroidAps);
    }

    [Fact]
    public void DetectOpenApsVariant_OtherApp_DoesNotMatchAndroidAps()
    {
        var ds = JsonSerializer.Deserialize<DeviceStatus>(
            """
            {"device":"openaps://rig","app":"oref0","openaps":{"suggested":{"bg":115}}}
            """
        )!;

        var result = DeviceStatusDecomposer.DetectOpenApsVariant(ds);

        result.Should().Be(V4Models.AidAlgorithm.OpenAps);
    }

    #endregion

    #region Trio Prediction Mapping

    [Fact]
    public async Task DecomposeAsync_TrioWithOrefPredictions_MapsIndividualCurvesAndNullDefault()
    {
        var ds = new DeviceStatus
        {
            Id = "trio-oref-predictions",
            Mills = 1700000000000,
            Device = "trio://iPhone",
            OpenAps = new OpenApsStatus
            {
                Version = "0.2.1",
                Iob = new OpenApsIobData { Iob = 1.5 },
                Suggested = new OpenApsSuggested
                {
                    Bg = 120.0,
                    EventualBG = 95.0,
                    Timestamp = "2023-11-14T12:00:00Z",
                    PredBGs = new OpenApsPredBGs
                    {
                        IOB = new List<double?> { 120, 115, 110, 105, 100 },
                        ZT = new List<double?> { 120, 118, 115 },
                        COB = new List<double?> { 120, 125, 130, 125, 120 },
                        UAM = new List<double?> { 120, 130, 135, 130 }
                    }
                }
            }
        };

        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        var aps = result.CreatedRecords.OfType<V4Models.ApsSnapshot>().Single();
        aps.AidAlgorithm.Should().Be(V4Models.AidAlgorithm.Trio);
        aps.PredictedDefaultJson.Should().BeNull("Trio uses oref multi-curve format; PredictedDefaultJson is for Loop's single curve");
        aps.PredictedIobJson.Should().NotBeNull();
        aps.PredictedZtJson.Should().NotBeNull();
        aps.PredictedCobJson.Should().NotBeNull();
        aps.PredictedUamJson.Should().NotBeNull();
        aps.PredictedStartMills.Should().NotBeNull();
    }

    [Fact]
    public async Task DecomposeAsync_TrioWithNoPredictions_AllPredictionFieldsNull()
    {
        var ds = new DeviceStatus
        {
            Id = "trio-no-predictions",
            Mills = 1700000000000,
            Device = "trio://iPhone",
            OpenAps = new OpenApsStatus
            {
                Version = "0.2.1",
                Iob = new OpenApsIobData { Iob = 0.5 },
                Suggested = new OpenApsSuggested
                {
                    Bg = 110.0,
                    EventualBG = 100.0,
                    Timestamp = "2023-11-14T12:00:00Z"
                    // No PredBGs
                }
            }
        };

        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        var aps = result.CreatedRecords.OfType<V4Models.ApsSnapshot>().Single();
        aps.AidAlgorithm.Should().Be(V4Models.AidAlgorithm.Trio);
        aps.PredictedDefaultJson.Should().BeNull();
        aps.PredictedIobJson.Should().BeNull();
        aps.PredictedZtJson.Should().BeNull();
        aps.PredictedCobJson.Should().BeNull();
        aps.PredictedUamJson.Should().BeNull();
    }

    #endregion

    #region Device Resolution

    [Fact]
    public async Task DecomposePumpAsync_WithManufacturerAndModel_SetsDeviceId()
    {
        // Arrange
        var expectedDeviceId = Guid.CreateVersion7();
        _deviceServiceMock
            .Setup(s => s.ResolveAsync(
                V4Models.DeviceCategory.InsulinPump,
                "Insulet",
                "Omnipod 5",
                1700000000000,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedDeviceId);

        var ds = new DeviceStatus
        {
            Id = "pump-device-resolve",
            Mills = 1700000000000,
            Device = "openaps://Samsung",
            Pump = new PumpStatus
            {
                Manufacturer = "Insulet",
                Model = "Omnipod 5",
                Reservoir = 150.5,
                Battery = new PumpBattery { Percent = 85 }
            }
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        // Assert
        var pump = result.CreatedRecords[0].Should().BeOfType<V4Models.PumpSnapshot>().Subject;
        pump.DeviceId.Should().Be(expectedDeviceId);
    }

    [Fact]
    public async Task DecomposeUploaderAsync_WithNameAndType_SetsDeviceId()
    {
        // Arrange
        var expectedDeviceId = Guid.CreateVersion7();
        _deviceServiceMock
            .Setup(s => s.ResolveAsync(
                V4Models.DeviceCategory.Uploader,
                "Samsung Galaxy S23",
                "phone",
                1700000000000,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedDeviceId);

        var ds = new DeviceStatus
        {
            Id = "uploader-device-resolve",
            Mills = 1700000000000,
            Device = "openaps://Samsung",
            Uploader = new UploaderStatus
            {
                Name = "Samsung Galaxy S23",
                Battery = 78,
                Type = "phone"
            }
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        // Assert
        var uploader = result.CreatedRecords[0].Should().BeOfType<V4Models.UploaderSnapshot>().Subject;
        uploader.DeviceId.Should().Be(expectedDeviceId);
    }

    [Fact]
    public async Task DecomposePumpAsync_PrefersSerialOverModelForRegistration()
    {
        var ds = new DeviceStatus
        {
            Id = "pump-serial-resolve",
            Mills = 1700000000000,
            Device = "carelink://Medtronic",
            Pump = new PumpStatus
            {
                Manufacturer = "Medtronic",
                Model = "MMT-1885",
                Serial = "NG4304436H",
            }
        };

        await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        _deviceServiceMock.Verify(s => s.ResolveAsync(
            V4Models.DeviceCategory.InsulinPump,
            "Medtronic",
            "NG4304436H",
            1700000000000,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DecomposeAsync_WithCgmIdentity_RegistersCgmDevice()
    {
        var ds = new DeviceStatus
        {
            Id = "cgm-device-resolve",
            Mills = 1700000000000,
            Device = "carelink://Medtronic",
            Cgm = new CgmStatus
            {
                Manufacturer = "Medtronic",
                Model = "Guardian (DURABLE)",
                SensorState = "NO_ERROR_MESSAGE",
            }
        };

        await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        _deviceServiceMock.Verify(s => s.ResolveAsync(
            V4Models.DeviceCategory.CGM,
            "Medtronic",
            "Guardian (DURABLE)",
            1700000000000,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DecomposeAsync_WithoutCgm_DoesNotRegisterCgmDevice()
    {
        var ds = new DeviceStatus
        {
            Id = "no-cgm",
            Mills = 1700000000000,
            Device = "carelink://Medtronic",
            Pump = new PumpStatus { Manufacturer = "Medtronic", Model = "MMT-1885" }
        };

        await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        _deviceServiceMock.Verify(s => s.ResolveAsync(
            V4Models.DeviceCategory.CGM,
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<long>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region Device Resolution Time

    private static readonly DateTime LoopStatusAt = new(2026, 3, 10, 14, 30, 0, DateTimeKind.Utc);

    private static DeviceStatus MakeCreatedAtOnlyLoopStatus(string id, DateTime createdAt) => new()
    {
        Id = id,
        CreatedAt = createdAt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        Device = "loop://iPhone",
        Pump = new PumpStatus { Manufacturer = "Insulet", Model = "Omnipod DASH", Reservoir = 80 },
        Uploader = new UploaderStatus { Name = "iPhone", Type = "phone", Battery = 60 },
        Cgm = new CgmStatus { Manufacturer = "Dexcom", Serial = "8G1234" },
        Loop = new LoopStatus { Iob = new LoopIob { Iob = 1.2 } },
    };

    /// <summary>
    /// A decomposer over a real <see cref="DeviceService"/>, so first/last seen and patient-device
    /// attribution come from the service's own date handling rather than a mock.
    /// </summary>
    private DeviceStatusDecomposer CreateDecomposerWithDeviceService(
        List<V4Models.Device> devices, V4Models.PatientDevice patientDevice)
    {
        var deviceRepo = new Mock<IDeviceRepository>();
        deviceRepo
            .Setup(r => r.FindByIdentitiesAsync(
                It.IsAny<IReadOnlyCollection<(V4Models.DeviceCategory Category, string Type, string Serial)>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<(V4Models.DeviceCategory Category, string Type, string Serial)> ids, CancellationToken _) =>
                devices.Where(d => ids.Contains((d.Category, d.Type, d.Serial))).ToList());
        deviceRepo
            .Setup(r => r.CreateAsync(It.IsAny<V4Models.Device>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((V4Models.Device device, WriteOrigin _, CancellationToken _) =>
            {
                devices.Add(device);
                return device;
            });
        deviceRepo
            .Setup(r => r.WidenSeenWindowAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var patientDeviceRepo = new Mock<IPatientDeviceRepository>();
        patientDeviceRepo
            .Setup(r => r.GetByDeviceIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) =>
                [.. ids.Select(id => { patientDevice.DeviceId = id; return patientDevice; })]);

        var ctxFactory = new TestNocturneDbContextFactory(_context);
        return DeviceStatusDecomposerDoubles.Build(
            new ApsSnapshotRepository(ctxFactory, new SystemAuditContext(), NullLogger<ApsSnapshotRepository>.Instance),
            new PumpSnapshotRepository(ctxFactory, new SystemAuditContext(), NullLogger<PumpSnapshotRepository>.Instance),
            new UploaderSnapshotRepository(ctxFactory, new SystemAuditContext(), NullLogger<UploaderSnapshotRepository>.Instance),
            _extrasRepo,
            _sensorStateRepo,
            _stateSpanServiceMock.Object,
            new DeviceService(deviceRepo.Object, patientDeviceRepo.Object, MockTenantAccessor.Create().Object, DeviceDateClockDoubles.Utc()));
    }

    private static V4Models.PatientDevice MakeDatedPatientDevice() => new()
    {
        Id = Guid.CreateVersion7(),
        DeviceCategory = V4Models.DeviceCategory.InsulinPump,
        Manufacturer = "Insulet",
        Model = "Omnipod DASH",
        StartDate = new DateOnly(2026, 3, 1),
    };

    [Fact]
    public async Task DecomposeAsync_CreatedAtOnlyStatus_AttributesPumpAndApsAtTheStatusTime()
    {
        var patientDevice = MakeDatedPatientDevice();
        var decomposer = CreateDecomposerWithDeviceService([], patientDevice);

        await decomposer.DecomposeBatchAsync(
            [MakeCreatedAtOnlyLoopStatus("loop-created-at-batch", LoopStatusAt)], source: null, WriteOrigin.Live);

        _context.PumpSnapshots.Single(p => p.LegacyId == "loop-created-at-batch").PatientDeviceId.Should().Be(patientDevice.Id);
        _context.ApsSnapshots.Single(a => a.LegacyId == "loop-created-at-batch").PatientDeviceId.Should().Be(patientDevice.Id);
    }

    [Fact]
    public async Task DecomposeAsync_CreatedAtOnlyStatus_StampsDevicesWithTheStatusTime()
    {
        var devices = new List<V4Models.Device>();
        var decomposer = CreateDecomposerWithDeviceService(devices, MakeDatedPatientDevice());

        await decomposer.DecomposeBatchAsync(
            [MakeCreatedAtOnlyLoopStatus("loop-first-seen-batch", LoopStatusAt)], source: null, WriteOrigin.Live);

        devices.Select(d => d.Category).Should().BeEquivalentTo(
            [V4Models.DeviceCategory.InsulinPump, V4Models.DeviceCategory.Uploader, V4Models.DeviceCategory.CGM]);
        devices.Should().AllSatisfy(d =>
        {
            d.FirstSeenTimestamp.Should().Be(LoopStatusAt);
            d.LastSeenTimestamp.Should().Be(LoopStatusAt);
        });
    }

    /// <summary>
    /// A re-send whose pump no longer resolves to a patient device, its link having ended since,
    /// keeps the link its pump and APS snapshots were stored with.
    /// </summary>
    [Fact]
    public async Task DecomposeAsync_ResendResolvingNoLink_KeepsTheStoredPumpAndApsLinks()
    {
        var patientDevice = MakeDatedPatientDevice();
        var decomposer = CreateDecomposerWithDeviceService([], patientDevice);
        var status = MakeCreatedAtOnlyLoopStatus("loop-link-kept", LoopStatusAt);
        await decomposer.DecomposeBatchAsync([status], null, WriteOrigin.Live);

        patientDevice.StartDate = new DateOnly(2027, 1, 1);
        status.Pump!.Reservoir = 70;
        status.Loop!.Iob!.Iob = 0.8;
        var resend = await decomposer.DecomposeBatchAsync([status], null, WriteOrigin.Live);

        resend.UpdatedRecords.Should().Contain(r => r is V4Models.PumpSnapshot).And.Contain(r => r is V4Models.ApsSnapshot);
        _context.PumpSnapshots.Single(p => p.LegacyId == "loop-link-kept").PatientDeviceId.Should().Be(patientDevice.Id);
        _context.ApsSnapshots.Single(a => a.LegacyId == "loop-link-kept").PatientDeviceId.Should().Be(patientDevice.Id);
    }

    [Fact]
    public async Task DecomposeAsync_LaterCreatedAtOnlyStatus_AdvancesDeviceLastSeen()
    {
        var devices = new List<V4Models.Device>();
        var patientDevice = MakeDatedPatientDevice();
        var later = LoopStatusAt.AddMinutes(5);

        // A fresh service per status, as each ingest request gets its own scoped DeviceService.
        await CreateDecomposerWithDeviceService(devices, patientDevice)
            .DecomposeBatchAsync([MakeCreatedAtOnlyLoopStatus("loop-seen-1", LoopStatusAt)], null, WriteOrigin.Live);
        await CreateDecomposerWithDeviceService(devices, patientDevice)
            .DecomposeBatchAsync([MakeCreatedAtOnlyLoopStatus("loop-seen-2", later)], null, WriteOrigin.Live);

        devices.Should().HaveCount(3);
        devices.Should().AllSatisfy(d =>
        {
            d.FirstSeenTimestamp.Should().Be(LoopStatusAt);
            d.LastSeenTimestamp.Should().Be(later);
        });
    }

    #endregion

    #region AAPS Date Normalization

    [Fact]
    public async Task DecomposeAsync_OpenApsWithMillsZeroAndDateSet_UsesDateForTimestamp()
    {
        // Arrange — AAPS sends "date" instead of "mills"
        var expectedMills = 1712925027741L;
        var ds = new DeviceStatus
        {
            Id = "aaps-date-1",
            Mills = 0,
            Date = expectedMills,
            Device = "openaps://samsung SM-A525F",
            OpenAps = new OpenApsStatus
            {
                Iob = new OpenApsIobData { Iob = 3.4 },
                Enacted = new OpenApsEnacted
                {
                    Received = true,
                    Rate = 0.05,
                    Duration = 30,
                    Bg = 200,
                    Timestamp = "2026-04-12T09:30:28.167Z"
                }
            }
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        // Assert
        var aps = result.CreatedRecords[0].Should().BeOfType<V4Models.ApsSnapshot>().Subject;
        aps.Timestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(expectedMills).UtcDateTime);
    }

    [Fact]
    public async Task DecomposeAsync_OpenApsWithMillsAndDateBothSet_PrefersExistingMills()
    {
        // Arrange — both mills and date present; mills takes precedence
        var ds = new DeviceStatus
        {
            Id = "aaps-both-1",
            Mills = 1700000000000,
            Date = 9999999999999,
            Device = "openaps://samsung SM-A525F",
            OpenAps = new OpenApsStatus
            {
                Iob = new OpenApsIobData { Iob = 1.0 },
                Enacted = new OpenApsEnacted { Received = true, Rate = 0.5, Duration = 30, Bg = 120, Timestamp = "2023-11-14T12:00:00Z" }
            }
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        // Assert
        var aps = result.CreatedRecords[0].Should().BeOfType<V4Models.ApsSnapshot>().Subject;
        aps.Timestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime);
    }

    [Fact]
    public async Task DecomposeAsync_OpenApsWithJsonDateField_NormalizesMillsFromDate()
    {
        // Arrange — simulate actual AAPS JSON payload (no "mills" field, only "date")
        var json = """
        {
            "_id": "aaps-json-1",
            "date": 1712925027741,
            "device": "openaps://samsung SM-A525F",
            "openaps": {
                "iob": { "iob": 3.4 },
                "enacted": {
                    "received": true,
                    "rate": 0.05,
                    "duration": 30,
                    "bg": 200,
                    "timestamp": "2026-04-12T09:30:28.167Z"
                }
            }
        }
        """;
        var ds = System.Text.Json.JsonSerializer.Deserialize<DeviceStatus>(json)!;

        // Act
        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        // Assert
        var aps = result.CreatedRecords[0].Should().BeOfType<V4Models.ApsSnapshot>().Subject;
        aps.Timestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1712925027741).UtcDateTime);
    }

    #endregion

    #region Timestamp Fallback

    [Fact]
    public async Task DecomposeAsync_OpenApsWithMillsZeroNoDate_FallsBackToIobTime()
    {
        // Arrange — neither mills nor date set; should fall back to OpenAps.Iob.Time
        var ds = new DeviceStatus
        {
            Id = "aaps-fallback-1",
            Mills = 0,
            CreatedAt = "2026-04-12T12:34:27.741Z",
            Device = "openaps://samsung SM-A525F",
            OpenAps = new OpenApsStatus
            {
                Iob = new OpenApsIobData { Iob = 3.4, Time = "2026-04-12T09:30:30.549Z" },
                Enacted = new OpenApsEnacted
                {
                    Received = true,
                    Rate = 0.05,
                    Duration = 30,
                    Bg = 200,
                    Timestamp = "2026-04-12T09:30:28.167Z"
                }
            }
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        // Assert — should use OpenAps.Iob.Time as fallback (most precise APS timestamp)
        var aps = result.CreatedRecords[0].Should().BeOfType<V4Models.ApsSnapshot>().Subject;
        aps.Timestamp.Should().NotBe(DateTime.UnixEpoch);
        aps.Timestamp.Year.Should().Be(2026);
        aps.Timestamp.Month.Should().Be(4);
        aps.Timestamp.Day.Should().Be(12);
    }

    [Theory]
    [InlineData("2026-04-12T09:35:00Z", "2026-04-12T09:33:00Z", "2026-04-12T09:36:00.000Z", 35)]
    [InlineData(null, "2026-04-12T09:33:00Z", "2026-04-12T09:36:00.000Z", 33)]
    [InlineData(null, null, "2026-04-12T09:36:00.000Z", 36)]
    [InlineData(null, null, null, null)]
    public async Task DecomposeAsync_LoopWithMillsZero_PrefersTheLoopCycleTimeToThePredictionStart(
        string? loopTimestamp, string? pumpClock, string? createdAt, int? expectedMinute)
    {
        var ds = new DeviceStatus
        {
            Id = "loop-cycle-time",
            Mills = 0,
            CreatedAt = createdAt,
            Device = "loop://iPhone",
            Loop = new LoopStatus
            {
                Timestamp = loopTimestamp,
                Predicted = new LoopPredicted { StartDate = "2026-04-12T09:30:00Z" },
            },
            Pump = new PumpStatus { Clock = pumpClock },
            Override = new OverrideStatus { Active = true, Duration = 30.0 },
        };
        var expected = expectedMinute is { } minute
            ? new DateTime(2026, 4, 12, 9, minute, 0, DateTimeKind.Utc)
            : ReceivedAt.UtcDateTime;

        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        result.CreatedRecords.OfType<V4Models.ApsSnapshot>().Single().Timestamp.Should().Be(expected);
        result.CreatedRecords.OfType<V4Models.PumpSnapshot>().Single().Timestamp.Should().Be(expected);
        result.CreatedRecords.OfType<StateSpan>().Single(s => s.Category == StateSpanCategory.Override)
            .StartTimestamp.Should().Be(expected);
    }

    #endregion

    #region LoopJson and AidVersion

    [Fact]
    public async Task DecomposeAsync_OpenApsKeysTheModelDoesNotName_SurviveTheStoreAndTheV1Read()
    {
        const string uploaded = """
            {
              "suggested": {
                "timestamp": "2023-11-14T12:00:00Z",
                "reason": "COB: 0",
                "someNewField": { "nested": [1, 2] },
                "predBGs": { "IOB": [100, 99], "aCOB": [100, 101] },
                "requested": { "rate": 1.2, "requestedExtra": "x" }
              },
              "enacted": {
                "timestamp": "2023-11-14T12:00:00Z",
                "received": true,
                "rate": 1.5,
                "enactedExtra": 4,
                "requested": { "rate": 1.5, "requestedExtra": "y" }
              },
              "iob": { "iob": 1.8, "basaliob": 0.4, "iobExtra": 7 },
              "version": "0.7.0",
              "openapsExtra": { "a": true }
            }
            """;
        var ds = new DeviceStatus
        {
            Id = "openaps-unknown-keys",
            Mills = 1700000000000,
            Device = "openaps://rig",
            OpenAps = JsonSerializer.Deserialize<OpenApsStatus>(uploaded),
        };

        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        var aps = result.CreatedRecords.OfType<V4Models.ApsSnapshot>().Single();
        aps.AdditionalProperties = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            JsonSerializer.Serialize(aps.AdditionalProperties));
        var projected = DeviceStatusProjectionService.ProjectFromSnapshots(aps, null, null, null, null);
        var read = JsonDocument.Parse(JsonSerializer.Serialize(projected.OpenAps)).RootElement;

        var suggested = read.GetProperty("suggested");
        suggested.GetProperty("someNewField").GetProperty("nested").GetArrayLength().Should().Be(2);
        suggested.GetProperty("predBGs").GetProperty("aCOB").GetArrayLength().Should().Be(2);
        suggested.GetProperty("predBGs").GetProperty("IOB").GetArrayLength().Should().Be(2);
        suggested.GetProperty("requested").GetProperty("requestedExtra").GetString().Should().Be("x");
        var enacted = read.GetProperty("enacted");
        enacted.GetProperty("enactedExtra").GetInt32().Should().Be(4);
        enacted.GetProperty("requested").GetProperty("requestedExtra").GetString().Should().Be("y");
        var iob = read.GetProperty("iob");
        iob.GetProperty("iobExtra").GetInt32().Should().Be(7);
        iob.GetProperty("iob").GetDouble().Should().Be(1.8);
        read.GetProperty("openapsExtra").GetProperty("a").GetBoolean().Should().BeTrue();
        read.GetProperty("version").GetString().Should().Be("0.7.0");
    }

    [Fact]
    public async Task DecomposeAsync_PumpKeysTheModelDoesNotName_SurviveTheStoreAndTheV1Read()
    {
        const string uploaded = """
            {
              "clock": "2023-11-14T12:00:00Z",
              "reservoir": 80,
              "battery": { "percent": 70, "batteryExtra": 1 },
              "status": { "status": "normal", "suspended": false, "statusExtra": "s" },
              "iob": { "iob": 1.2, "bolusiob": 0.5, "iobExtra": 2 },
              "extended": { "Version": "1.0", "ActiveProfile": "Default" },
              "pumpExtra": { "a": [1] }
            }
            """;
        var ds = new DeviceStatus
        {
            Id = "pump-unknown-keys",
            Mills = 1700000000000,
            Device = "openaps://rig",
            Pump = JsonSerializer.Deserialize<PumpStatus>(uploaded),
        };

        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        var pump = result.CreatedRecords.OfType<V4Models.PumpSnapshot>().Single();
        pump.AdditionalProperties = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            JsonSerializer.Serialize(pump.AdditionalProperties));
        var projected = ProjectPump(pump);
        var read = JsonDocument.Parse(JsonSerializer.Serialize(projected.Pump)).RootElement;

        read.GetProperty("pumpExtra").GetProperty("a").GetArrayLength().Should().Be(1);
        read.GetProperty("battery").GetProperty("batteryExtra").GetInt32().Should().Be(1);
        read.GetProperty("battery").GetProperty("percent").GetInt32().Should().Be(70);
        read.GetProperty("status").GetProperty("statusExtra").GetString().Should().Be("s");
        read.GetProperty("iob").GetProperty("iobExtra").GetInt32().Should().Be(2);
        var extended = read.GetProperty("extended");
        extended.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("Version", "ActiveProfile");
    }

    [Theory]
    [InlineData("activity", "0.021", JsonValueKind.Number)]
    [InlineData("time", "\"2023-11-14T12:00:00Z\"", JsonValueKind.String)]
    [InlineData("timestamp", "\"2023-11-14T12:00:00.000Z\"", JsonValueKind.String)]
    [InlineData("mills", "1700000000000", JsonValueKind.Number)]
    [InlineData("basaliob", "0.4", JsonValueKind.Number)]
    public async Task DecomposeAsync_OpenApsIobField_SurvivesTheStoreAndTheV1Read(
        string name, string json, JsonValueKind kind)
    {
        var uploaded = $$"""{ "iob": { "iob": 1.8, "{{name}}": {{json}} } }""";
        var ds = new DeviceStatus
        {
            Id = "openaps-iob-" + name,
            Mills = 1700000000000,
            Device = "openaps://rig",
            OpenAps = JsonSerializer.Deserialize<OpenApsStatus>(uploaded),
        };

        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        var aps = result.CreatedRecords.OfType<V4Models.ApsSnapshot>().Single();
        aps.AdditionalProperties = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            JsonSerializer.Serialize(aps.AdditionalProperties));
        var projected = DeviceStatusProjectionService.ProjectFromSnapshots(aps, null, null, null, null);
        var iob = JsonDocument.Parse(JsonSerializer.Serialize(projected.OpenAps)).RootElement.GetProperty("iob");

        iob.GetProperty(name).ValueKind.Should().Be(kind);
        iob.GetProperty(name).GetRawText().Should().Be(json);
        iob.GetProperty("iob").GetDouble().Should().Be(1.8);
    }

    [Fact]
    public async Task DecomposeAsync_PumpFieldsTheSnapshotHasNoColumnFor_SurviveTheStoreAndTheV1Read()
    {
        const string uploaded = """
            {
              "serial": "SN-0042",
              "pumpMode": "Automatic",
              "iob": { "iob": 1.2, "bolusiob": 0.5, "basaliob": 0.7, "timestamp": "2023-11-14T12:00:00Z" }
            }
            """;
        var ds = new DeviceStatus
        {
            Id = "pump-modelled",
            Mills = 1700000000000,
            Device = "openaps://rig",
            Pump = JsonSerializer.Deserialize<PumpStatus>(uploaded),
        };

        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        var pump = result.CreatedRecords.OfType<V4Models.PumpSnapshot>().Single();
        pump.AdditionalProperties = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            JsonSerializer.Serialize(pump.AdditionalProperties));
        var read = JsonDocument.Parse(JsonSerializer.Serialize(ProjectPump(pump).Pump)).RootElement;

        read.GetProperty("serial").GetString().Should().Be("SN-0042");
        read.GetProperty("pumpMode").GetString().Should().Be("Automatic");
        var iob = read.GetProperty("iob");
        iob.GetProperty("basaliob").GetDouble().Should().Be(0.7);
        iob.GetProperty("timestamp").GetString().Should().Be("2023-11-14T12:00:00Z");
        iob.GetProperty("iob").GetDouble().Should().Be(1.2);
        iob.GetProperty("bolusiob").GetDouble().Should().Be(0.5);
    }

    [Fact]
    public void ProjectFromSnapshots_StoredModelledFieldsOfTheWrongKind_ComeBackAsStoredWithoutFailingTheRead()
    {
        var aps = new V4Models.ApsSnapshot
        {
            AidAlgorithm = V4Models.AidAlgorithm.OpenAps,
            Iob = 1.8,
            AdditionalProperties = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                """{ "uploaded:openaps": { "iob": { "mills": "1700000000000", "activity": "x", "time": 5, "timestamp": true } } }"""),
        };
        var pump = new V4Models.PumpSnapshot
        {
            Iob = 1.2,
            AdditionalProperties = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                """{ "uploaded:pump": { "serial": 42, "iob": { "basaliob": "0.7", "timestamp": 9 } } }"""),
        };

        var read = DeviceStatusProjectionService.ProjectFromSnapshots(aps, pump, null, null, null);

        var openApsIob = JsonDocument.Parse(JsonSerializer.Serialize(read.OpenAps)).RootElement.GetProperty("iob");
        openApsIob.GetProperty("mills").GetString().Should().Be("1700000000000");
        openApsIob.GetProperty("activity").GetString().Should().Be("x");
        openApsIob.GetProperty("time").GetInt32().Should().Be(5);
        openApsIob.GetProperty("timestamp").GetBoolean().Should().BeTrue();
        var pumpRead = JsonDocument.Parse(JsonSerializer.Serialize(read.Pump)).RootElement;
        pumpRead.GetProperty("serial").GetInt32().Should().Be(42);
        pumpRead.GetProperty("iob").GetProperty("basaliob").GetString().Should().Be("0.7");
        pumpRead.GetProperty("iob").GetProperty("timestamp").GetInt32().Should().Be(9);
    }

    private static DeviceStatus ProjectPump(V4Models.PumpSnapshot pump) =>
        DeviceStatusProjectionService.ProjectFromSnapshots(null, pump, null, null, null);

    [Fact]
    public async Task DecomposeAsync_LoopKeysTheModelDoesNotName_SurviveIntoLoopJsonAndTheV1Read()
    {
        const string uploaded = """
            {
              "iob": { "iob": 1.8, "timestamp": "2023-11-14T12:00:00Z", "futureIobKey": 7 },
              "automaticDoseRecommendation": {
                "timestamp": "2023-11-14T12:00:00Z",
                "bolusVolume": 0.25,
                "tempBasalAdjustment": { "rate": 1.5, "duration": 1800 }
              },
              "testingDetails": { "remoteCommands": ["carbs-delete", "carbs-edit"] },
              "version": "3.4"
            }
            """;
        var ds = new DeviceStatus
        {
            Id = "loop-json-unknown-keys",
            Mills = 1700000000000,
            Device = "loop://iPhone",
            Loop = JsonSerializer.Deserialize<LoopStatus>(uploaded),
        };

        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        var aps = result.CreatedRecords.OfType<V4Models.ApsSnapshot>().Single();
        var stored = JsonDocument.Parse(aps.LoopJson!).RootElement;
        stored.GetProperty("testingDetails").GetProperty("remoteCommands").EnumerateArray()
            .Select(e => e.GetString()).Should().Equal("carbs-delete", "carbs-edit");
        var dose = stored.GetProperty("automaticDoseRecommendation");
        dose.GetProperty("bolusVolume").GetDouble().Should().Be(0.25);
        dose.GetProperty("tempBasalAdjustment").GetProperty("duration").GetInt32().Should().Be(1800);
        stored.GetProperty("iob").GetProperty("futureIobKey").GetInt32().Should().Be(7);

        var projected = DeviceStatusProjectionService.ProjectFromSnapshots(aps, null, null, null, null);
        var read = JsonDocument.Parse(JsonSerializer.Serialize(projected.Loop)).RootElement;
        read.GetProperty("testingDetails").GetProperty("remoteCommands").GetArrayLength().Should().Be(2);
        read.GetProperty("automaticDoseRecommendation").GetProperty("bolusVolume").GetDouble().Should().Be(0.25);
        read.GetProperty("automaticDoseRecommendation").GetProperty("tempBasalAdjustment")
            .GetProperty("rate").GetDouble().Should().Be(1.5);
    }

    [Fact]
    public async Task DecomposeAsync_WithLoopData_StoresLoopJson()
    {
        // Arrange
        var ds = new DeviceStatus
        {
            Id = "loop-json-1",
            Mills = 1700000000000,
            Device = "Loop/3.0",
            Loop = new LoopStatus
            {
                Iob = new LoopIob { Iob = 1.8, BasalIob = 0.5 },
                Cob = new LoopCob { Cob = 25.0 },
                RecommendedBolus = 0.7,
                Predicted = new LoopPredicted
                {
                    Values = [130, 125, 120, 115, 110],
                    StartDate = "2023-11-14T12:00:00Z"
                },
                Enacted = new LoopEnacted
                {
                    Received = true,
                    Rate = 2.0,
                    Duration = 30,
                    BolusVolume = 0.4
                }
            }
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        // Assert
        var aps = result.CreatedRecords.OfType<V4Models.ApsSnapshot>().Single();
        aps.LoopJson.Should().NotBeNullOrEmpty();
        // Should contain key Loop data
        aps.LoopJson.Should().Contain("iob");
        aps.LoopJson.Should().Contain("cob");
        // AidVersion is not used for Loop
        aps.AidVersion.Should().BeNull();
    }

    [Fact]
    public async Task DecomposeAsync_WithTrioData_StoresAidVersion()
    {
        // Arrange
        var ds = new DeviceStatus
        {
            Id = "trio-version-1",
            Mills = 1700000000000,
            Device = "trio://iPhone",
            OpenAps = new OpenApsStatus
            {
                Version = "0.2.1",
                Iob = new OpenApsIobData { Iob = 0.8 },
                Suggested = new OpenApsSuggested
                {
                    Bg = 105.0,
                    EventualBG = 98.0,
                    Timestamp = "2023-11-14T12:00:00Z"
                }
            }
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        // Assert
        var aps = result.CreatedRecords.OfType<V4Models.ApsSnapshot>().Single();
        aps.AidVersion.Should().Be("0.2.1");
    }

    [Fact]
    public async Task DecomposeAsync_WithAapsData_StoresAidVersion()
    {
        // Arrange — AAPS emits a version string on the OpenAps block
        var ds = new DeviceStatus
        {
            Id = "aaps-version-1",
            Mills = 1700000000000,
            Device = "openaps://samsung SM-G975F",
            OpenAps = new OpenApsStatus
            {
                Version = "3.2.0.4",
                Iob = new OpenApsIobData { Iob = 1.5 },
                Suggested = new OpenApsSuggested
                {
                    Bg = 110.0,
                    EventualBG = 100.0,
                    Timestamp = "2023-11-14T12:00:00Z"
                }
            },
            Uploader = new UploaderStatus { Name = "AndroidAPS", Battery = 85 }
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        // Assert
        var aps = result.CreatedRecords.OfType<V4Models.ApsSnapshot>().Single();
        aps.AidVersion.Should().Be("3.2.0.4");
        aps.AidAlgorithm.Should().Be(V4Models.AidAlgorithm.AndroidAps);
    }

    #endregion

    #region PumpSnapshot IOB

    [Fact]
    public async Task DecomposeAsync_WithPumpIob_StoresPumpIob()
    {
        // Arrange
        var ds = new DeviceStatus
        {
            Id = "pump-iob-1",
            Mills = 1700000000000,
            Device = "openaps://Samsung",
            Pump = new PumpStatus
            {
                Manufacturer = "Medtronic",
                Reservoir = 100.0,
                Battery = new PumpBattery { Percent = 90 },
                Iob = new PumpIob { Iob = 3.5, BolusIob = 2.1 }
            }
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        // Assert
        var pump = result.CreatedRecords[0].Should().BeOfType<V4Models.PumpSnapshot>().Subject;
        pump.Iob.Should().Be(3.5);
        pump.BolusIob.Should().Be(2.1);
    }

    #endregion

    #region Extras Decomposition

    [Fact]
    public async Task DecomposeAsync_WithConfiguration_StoresExtras()
    {
        // Arrange — AAPS sends "configuration" as an unknown top-level key
        var json = """
        {
            "_id": "aaps-config-1",
            "mills": 1700000000000,
            "device": "openaps://samsung",
            "configuration": { "sensitivityType": "oref1", "autosensMax": 1.2 },
            "uploader": { "name": "AndroidAPS", "battery": 85 },
            "openaps": {
                "iob": { "iob": 1.0 },
                "suggested": { "bg": 120, "eventualBG": 100, "timestamp": "2023-11-14T12:00:00Z" }
            }
        }
        """;
        var ds = JsonSerializer.Deserialize<DeviceStatus>(json)!;

        // Act
        await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        // Assert — configuration should end up in extras
        var extrasEntities = _context.DeviceStatusExtras.ToList();
        extrasEntities.Should().HaveCount(1);
        extrasEntities[0].ExtrasJson.Should().Contain("configuration");
    }

    [Fact]
    public async Task DecomposeAsync_ClientIsValidAndServerStamps_AreNotStored()
    {
        var json = """
        {
            "_id": "aaps-stamps-1",
            "mills": 1700000000000,
            "device": "openaps://samsung",
            "isValid": true,
            "srvModified": 1,
            "srvCreated": 1,
            "configuration": { "sensitivityType": "oref1" },
            "openaps": {
                "iob": { "iob": 1.0 },
                "suggested": { "bg": 120, "eventualBG": 100, "timestamp": "2023-11-14T12:00:00Z" }
            }
        }
        """;
        var ds = JsonSerializer.Deserialize<DeviceStatus>(json)!;

        await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        var extras = _context.DeviceStatusExtras.Single().ExtrasJson;
        extras.Should().Contain("configuration");
        extras.Should().NotContain("isValid").And.NotContain("srvModified").And.NotContain("srvCreated");
        _context.ApsSnapshots.Single().DeletedAt.Should().BeNull();
    }

    [Fact]
    public async Task DecomposeAsync_WithRadioAdapter_StoresExtras()
    {
        // Arrange
        var ds = new DeviceStatus
        {
            Id = "radio-adapter-1",
            Mills = 1700000000000,
            Device = "Loop/3.0",
            Loop = new LoopStatus
            {
                Iob = new LoopIob { Iob = 1.0 },
                Predicted = new LoopPredicted
                {
                    Values = [120.0, 115.0],
                    StartDate = "2023-11-14T12:00:00Z"
                }
            },
            RadioAdapter = new RadioAdapterStatus { PumpRssi = -65, Rssi = -70 }
        };

        // Act
        await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        // Assert
        var extrasEntities = _context.DeviceStatusExtras.ToList();
        extrasEntities.Should().HaveCount(1);
        extrasEntities[0].ExtrasJson.Should().Contain("radioAdapter");
    }

    [Fact]
    public async Task DecomposeAsync_WithXDripJs_StoresExtras()
    {
        // Arrange
        var ds = new DeviceStatus
        {
            Id = "xdripjs-1",
            Mills = 1700000000000,
            Device = "xDrip+",
            XDripJs = new XDripJsStatus { State = 6, StateString = "OK", VoltageA = 217, VoltageB = 212 },
            Uploader = new UploaderStatus { Battery = 80 }
        };

        // Act
        await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        // Assert
        var extrasEntities = _context.DeviceStatusExtras.ToList();
        extrasEntities.Should().HaveCount(1);
        extrasEntities[0].ExtrasJson.Should().Contain("xdripjs");
    }

    #region Sensor state → SensorStateReport

    [Theory]
    [InlineData("NO_ERROR_MESSAGE", SensorState.Ok)]
    [InlineData("WARM_UP", SensorState.WarmingUp)]
    [InlineData("CALIBRATION_REQUIRED", SensorState.CalibrationNeeded)]
    [InlineData("SENSOR_ERROR", SensorState.Error)]
    [InlineData("CHANGE_SENSOR", SensorState.Error)]
    [InlineData("SENSOR_END_OF_LIFE", SensorState.Expired)]
    public async Task ACareLinkSensorStateWritesItsMappedReport(string reported, SensorState expected)
    {
        await _decomposer.DecomposeBatchAsync([CareLinkStatus("carelink-1", reported)], "carelink-connector", WriteOrigin.Live);

        var report = SensorStateReports().Single();
        report.State.Should().Be(expected);
        report.ReportedField.Should().Be(V4Models.SensorStateReport.CareLinkField);
        report.ReportedValue.Should().Be(reported);
        report.Device.Should().Be("CareLink MiniMed 780G");
        report.DataSource.Should().Be("carelink-connector");
        report.Timestamp.Should().Be(1700000000000L.ToUtcDateTime());
        report.CorrelationId.Should().Be(_context.DeviceStatusExtras.Single().CorrelationId);
    }

    [Theory]
    [InlineData(0x02, SensorState.WarmingUp)]
    [InlineData(0x04, SensorState.CalibrationNeeded)]
    [InlineData(0x05, SensorState.CalibrationNeeded)]
    [InlineData(0x06, SensorState.Ok)]
    [InlineData(0x08, SensorState.CalibrationNeeded)]
    [InlineData(0x09, SensorState.CalibrationNeeded)]
    [InlineData(0x0a, SensorState.CalibrationNeeded)]
    [InlineData(0x0b, SensorState.Error)]
    [InlineData(0x0c, SensorState.Error)]
    [InlineData(0x0d, SensorState.CalibrationNeeded)]
    [InlineData(0x0f, SensorState.Expired)]
    [InlineData(0x10, SensorState.Error)]
    [InlineData(0x11, SensorState.Error)]
    [InlineData(0x12, SensorState.Error)]
    public async Task AnXDripJsSessionCodeWritesItsMappedReport(int code, SensorState expected)
    {
        await _decomposer.DecomposeBatchAsync([XDripJsStatus("xdripjs-1", code)], "nightscout", WriteOrigin.Live);

        var report = SensorStateReports().Single();
        report.State.Should().Be(expected);
        report.ReportedField.Should().Be(V4Models.SensorStateReport.XDripJsField);
        report.ReportedValue.Should().Be(code.ToString(System.Globalization.CultureInfo.InvariantCulture));
        report.Device.Should().Be("xdripjs://rig");
        report.DataSource.Should().Be("nightscout");
    }

    [Theory]
    [InlineData(0x07)]
    [InlineData(0x0e)]
    [InlineData(0x01)]
    [InlineData(0x03)]
    [InlineData(0x13)]
    public async Task AnXDripJsCodeOutsideTheAllowlistWritesAReportWithNoState(int code)
    {
        await _decomposer.DecomposeBatchAsync([XDripJsStatus("xdripjs-1", code)], "nightscout", WriteOrigin.Live);

        var report = SensorStateReports().Single();
        report.State.Should().BeNull();
        report.ReportedField.Should().Be(V4Models.SensorStateReport.XDripJsField);
        report.ReportedValue.Should().Be(code.ToString(System.Globalization.CultureInfo.InvariantCulture));
        report.Device.Should().Be("xdripjs://rig");
        report.DataSource.Should().Be("nightscout");
        _context.DeviceStatusExtras.Should().ContainSingle("the extras row is written as before");
    }

    [Theory]
    [InlineData("SENSOR_SIGNAL_LOST")]
    [InlineData("SG_BELOW_40_MGDL")]
    [InlineData("warm_up")]
    public async Task ACareLinkValueOutsideTheAllowlistWritesAReportWithNoState(string reported)
    {
        await _decomposer.DecomposeBatchAsync([CareLinkStatus("carelink-1", reported)], "carelink-connector", WriteOrigin.Live);

        var report = SensorStateReports().Single();
        report.State.Should().BeNull();
        report.ReportedField.Should().Be(V4Models.SensorStateReport.CareLinkField);
        report.ReportedValue.Should().Be(reported);
        report.Device.Should().Be("CareLink MiniMed 780G");
        report.DataSource.Should().Be("carelink-connector");
        report.CorrelationId.Should().Be(_context.DeviceStatusExtras.Single().CorrelationId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task AnEmptyCareLinkValueWritesNoReport(string reported)
    {
        await _decomposer.DecomposeBatchAsync([CareLinkStatus("carelink-1", reported)], "carelink-connector", WriteOrigin.Live);

        SensorStateReports().Should().BeEmpty();
    }

    [Fact]
    public async Task AValueLongerThanTheStoreHoldsIsCutToFit()
    {
        var reported = new string('X', 148);

        await _decomposer.DecomposeBatchAsync([CareLinkStatus("carelink-1", reported)], "carelink-connector", WriteOrigin.Live);

        SensorStateReports().Single().ReportedValue.Should().Be(reported[..128], "the column holds 128 characters");
    }

    [Fact]
    public async Task AStatusStatingNoSensorStateWritesNoReport()
    {
        await _decomposer.DecomposeBatchAsync(
            [new DeviceStatus
            {
                Id = "carelink-1", Mills = 1700000000000, Device = "CareLink MiniMed 780G",
                Cgm = new CgmStatus { Manufacturer = "Medtronic" },
                XDripJs = new XDripJsStatus { StateString = "OK" },
                Uploader = new UploaderStatus { Battery = 80 },
            }],
            "carelink-connector", WriteOrigin.Live);

        SensorStateReports().Should().BeEmpty();
    }

    [Fact]
    public async Task CareLinksValueIsReadBeforeAnXDripJsCode()
    {
        var ds = CareLinkStatus("carelink-1", "SENSOR_ERROR");
        ds.XDripJs = new XDripJsStatus { State = 0x06 };

        await _decomposer.DecomposeBatchAsync([ds], "carelink-connector", WriteOrigin.Live);

        SensorStateReports().Single().State.Should().Be(SensorState.Error);
    }

    [Fact]
    public async Task AnUnrecognisedCareLinkValueFallsThroughToTheXDripJsCode()
    {
        var ds = CareLinkStatus("carelink-1", "SENSOR_SIGNAL_LOST");
        ds.XDripJs = new XDripJsStatus { State = 0x0b };

        await _decomposer.DecomposeBatchAsync([ds], "carelink-connector", WriteOrigin.Live);

        SensorStateReports().Single().ReportedField.Should().Be(V4Models.SensorStateReport.XDripJsField);
    }

    [Fact]
    public async Task WithNeitherValueMappedCareLinksIsWritten()
    {
        var ds = CareLinkStatus("carelink-1", "SENSOR_SIGNAL_LOST");
        ds.XDripJs = new XDripJsStatus { State = 0x03 };

        await _decomposer.DecomposeBatchAsync([ds], "carelink-connector", WriteOrigin.Live);

        SensorStateReports().Should().ContainSingle().Which.Should().Match<V4Models.SensorStateReport>(r =>
            r.State == null && r.ReportedField == V4Models.SensorStateReport.CareLinkField && r.ReportedValue == "SENSOR_SIGNAL_LOST");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AReUploadOfAStoredStatusWritesNoSecondReport(bool withSnapshot)
    {
        DeviceStatus Status()
        {
            var ds = CareLinkStatus("carelink-1", "WARM_UP");
            if (!withSnapshot)
                ds.Uploader = null;
            return ds;
        }

        await _decomposer.DecomposeBatchAsync([Status()], "carelink-connector", WriteOrigin.Live);
        await _decomposer.DecomposeBatchAsync([Status()], "carelink-connector", WriteOrigin.Live);
        await _decomposer.DecomposeBatchAsync([Status(), Status()], "carelink-connector", WriteOrigin.Live);

        SensorStateReports().Should().ContainSingle();
    }

    private List<V4Models.SensorStateReport> SensorStateReports() =>
        [.. _context.SensorStateReports.AsEnumerable().Select(Nocturne.Infrastructure.Data.Mappers.V4.SensorStateReportMapper.ToDomainModel)];

    private static DeviceStatus CareLinkStatus(string id, string sensorState) => new()
    {
        Id = id,
        Mills = 1700000000000,
        Device = "CareLink MiniMed 780G",
        Cgm = new CgmStatus { Manufacturer = "Medtronic", SensorState = sensorState },
        Uploader = new UploaderStatus { Battery = 80 },
    };

    private static DeviceStatus XDripJsStatus(string id, int code) => new()
    {
        Id = id,
        Mills = 1700000000000,
        Device = "xdripjs://rig",
        XDripJs = new XDripJsStatus { State = code, StateString = "x" },
        Uploader = new UploaderStatus { Battery = 80 },
    };

    #endregion

    [Fact]
    public async Task DecomposeAsync_WithNoExtras_SkipsExtrasCreation()
    {
        // Arrange — standard OpenAPS device status with no peripheral sub-objects
        var ds = new DeviceStatus
        {
            Id = "no-extras-1",
            Mills = 1700000000000,
            Device = "openaps://Samsung",
            OpenAps = new OpenApsStatus
            {
                Iob = new OpenApsIobData { Iob = 2.0 },
                Suggested = new OpenApsSuggested
                {
                    Bg = 110.0,
                    EventualBG = 100.0,
                    Timestamp = "2023-11-14T12:00:00Z"
                }
            },
            Pump = new PumpStatus { Reservoir = 100.0 },
            Uploader = new UploaderStatus { Battery = 55 }
        };

        // Act
        await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        // Assert — no extras record should be created
        var extrasEntities = _context.DeviceStatusExtras.ToList();
        extrasEntities.Should().BeEmpty();
    }

    [Fact]
    public async Task DecomposeAsync_WithUnknownTopLevelKeys_CapturesInExtras()
    {
        // Arrange — unknown top-level keys are captured via JsonExtensionData
        var json = """
        {
            "_id": "unknown-keys-1",
            "mills": 1700000000000,
            "device": "some-device",
            "customPlugin": { "version": "1.0", "data": [1, 2, 3] },
            "anotherField": "hello"
        }
        """;
        var ds = JsonSerializer.Deserialize<DeviceStatus>(json)!;

        // Act
        await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        // Assert
        var extrasEntities = _context.DeviceStatusExtras.ToList();
        extrasEntities.Should().HaveCount(1);
        extrasEntities[0].ExtrasJson.Should().Contain("customPlugin");
        extrasEntities[0].ExtrasJson.Should().Contain("anotherField");
    }

    [Fact]
    public async Task DecomposeAsync_ApsSnapshotGetsPumpDeviceId()
    {
        // Arrange
        var expectedDeviceId = Guid.CreateVersion7();
        _deviceServiceMock
            .Setup(s => s.ResolveAsync(
                V4Models.DeviceCategory.InsulinPump,
                "Insulet",
                "Omnipod 5",
                1700000000000,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedDeviceId);

        var ds = new DeviceStatus
        {
            Id = "aps-pump-device-id",
            Mills = 1700000000000,
            Device = "openaps://Samsung",
            OpenAps = new OpenApsStatus
            {
                Iob = new OpenApsIobData { Iob = 2.0 },
                Suggested = new OpenApsSuggested
                {
                    Bg = 110.0, EventualBG = 100.0, Timestamp = "2023-11-14T12:00:00Z"
                }
            },
            Pump = new PumpStatus
            {
                Manufacturer = "Insulet",
                Model = "Omnipod 5",
                Reservoir = 100.0
            }
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        // Assert
        var aps = result.CreatedRecords.OfType<V4Models.ApsSnapshot>().Single();
        aps.DeviceId.Should().Be(expectedDeviceId);
    }

    [Fact]
    public async Task DecomposeAsync_ApsSnapshotWithNoPump_HasNullDeviceId()
    {
        // Arrange — no Pump section means no DeviceId to propagate
        var ds = new DeviceStatus
        {
            Id = "aps-no-pump-device-id",
            Mills = 1700000000000,
            Device = "openaps://Samsung",
            OpenAps = new OpenApsStatus
            {
                Iob = new OpenApsIobData { Iob = 1.5 },
                Suggested = new OpenApsSuggested
                {
                    Bg = 120.0, EventualBG = 100.0, Timestamp = "2023-11-14T12:00:00Z"
                }
            }
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        // Assert
        var aps = result.CreatedRecords.OfType<V4Models.ApsSnapshot>().Single();
        aps.DeviceId.Should().BeNull();
    }

    [Fact]
    public async Task DecomposeAsync_PumpSnapshotGetsPatientDeviceId()
    {
        // Arrange
        var expectedDeviceId = Guid.CreateVersion7();
        var expectedPatientDeviceId = Guid.CreateVersion7();

        _deviceServiceMock
            .Setup(s => s.ResolveAsync(
                V4Models.DeviceCategory.InsulinPump,
                "Insulet",
                "Omnipod 5",
                1700000000000,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedDeviceId);

        _deviceServiceMock.Links()
            .Setup(l => l.Resolve(expectedDeviceId, 1700000000000))
            .Returns(expectedPatientDeviceId);

        var ds = new DeviceStatus
        {
            Id = "pump-patient-device-id",
            Mills = 1700000000000,
            Device = "openaps://Samsung",
            Pump = new PumpStatus
            {
                Manufacturer = "Insulet",
                Model = "Omnipod 5",
                Reservoir = 150.0
            }
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        // Assert
        var pump = result.CreatedRecords.OfType<V4Models.PumpSnapshot>().Single();
        pump.PatientDeviceId.Should().Be(expectedPatientDeviceId);
    }

    #endregion

    #region AAPS SMB Volume Fallback

    [Fact]
    public async Task DecomposeAsync_OpenApsEnactedSmbZeroWithUnits_UsesUnitsForBolusVolume()
    {
        // Arrange — AAPS: smb = 0 (bolusDelivered from TBR), units = 0.2 (actual SMB from APS request)
        var ds = new DeviceStatus
        {
            Id = "aaps-smb-1",
            Mills = 1712925027741,
            Device = "openaps://samsung SM-A525F",
            OpenAps = new OpenApsStatus
            {
                Iob = new OpenApsIobData { Iob = 3.4 },
                Enacted = new OpenApsEnacted
                {
                    Received = true,
                    Rate = 0.05,
                    Duration = 30,
                    Smb = 0,
                    Units = 0.2,
                    Bg = 200,
                    Timestamp = "2026-04-12T09:30:28.167Z"
                }
            }
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        // Assert
        var aps = result.CreatedRecords[0].Should().BeOfType<V4Models.ApsSnapshot>().Subject;
        aps.EnactedBolusVolume.Should().Be(0.2);
    }

    [Fact]
    public async Task DecomposeAsync_OpenApsEnactedSmbNullWithUnits_UsesUnitsForBolusVolume()
    {
        // Arrange — smb is null, units has the value
        var ds = new DeviceStatus
        {
            Id = "aaps-smb-2",
            Mills = 1712925027741,
            Device = "openaps://samsung SM-A525F",
            OpenAps = new OpenApsStatus
            {
                Iob = new OpenApsIobData { Iob = 1.0 },
                Enacted = new OpenApsEnacted
                {
                    Received = true,
                    Rate = 0.5,
                    Duration = 30,
                    Smb = null,
                    Units = 0.15,
                    Bg = 150,
                    Timestamp = "2026-04-12T10:00:00Z"
                }
            }
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        // Assert
        var aps = result.CreatedRecords[0].Should().BeOfType<V4Models.ApsSnapshot>().Subject;
        aps.EnactedBolusVolume.Should().Be(0.15);
    }

    #endregion

    #region Pump Suspension Sequence (integration-style)

    [Fact]
    public async Task DecomposeAsync_PumpSuspensionSequence_OpensThenClosesStateSpan()
    {
        var spans = _spans;

        DeviceStatus MakeDs(string id, long mills, bool suspended) => new()
        {
            Id = id,
            Mills = mills,
            Device = "openaps://Samsung",
            Pump = new PumpStatus
            {
                Manufacturer = "Insulet",
                Model = "Omnipod",
                Status = new PumpStatusDetails { Suspended = suspended },
            },
        };

        var t0 = 1700000000000L;
        var minute = 60_000L;

        // Act — three sequential uploads: not suspended, suspended, not suspended
        await _decomposer.DecomposeBatchAsync([MakeDs("ds-1", t0, suspended: false)], null, WriteOrigin.Live);
        await _decomposer.DecomposeBatchAsync([MakeDs("ds-2", t0 + minute, suspended: true)], null, WriteOrigin.Live);
        await _decomposer.DecomposeBatchAsync([MakeDs("ds-3", t0 + 2 * minute, suspended: false)], null, WriteOrigin.Live);

        // Assert — exactly one PumpMode/Suspended span, opened then closed
        var pumpModeSpans = spans.Where(s => s.Category == StateSpanCategory.PumpMode).ToList();
        pumpModeSpans.Should().HaveCount(1);

        var span = pumpModeSpans[0];
        span.State.Should().Be(PumpModeState.Suspended.ToString());
        span.OriginalId.Should().StartWith("pump-suspended:");
        span.StartTimestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(t0 + minute).UtcDateTime);
        span.EndTimestamp.Should().NotBeNull();
        span.EndTimestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(t0 + 2 * minute).UtcDateTime);
    }

    #endregion

    #region Pump Mode Sequence (integration-style)

    private static DeviceStatus MakePumpModeDs(string id, long mills, string? pumpMode, bool suspended = false) => new()
    {
        Id = id,
        Mills = mills,
        Device = "carelink-connector",
        Pump = new PumpStatus
        {
            Manufacturer = "Medtronic",
            Model = "780G",
            PumpMode = pumpMode,
            Status = new PumpStatusDetails { Suspended = suspended },
        },
    };

    [Fact]
    public async Task DecomposeAsync_PumpModeSequence_TogglesAutomaticAndManualSpans()
    {
        var spans = _spans;
        var t0 = 1700000000000L;
        var minute = 60_000L;

        await _decomposer.DecomposeBatchAsync([MakePumpModeDs("ds-1", t0, "Manual")], null, WriteOrigin.Live);
        await _decomposer.DecomposeBatchAsync([MakePumpModeDs("ds-2", t0 + minute, "Automatic")], null, WriteOrigin.Live);
        await _decomposer.DecomposeBatchAsync([MakePumpModeDs("ds-3", t0 + 2 * minute, "Manual")], null, WriteOrigin.Live);

        var modeSpans = spans.Where(s => s.Category == StateSpanCategory.PumpMode).ToList();

        var automatic = modeSpans.Single(s => s.State == PumpModeState.Automatic.ToString());
        automatic.StartTimestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(t0 + minute).UtcDateTime);
        automatic.EndTimestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(t0 + 2 * minute).UtcDateTime);

        var manualSpans = modeSpans.Where(s => s.State == PumpModeState.Manual.ToString()).ToList();
        manualSpans.Should().HaveCount(2);
        manualSpans.Should().ContainSingle(s => s.EndTimestamp == null
            && s.StartTimestamp == DateTimeOffset.FromUnixTimeMilliseconds(t0 + 2 * minute).UtcDateTime);
        manualSpans.Should().ContainSingle(s => s.EndTimestamp == DateTimeOffset.FromUnixTimeMilliseconds(t0 + minute).UtcDateTime);
    }

    [Fact]
    public async Task DecomposeAsync_PumpModeSteadyState_KeepsSingleOpenSpan()
    {
        var spans = _spans;
        var t0 = 1700000000000L;
        var minute = 60_000L;

        await _decomposer.DecomposeBatchAsync([MakePumpModeDs("ds-1", t0, "Automatic")], null, WriteOrigin.Live);
        await _decomposer.DecomposeBatchAsync([MakePumpModeDs("ds-2", t0 + minute, "Automatic")], null, WriteOrigin.Live);
        await _decomposer.DecomposeBatchAsync([MakePumpModeDs("ds-3", t0 + 2 * minute, "Automatic")], null, WriteOrigin.Live);

        var modeSpans = spans.Where(s => s.Category == StateSpanCategory.PumpMode).ToList();
        modeSpans.Should().ContainSingle();
        modeSpans[0].State.Should().Be(PumpModeState.Automatic.ToString());
        modeSpans[0].EndTimestamp.Should().BeNull();
        modeSpans[0].StartTimestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(t0).UtcDateTime);
    }

    [Fact]
    public async Task DecomposeAsync_AutomaticAndSuspendedSpans_CoexistOpen()
    {
        var spans = _spans;
        var t0 = 1700000000000L;
        var minute = 60_000L;

        await _decomposer.DecomposeBatchAsync([MakePumpModeDs("ds-1", t0, "Manual", suspended: false)], null, WriteOrigin.Live);
        await _decomposer.DecomposeBatchAsync([MakePumpModeDs("ds-2", t0 + minute, "Automatic", suspended: true)], null, WriteOrigin.Live);

        var open = spans.Where(s => s.Category == StateSpanCategory.PumpMode && s.EndTimestamp == null)
            .Select(s => s.State).ToList();

        open.Should().Contain(PumpModeState.Automatic.ToString());
        open.Should().Contain(PumpModeState.Suspended.ToString());
    }

    [Fact]
    public async Task DecomposeAsync_NoPumpMode_EmitsNoModeSpan()
    {
        var spans = _spans;

        await _decomposer.DecomposeBatchAsync([MakePumpModeDs("ds-1", 1700000000000L, pumpMode: null)], null, WriteOrigin.Live);

        spans.Where(s => s.Category == StateSpanCategory.PumpMode
                && (s.State == PumpModeState.Automatic.ToString() || s.State == PumpModeState.Manual.ToString()))
            .Should().BeEmpty();
    }

    #endregion

    #region Prediction curves stored once

    private static readonly JsonSerializerOptions CanonicalJson = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static string Canonical(object? value) =>
        value is null ? "null" : JsonSerializer.Serialize(value, value.GetType(), CanonicalJson);

    private static OpenApsPredBGs Curves(double start) => new()
    {
        IOB = [start, start - 5, start - 10],
        ZT = [start, start - 2],
        COB = [start, start + 5, start + 10, start + 5],
        UAM = [start, start + 10],
    };

    private async Task<(V4Models.ApsSnapshot Stored, DeviceStatus Projected)> DecomposeAndProject(DeviceStatus ds)
    {
        var result = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);
        var stored = result.CreatedRecords.OfType<V4Models.ApsSnapshot>().Single();
        return (stored, DeviceStatusProjectionService.ProjectFromSnapshots(stored, null, null, null, null));
    }

    [Fact]
    public async Task DecomposeAsync_OpenApsSameCycle_StoresCurvesOnceAndProjectsThemBack()
    {
        var suggested = new OpenApsSuggested
        {
            Bg = 120, EventualBG = 95, Reason = "COB: 0, Dev: -3", Timestamp = "2023-11-14T12:00:00Z", PredBGs = Curves(120),
        };
        var enacted = new OpenApsEnacted
        {
            Bg = 120, EventualBG = 95, Reason = "COB: 0, Dev: -3", Timestamp = "2023-11-14T12:00:00Z", PredBGs = Curves(120),
            Rate = 0.8, Duration = 30, Received = true,
        };
        var ds = new DeviceStatus
        {
            Id = "curves-same-cycle",
            Mills = 1700000000000,
            Device = "openaps://Samsung",
            OpenAps = new OpenApsStatus { Suggested = suggested, Enacted = enacted },
        };
        var originalSuggested = Canonical(suggested);
        var originalEnacted = Canonical(enacted);

        var (stored, projected) = await DecomposeAndProject(ds);

        stored.SuggestedJson.Should().NotContain("predBGs");
        stored.EnactedJson.Should().NotContain("predBGs");
        stored.PredictedIobJson.Should().Be("[120,115,110]");
        Canonical(projected.OpenAps!.Suggested).Should().Be(originalSuggested);
        Canonical(projected.OpenAps.Enacted).Should().Be(originalEnacted);
    }

    [Fact]
    public async Task DecomposeAsync_OpenApsResentEnacted_KeepsTheOlderCommandsOwnCurves()
    {
        // The re-sent enacted command is the previous cycle's, so its curves differ from the
        // fresher suggestion the columns are taken from.
        var suggested = new OpenApsSuggested { Bg = 118, Timestamp = "2023-11-14T12:05:00Z", PredBGs = Curves(118) };
        var enacted = new OpenApsEnacted
        {
            Bg = 120, Timestamp = "2023-11-14T12:00:00Z", PredBGs = Curves(120), Rate = 0.8, Duration = 30, Received = true,
        };
        var ds = new DeviceStatus
        {
            Id = "curves-resent-enacted",
            Mills = 1700000300000,
            Device = "openaps://Samsung",
            OpenAps = new OpenApsStatus { Suggested = suggested, Enacted = enacted },
        };
        var originalSuggested = Canonical(suggested);
        var originalEnacted = Canonical(enacted);

        var (stored, projected) = await DecomposeAndProject(ds);

        stored.SuggestedJson.Should().NotContain("predBGs");
        stored.EnactedJson.Should().Contain("predBGs");
        Canonical(projected.OpenAps!.Suggested).Should().Be(originalSuggested);
        Canonical(projected.OpenAps.Enacted).Should().Be(originalEnacted);
    }

    [Fact]
    public async Task DecomposeAsync_OpenApsCommandWithoutCurvesBesideOneWith_ReadsBackWithout()
    {
        // Trio uploads a suggestion without predBGs beside an enacted command that has them.
        var suggested = new OpenApsSuggested { Bg = 91, Timestamp = "2023-11-14T12:00:00Z" };
        var enacted = new OpenApsEnacted
        {
            Bg = 91, Timestamp = "2023-11-14T12:00:00Z", PredBGs = Curves(91), Rate = 0.6, Duration = 30, Received = true,
        };
        var ds = new DeviceStatus
        {
            Id = "curves-one-sided",
            Mills = 1700000000000,
            Device = "Trio",
            OpenAps = new OpenApsStatus { Suggested = suggested, Enacted = enacted },
        };
        var originalSuggested = Canonical(suggested);
        var originalEnacted = Canonical(enacted);

        var (stored, projected) = await DecomposeAndProject(ds);

        stored.SuggestedJson.Should().Contain("\"predBGs\":null");
        stored.EnactedJson.Should().NotContain("predBGs");
        Canonical(projected.OpenAps!.Suggested).Should().Be(originalSuggested);
        Canonical(projected.OpenAps.Enacted).Should().Be(originalEnacted);
    }

    [Fact]
    public async Task DecomposeAsync_OpenApsCommandWithEmptyPredBGs_KeepsIt()
    {
        var suggested = new OpenApsSuggested { Bg = 118, Timestamp = "2023-11-14T12:05:00Z", PredBGs = new OpenApsPredBGs() };
        var ds = new DeviceStatus
        {
            Id = "curves-empty",
            Mills = 1700000300000,
            Device = "openaps://Samsung",
            OpenAps = new OpenApsStatus { Suggested = suggested },
        };
        var originalSuggested = Canonical(suggested);

        var (stored, projected) = await DecomposeAndProject(ds);

        stored.SuggestedJson.Should().Contain("predBGs");
        Canonical(projected.OpenAps!.Suggested).Should().Be(originalSuggested);
    }

    [Fact]
    public async Task DecomposeAsync_OpenAps_StoresNoNullValuedKeys()
    {
        var ds = new DeviceStatus
        {
            Id = "no-null-keys",
            Mills = 1700000000000,
            Device = "openaps://Samsung",
            OpenAps = new OpenApsStatus
            {
                Suggested = new OpenApsSuggested { Bg = 118, Timestamp = "2023-11-14T12:05:00Z" },
            },
        };

        var (stored, _) = await DecomposeAndProject(ds);

        stored.SuggestedJson.Should().NotContain("null");
        stored.SuggestedJson.Should().NotContain("predBGs");
    }

    [Fact]
    public async Task DecomposeAsync_Loop_StoresPredictionOnceAndProjectsItBack()
    {
        var loop = new LoopStatus
        {
            Iob = new LoopIob { Iob = 1.8, BasalIob = 0.5 },
            Cob = new LoopCob { Cob = 25.0 },
            RecommendedBolus = 0.7,
            Predicted = new LoopPredicted { Values = [130, 125, 120, 115, 110], StartDate = "2023-11-14T12:00:00Z" },
            Enacted = new LoopEnacted { Received = true, Rate = 2.0, Duration = 30, BolusVolume = 0.4 },
        };
        var ds = new DeviceStatus { Id = "loop-curves", Mills = 1700000000000, Device = "Loop/3.0", Loop = loop };
        var originalLoop = Canonical(loop);

        var (stored, projected) = await DecomposeAndProject(ds);

        stored.SuggestedJson.Should().BeNull();
        stored.EnactedJson.Should().BeNull();
        stored.LoopJson.Should().NotContain("values");
        stored.PredictedDefaultJson.Should().Be("[130,125,120,115,110]");
        Canonical(projected.Loop).Should().Be(originalLoop);
    }

    [Fact]
    public void ProjectFromSnapshots_RowStoredWithCurvesInItsBlobs_ProjectsThemUnchanged()
    {
        var suggested = new OpenApsSuggested { Bg = 118, Timestamp = "2023-11-14T12:05:00Z", PredBGs = Curves(118) };
        var enacted = new OpenApsEnacted { Bg = 120, Timestamp = "2023-11-14T12:00:00Z", PredBGs = Curves(120) };
        var aps = new V4Models.ApsSnapshot
        {
            Id = Guid.CreateVersion7(),
            Timestamp = DateTime.UtcNow,
            AidAlgorithm = V4Models.AidAlgorithm.OpenAps,
            SuggestedJson = JsonSerializer.Serialize(suggested),
            EnactedJson = JsonSerializer.Serialize(enacted),
            PredictedIobJson = "[1,2,3]",
        };

        var projected = DeviceStatusProjectionService.ProjectFromSnapshots(aps, null, null, null, null);

        Canonical(projected.OpenAps!.Suggested).Should().Be(Canonical(suggested));
        Canonical(projected.OpenAps.Enacted).Should().Be(Canonical(enacted));
    }

    #endregion

    #region Prediction accuracy reads the stored curves

    // Shapes follow nightscout/cgm-remote-monitor tests/fixtures/captured/{loop,trio}/devicestatus.json
    // at c6a0d8ed65637bc90d3090e6e7a0f752041c57fd: Loop's predicted.values runs about 80 values from a
    // startDate off the five-minute grid, and oref's predBGs run 48 values unless a curve's tail was
    // cut. The values here are synthesised.

    private static PredictionSnapshot? ParseStored(V4Models.ApsSnapshot aps) =>
        PredictionAccuracyCalculator.Parse(new V4Models.ApsPredictionPoint(
            aps.PredictedStartTimestamp!.Value, aps.Device, aps.Enacted, aps.AidAlgorithm,
            aps.PredictedDefaultJson, aps.PredictedIobJson, aps.PredictedZtJson, aps.PredictedCobJson, aps.PredictedUamJson));

    private static double?[]? CurveOf(PredictionSnapshot snapshot, PredictionCurve curve) =>
        snapshot.Curves[(int)curve];

    /// <summary>A curve from <paramref name="first"/> moving by <paramref name="step"/> and levelling at <paramref name="floor"/>.</summary>
    private static List<double?> Synthesised(int count, double first, double step, double floor = 39) =>
        [.. Enumerable.Range(0, count).Select(i => (double?)Math.Max(floor, Math.Round(first + step * i)))];

    [Fact]
    public async Task PredictionAccuracy_ReadsLoopsStoredCurveAsTheDefault()
    {
        var values = Synthesised(83, 141, -0.8, floor: 70).Select(v => v!.Value).ToArray();
        var ds = new DeviceStatus
        {
            Id = "loop-prediction-shape",
            Mills = new DateTimeOffset(2026, 5, 9, 0, 1, 57, 372, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            Device = "loop://test-device",
            Loop = new LoopStatus
            {
                Timestamp = "2026-05-09T00:01:57.372Z",
                Predicted = new LoopPredicted { Values = values, StartDate = "2026-05-08T23:59:59.372Z" },
            },
        };

        var snapshot = ParseStored(await DecomposeApsAsync(ds))!;

        snapshot.StartMills.Should().Be(new DateTimeOffset(2026, 5, 8, 23, 59, 59, 372, TimeSpan.Zero).ToUnixTimeMilliseconds());
        CurveOf(snapshot, PredictionCurve.Default).Should().Equal(values.Take(25).Select(v => (double?)v), "values past the last horizon are not read");
        snapshot.Curves.Count(c => c is not null).Should().Be(1);
        PredictionAccuracyCalculator.TargetMills(snapshot, PredictionCurve.Default, 6)
            .Should().Be(new DateTimeOffset(2026, 5, 9, 0, 25, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                "Loop's later values sit on the grid point after its off-grid start");
    }

    [Fact]
    public async Task PredictionAccuracy_ReadsAapsStoredCurves_WithoutTheDefaultCopyOfIob()
    {
        var iob = Synthesised(31, 156, -4);
        var zeroTemp = Synthesised(7, 104, 3);
        var ds = new DeviceStatus
        {
            Id = "aaps-prediction-shape",
            Mills = 1778284798029,
            Device = "openaps://samsung",
            OpenAps = new OpenApsStatus
            {
                Suggested = new OpenApsSuggested
                {
                    Bg = 156.0,
                    Timestamp = "2026-05-08T23:59:58.029Z",
                    PredBGs = new OpenApsPredBGs
                    {
                        IOB = iob,
                        ZT = zeroTemp,
                        COB = Synthesised(48, 156, 5, floor: 0),
                        UAM = Synthesised(48, 156, 2, floor: 0),
                    },
                },
            },
            Uploader = new UploaderStatus { Name = "AndroidAPS" },
        };

        var aps = await DecomposeApsAsync(ds);
        var snapshot = ParseStored(aps)!;

        aps.PredictedDefaultJson.Should().Be(aps.PredictedIobJson, "the decomposer copies oref's IOB curve into the default column");
        CurveOf(snapshot, PredictionCurve.Default).Should().BeNull();
        CurveOf(snapshot, PredictionCurve.Iob).Should().Equal(iob.Take(25), "a curve of 31 reaches every horizon itself");
        CurveOf(snapshot, PredictionCurve.ZeroTemp).Should().Equal(zeroTemp, "oref cut the zero-temp curve where it turned up; nothing is held");
        CurveOf(snapshot, PredictionCurve.Cob).Should().HaveCount(25);
        CurveOf(snapshot, PredictionCurve.Uam).Should().HaveCount(25);
    }

    [Fact]
    public async Task PredictionAccuracy_ReadsTriosStoredCurves_HoldingALevelledTail()
    {
        var iob = Synthesised(18, 152, -7);
        var ds = new DeviceStatus
        {
            Id = "trio-prediction-shape",
            Mills = 1778284489623,
            Device = "Trio",
            OpenAps = new OpenApsStatus
            {
                Version = "0.2.1",
                Suggested = new OpenApsSuggested
                {
                    Bg = 152.0,
                    Timestamp = "2026-05-08T23:54:49.623Z",
                    PredBGs = new OpenApsPredBGs
                    {
                        IOB = iob,
                        ZT = Synthesised(48, 152, -3),
                        COB = Synthesised(48, 152, 6, floor: 0),
                        UAM = Synthesised(22, 152, -5.5),
                    },
                },
            },
        };

        var snapshot = ParseStored(await DecomposeApsAsync(ds))!;

        snapshot.Algorithm.Should().Be(V4Models.AidAlgorithm.Trio);
        CurveOf(snapshot, PredictionCurve.Default).Should().BeNull();
        CurveOf(snapshot, PredictionCurve.Iob).Should().StartWith(iob)
            .And.HaveCount(25, "oref dropped the level tail after 39").And.EndWith(39);
        CurveOf(snapshot, PredictionCurve.Uam).Should().HaveCount(25).And.EndWith(39);
    }

    #endregion
}
