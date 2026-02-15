using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

using V4Models = Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.V4;

public class DeviceStatusDecomposerTests : IDisposable
{
    private readonly NocturneDbContext _context;
    private readonly Mock<IStateSpanService> _stateSpanServiceMock;
    private readonly DeviceStatusDecomposer _decomposer;

    public DeviceStatusDecomposerTests()
    {
        _context = TestDbContextFactory.CreateInMemoryContext();
        var apsRepo = new ApsSnapshotRepository(_context, NullLogger<ApsSnapshotRepository>.Instance);
        var pumpRepo = new PumpSnapshotRepository(_context, NullLogger<PumpSnapshotRepository>.Instance);
        var uploaderRepo = new UploaderSnapshotRepository(_context, NullLogger<UploaderSnapshotRepository>.Instance);
        _stateSpanServiceMock = new Mock<IStateSpanService>();

        _decomposer = new DeviceStatusDecomposer(
            apsRepo, pumpRepo, uploaderRepo,
            _stateSpanServiceMock.Object,
            NullLogger<DeviceStatusDecomposer>.Instance);
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
                        IOB = new List<double> { 120, 115, 110, 105, 100 },
                        ZT = new List<double> { 120, 118, 115 },
                        COB = new List<double> { 120, 125, 130, 125, 120, 110 },
                        UAM = new List<double> { 120, 130, 135, 130 }
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
                        IOB = new List<double> { 120, 115, 110, 105, 100 },
                    }
                }
            }
        };

        // Act
        var result = await _decomposer.DecomposeAsync(ds);

        // Assert
        result.CreatedRecords.Should().HaveCount(1);
        var aps = result.CreatedRecords[0].Should().BeOfType<V4Models.ApsSnapshot>().Subject;
        aps.ApsSystem.Should().Be(V4Models.ApsSystem.OpenAps);
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
        var result = await _decomposer.DecomposeAsync(ds);

        // Assert
        result.CreatedRecords.Should().HaveCount(1);
        var aps = result.CreatedRecords[0].Should().BeOfType<V4Models.ApsSnapshot>().Subject;
        aps.ApsSystem.Should().Be(V4Models.ApsSystem.Loop);
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
        var result = await _decomposer.DecomposeAsync(ds);

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
        var result = await _decomposer.DecomposeAsync(ds);

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
        var result = await _decomposer.DecomposeAsync(ds);

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
                Duration = 60.0,
                Multiplier = 1.5,
                CurrentCorrectionRange = new CorrectionRange { MinValue = 140, MaxValue = 160 }
            }
        };

        var expectedStateSpan = new StateSpan
        {
            Id = "state-span-override",
            Category = StateSpanCategory.Override,
            StartMills = 1700000000000
        };

        _stateSpanServiceMock
            .Setup(s => s.UpsertStateSpanAsync(It.IsAny<StateSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedStateSpan);

        // Act
        var result = await _decomposer.DecomposeAsync(ds);

        // Assert
        result.CreatedRecords.Should().HaveCount(1);
        result.CreatedRecords[0].Should().BeOfType<StateSpan>();

        _stateSpanServiceMock.Verify(
            s => s.UpsertStateSpanAsync(
                It.Is<StateSpan>(ss =>
                    ss.Category == StateSpanCategory.Override
                    && ss.State == "Custom"
                    && ss.StartMills == 1700000000000
                    && ss.OriginalId == "override123"
                    && ss.Metadata != null
                    && ss.Metadata.ContainsKey("name")
                    && ss.Metadata.ContainsKey("multiplier")
                    && ss.Metadata.ContainsKey("currentCorrectionRange.minValue")
                    && ss.Metadata.ContainsKey("currentCorrectionRange.maxValue")),
                It.IsAny<CancellationToken>()),
            Times.Once);
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
        var result = await _decomposer.DecomposeAsync(ds);

        // Assert
        result.CreatedRecords.Should().BeEmpty();
        result.UpdatedRecords.Should().BeEmpty();
        _context.ApsSnapshots.Should().BeEmpty();
    }

    #endregion

    #region Idempotency

    [Fact]
    public async Task DecomposeAsync_IdempotentUpdate_UpdatesExistingSnapshot()
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
        var firstResult = await _decomposer.DecomposeAsync(ds);
        firstResult.CreatedRecords.Should().HaveCount(1);
        firstResult.UpdatedRecords.Should().BeEmpty();

        // Act - second call should update
        var secondResult = await _decomposer.DecomposeAsync(ds);

        // Assert
        secondResult.UpdatedRecords.Should().HaveCount(1);
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
        var result = await _decomposer.DecomposeAsync(ds);

        // Assert
        result.CreatedRecords.Should().HaveCount(3);
        result.CreatedRecords.OfType<V4Models.ApsSnapshot>().Should().HaveCount(1);
        result.CreatedRecords.OfType<V4Models.PumpSnapshot>().Should().HaveCount(1);
        result.CreatedRecords.OfType<V4Models.UploaderSnapshot>().Should().HaveCount(1);
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
        var result = await _decomposer.DecomposeAsync(ds);

        // Assert
        result.CreatedRecords.Should().HaveCount(1);
        var aps = result.CreatedRecords[0].Should().BeOfType<V4Models.ApsSnapshot>().Subject;
        aps.Enacted.Should().BeTrue();
    }

    #endregion
}
