using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.ChartData;
using Nocturne.API.Services.ChartData.Stages;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories.V4;

namespace Nocturne.API.Tests.Services.ChartData.Stages;

public class DataFetchStageTests
{
    // Common test timestamps: 24-hour window
    private const long StartTime = 1700000000000L;
    private const long EndTime = 1700086400000L;
    private const long BufferMs = 8L * 60 * 60 * 1000;
    private const long BufferStartTime = StartTime - BufferMs;

    private readonly Mock<ISensorGlucoseRepository> _mockSensorGlucoseRepo = new();
    private readonly Mock<IBolusRepository> _mockBolusRepo = new();
    private readonly Mock<ICarbIntakeRepository> _mockCarbIntakeRepo = new();
    private readonly Mock<IBGCheckRepository> _mockBgCheckRepo = new();
    private readonly Mock<IDeviceEventRepository> _mockDeviceEventRepo = new();
    private readonly Mock<ITempBasalRepository> _mockTempBasalRepo = new();
    private readonly Mock<IStateSpanRepository> _mockStateSpanRepo;
    private readonly Mock<ISystemEventRepository> _mockSystemEventRepo;
    private readonly Mock<ITrackerRepository> _mockTrackerRepo;
    private readonly Mock<IHeartRateService> _mockHeartRateService = new();
    private readonly Mock<IStepCountService> _mockStepCountService = new();
    private readonly DataFetchStage _stage;

    public DataFetchStageTests()
    {
        _mockStateSpanRepo = new Mock<IStateSpanRepository>();
        _mockSystemEventRepo = new Mock<ISystemEventRepository>();
        _mockTrackerRepo = new Mock<ITrackerRepository>();

        SetupDefaultMocks();

        _stage = new DataFetchStage(
            _mockSensorGlucoseRepo.Object,
            _mockBolusRepo.Object,
            _mockCarbIntakeRepo.Object,
            _mockBgCheckRepo.Object,
            _mockDeviceEventRepo.Object,
            _mockTempBasalRepo.Object,
            _mockStateSpanRepo.Object,
            _mockSystemEventRepo.Object,
            _mockTrackerRepo.Object,
            NullLogger<DataFetchStage>.Instance,
            _mockHeartRateService.Object,
            _mockStepCountService.Object
        );
    }

    private void SetupDefaultMocks()
    {
        // ISensorGlucoseRepository.GetAsync: (DateTime?, DateTime?, string?, string?, int, int, bool, bool, CancellationToken)
        _mockSensorGlucoseRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SensorGlucose>());

        // IBolusRepository.GetAsync: (DateTime?, DateTime?, string?, string?, int, int, bool, bool, BolusKind?, CancellationToken)
        _mockBolusRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<bool>(), It.IsAny<BolusKind?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Bolus>());

        // ICarbIntakeRepository.GetAsync: (DateTime?, DateTime?, string?, string?, int, int, bool, bool, CancellationToken)
        _mockCarbIntakeRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<CarbIntake>());

        // IBGCheckRepository.GetAsync: (DateTime?, DateTime?, string?, string?, int, int, bool, bool, CancellationToken)
        _mockBgCheckRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<BGCheck>());

        // IDeviceEventRepository.GetAsync: (DateTime?, DateTime?, string?, string?, int, int, bool, bool, CancellationToken)
        _mockDeviceEventRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<DeviceEvent>());

        // ITempBasalRepository.GetAsync: (DateTime?, DateTime?, string?, string?, int, int, bool, CancellationToken) — no nativeOnly
        _mockTempBasalRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<TempBasal>());

        var emptyStateSpans = new Dictionary<StateSpanCategory, List<StateSpan>>
        {
            [StateSpanCategory.PumpMode] = [],
            [StateSpanCategory.Profile] = [],
            [StateSpanCategory.Override] = [],
            [StateSpanCategory.Sleep] = [],
            [StateSpanCategory.Exercise] = [],
            [StateSpanCategory.Illness] = [],
            [StateSpanCategory.Travel] = [],
        };

        _mockStateSpanRepo
            .Setup(r => r.GetByCategories(
                It.IsAny<IEnumerable<StateSpanCategory>>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(emptyStateSpans);

        _mockSystemEventRepo
            .Setup(r => r.GetSystemEventsAsync(
                It.IsAny<SystemEventType?>(),
                It.IsAny<SystemEventCategory?>(),
                It.IsAny<long?>(),
                It.IsAny<long?>(),
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SystemEvent>());

        _mockTrackerRepo
            .Setup(r => r.GetAllDefinitionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TrackerDefinitionEntity>());

        _mockTrackerRepo
            .Setup(r => r.GetActiveInstancesAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<TrackerInstanceEntity>());

        _mockHeartRateService
            .Setup(s => s.GetHeartRatesByDateRangeAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<HeartRate>());

        _mockStepCountService
            .Setup(s => s.GetStepCountsByDateRangeAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<StepCount>());

    }

    [Fact]
    public async Task ExecuteAsync_FetchesAllRepositoriesAndPopulatesContext()
    {
        // Arrange
        var inputContext = new ChartDataContext
        {
            StartTime = StartTime,
            EndTime = EndTime,
            IntervalMinutes = 5,
            BufferStartTime = BufferStartTime,
        };

        // Act
        var result = await _stage.ExecuteAsync(inputContext, CancellationToken.None);

        // Assert — all collections are non-null
        result.SensorGlucoseList.Should().NotBeNull();
        result.BolusList.Should().NotBeNull();
        result.DisplayBoluses.Should().NotBeNull();
        result.CarbIntakeList.Should().NotBeNull();
        result.DisplayCarbIntakes.Should().NotBeNull();
        result.BgCheckList.Should().NotBeNull();
        result.DeviceEventList.Should().NotBeNull();
        result.TempBasalList.Should().NotBeNull();
        result.SystemEvents.Should().NotBeNull();
        result.TrackerDefinitions.Should().NotBeNull();
        result.TrackerInstances.Should().NotBeNull();
        result.StateSpans.Should().NotBeNull();

        // Assert — all 7 state span categories are present in the result
        result.StateSpans.Should().ContainKey(StateSpanCategory.PumpMode);
        result.StateSpans.Should().ContainKey(StateSpanCategory.Profile);
        result.StateSpans.Should().ContainKey(StateSpanCategory.Override);
        result.StateSpans.Should().ContainKey(StateSpanCategory.Sleep);
        result.StateSpans.Should().ContainKey(StateSpanCategory.Exercise);
        result.StateSpans.Should().ContainKey(StateSpanCategory.Illness);
        result.StateSpans.Should().ContainKey(StateSpanCategory.Travel);

        // Assert — request parameters are preserved unchanged
        result.StartTime.Should().Be(StartTime);
        result.EndTime.Should().Be(EndTime);
        result.BufferStartTime.Should().Be(BufferStartTime);
    }
}
