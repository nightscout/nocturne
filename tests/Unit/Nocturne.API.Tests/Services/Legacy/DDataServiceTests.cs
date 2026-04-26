using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.Devices;
using Nocturne.API.Services.Legacy;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Repositories;
using Xunit;

namespace Nocturne.API.Tests.Services.Legacy;

/// <summary>
/// Tests for DDataService with 1:1 legacy compatibility
/// </summary>
[Parity("ddata.test.js")]
public class DDataServiceTests
{
    private readonly Mock<IEntryStore> _mockEntryStore;
    private readonly Mock<ITreatmentService> _mockTreatmentService;
    private readonly Mock<IProfileProjectionService> _mockProfileProjectionService;
    private readonly Mock<IFoodRepository> _mockFoodRepository;
    private readonly Mock<IActivityService> _mockActivityService;
    private readonly Mock<ILogger<DDataService>> _mockLogger;
    private readonly DDataService _ddataService;

    public DDataServiceTests()
    {
        _mockEntryStore = new Mock<IEntryStore>();
        _mockTreatmentService = new Mock<ITreatmentService>();
        _mockProfileProjectionService = new Mock<IProfileProjectionService>();
        _mockFoodRepository = new Mock<IFoodRepository>();
        _mockActivityService = new Mock<IActivityService>();
        _mockLogger = new Mock<ILogger<DDataService>>();

        // Build a DeviceStatusProjectionService backed by empty mocked repositories
        var mockApsRepo = new Mock<IApsSnapshotRepository>();
        mockApsRepo.Setup(x => x.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ApsSnapshot>());
        var mockPumpRepo = new Mock<IPumpSnapshotRepository>();
        mockPumpRepo.Setup(x => x.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PumpSnapshot>());
        var mockUploaderRepo = new Mock<IUploaderSnapshotRepository>();
        var mockStateSpanRepo = new Mock<IStateSpanRepository>();
        var mockExtrasRepo = new Mock<IDeviceStatusExtrasRepository>();

        var projectionService = new DeviceStatusProjectionService(
            mockApsRepo.Object,
            mockPumpRepo.Object,
            mockUploaderRepo.Object,
            mockStateSpanRepo.Object,
            mockExtrasRepo.Object,
            new Mock<ILogger<DeviceStatusProjectionService>>().Object);

        _ddataService = new DDataService(
            _mockEntryStore.Object,
            _mockTreatmentService.Object,
            _mockProfileProjectionService.Object,
            projectionService,
            _mockFoodRepository.Object,
            _mockActivityService.Object,
            _mockLogger.Object
        );
    }

    [Fact]
    public async Task GetCurrentDDataAsync_ShouldReturnDDataStructure()
    { // Arrange
        _mockEntryStore
            .Setup(x =>
                x.QueryAsync(
                    It.IsAny<EntryQuery>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(Array.Empty<Entry>());
        _mockTreatmentService
            .Setup(x =>
                x.GetTreatmentsAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(Array.Empty<Treatment>());
        _mockProfileProjectionService
            .Setup(x =>
                x.GetProfilesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(Array.Empty<Profile>());
        _mockFoodRepository
            .Setup(x => x.GetFoodAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Food>());
        _mockActivityService
            .Setup(x =>
                x.GetActivitiesAsync(
                    It.IsAny<string?>(),
                    It.IsAny<int?>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(Array.Empty<Activity>());

        // Act
        var result = await _ddataService.GetCurrentDDataAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.Sgvs);
        Assert.NotNull(result.Treatments);
        Assert.NotNull(result.Mbgs);
        Assert.NotNull(result.Cals);
        Assert.NotNull(result.Profiles);
        Assert.NotNull(result.DeviceStatus);
        Assert.NotNull(result.Food);
        Assert.NotNull(result.Activity);
        Assert.NotNull(result.DbStats);
        Assert.True(result.LastUpdated > 0);
    }

    [Fact]
    public void ProcessDurations_ShouldRemoveDuplicatesByMills()
    {
        // Arrange
        var treatments = new List<Treatment>
        {
            new() { Mills = 1000, Duration = 30 },
            new() { Mills = 1000, Duration = 45 }, // Duplicate mills
            new() { Mills = 2000, Duration = 60 },
        };

        // Act
        var result = _ddataService.ProcessDurations(treatments, true);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal(1000, result[0].Mills);
        Assert.Equal(2000, result[1].Mills);
    }

    [Fact]
    public void ProcessDurations_ShouldCutOverlappingDurations()
    {
        // Arrange
        var treatments = new List<Treatment>
        {
            new() { Mills = 1000, Duration = 60 }, // 1000-61000 (60 minutes)
            new() { Mills = 30000, Duration = 0 }, // End event at 30000
        };

        // Act
        var result = _ddataService.ProcessDurations(treatments, false);

        // Assert
        var baseTreatment = result.FirstOrDefault(t => t.Mills == 1000);
        Assert.NotNull(baseTreatment);
        Assert.True(baseTreatment.Duration < 60); // Should be cut by end event
    }

    [Fact]
    public void ConvertTempTargetUnits_ShouldConvertMmolToMgdl()
    {
        // Arrange
        var treatments = new List<Treatment>
        {
            new()
            {
                TargetTop = 10.0,
                TargetBottom = 5.0,
                Units = "mmol",
            },
            new() { TargetTop = 15.0, TargetBottom = 8.0 }, // Below 20, should be converted
        };

        // Act
        var result = _ddataService.ConvertTempTargetUnits(treatments);

        // Assert
        Assert.All(
            result,
            t =>
            {
                Assert.True(t.TargetTop > 20); // Should be converted to mg/dl
                Assert.True(t.TargetBottom > 20);
                Assert.Equal("mg/dl", t.Units);
            }
        );
    }

    [Fact]
    public void GetRecentDeviceStatus_ShouldReturnRecentStatuses()
    {
        // Arrange
        var deviceStatuses = new List<DeviceStatus>
        {
            new()
            {
                Id = "1",
                Device = "test",
                Mills = 1000,
                Pump = new PumpStatus(),
            },
            new()
            {
                Id = "2",
                Device = "test",
                Mills = 2000,
                Pump = new PumpStatus(),
            },
            new()
            {
                Id = "3",
                Device = "test",
                Mills = 3000,
                Uploader = new UploaderStatus(),
            },
        };

        // Act
        var result = _ddataService.GetRecentDeviceStatus(deviceStatuses, 2500);

        // Assert
        Assert.NotEmpty(result);
        Assert.All(result, ds => Assert.True(ds.Mills <= 2500));
    }

    [Fact]
    public void ProcessTreatments_ShouldPopulateLastProfileFromSwitch()
    {
        // Arrange - lastProfileFromSwitch must be computed from profile treatments
        // cgm-remote-monitor finds the latest zero-duration Profile Switch before current time
        var treatments = new List<Treatment>
        {
            new()
            {
                EventType = "Profile Switch",
                Mills = 1000,
                Duration = 0,
                Profile = "OldProfile",
            },
            new()
            {
                EventType = "Profile Switch",
                Mills = 3000,
                Duration = 0,
                Profile = "CurrentProfile",
            },
            new()
            {
                EventType = "Profile Switch",
                Mills = 2000,
                Duration = 60,
                Profile = "TimedProfile",
            },
            new()
            {
                EventType = "Meal Bolus",
                Mills = 2500,
                Duration = 0,
            },
        };

        // Act
        var result = _ddataService.ProcessTreatments(treatments, false);

        // Assert - should pick the latest zero-duration Profile Switch
        Assert.Equal("CurrentProfile", result.LastProfileFromSwitch);
    }

    [Fact]
    public void ProcessTreatments_ShouldReturnNullLastProfileFromSwitch_WhenNoProfileSwitches()
    {
        // Arrange
        var treatments = new List<Treatment>
        {
            new()
            {
                EventType = "Meal Bolus",
                Mills = 1000,
            },
        };

        // Act
        var result = _ddataService.ProcessTreatments(treatments, false);

        // Assert
        Assert.Null(result.LastProfileFromSwitch);
    }

    [Fact]
    public void IdMergePreferNew_ShouldPreferNewDataWhenCollisionFound()
    {
        // Arrange
        var oldData = new List<TestDataWithId>
        {
            new() { Id = "1", Value = "old1" },
            new() { Id = "2", Value = "old2" },
        };

        var newData = new List<TestDataWithId>
        {
            new() { Id = "1", Value = "new1" }, // Collision
            new() { Id = "3", Value = "new3" },
        };

        // Act
        var result = _ddataService.IdMergePreferNew(oldData, newData);

        // Assert
        Assert.Equal(3, result.Count);
        Assert.Equal("new1", result.First(x => x.Id == "1").Value); // Should prefer new
        Assert.Equal("old2", result.First(x => x.Id == "2").Value); // Should keep old
        Assert.Equal("new3", result.First(x => x.Id == "3").Value); // Should include new
    }

    private class TestDataWithId
    {
        public string Id { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }
}
