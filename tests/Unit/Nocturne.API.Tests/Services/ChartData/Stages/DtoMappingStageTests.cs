using FluentAssertions;
using Moq;
using Nocturne.API.Services.ChartData;
using Nocturne.API.Services.ChartData.Stages;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Services.ChartData.Stages;

public class DtoMappingStageTests
{
    private const long StartTime = 1700000000000L;
    private const long EndTime = 1700086400000L;

    private readonly Mock<ITreatmentFoodService> _mockTreatmentFoodService;
    private readonly DtoMappingStage _stage;

    public DtoMappingStageTests()
    {
        _mockTreatmentFoodService = new Mock<ITreatmentFoodService>();
        _stage = new DtoMappingStage(_mockTreatmentFoodService.Object);
    }

    [Fact]
    public async Task ExecuteAsync_MapsAllMarkersAndSpans()
    {
        // Arrange
        var now = DateTimeOffset.FromUnixTimeMilliseconds(StartTime + 3600_000).UtcDateTime;

        var glucose = new SensorGlucose
        {
            Id = Guid.NewGuid(),
            Timestamp = now,
            Mgdl = 120,
        };

        var bolus = new Bolus
        {
            Id = Guid.NewGuid(),
            Timestamp = now,
            Insulin = 2.5,
        };

        var carbIntakeId = Guid.NewGuid();
        var carbIntake = new CarbIntake
        {
            Id = carbIntakeId,
            Timestamp = now,
            Carbs = 30.0,
        };

        var bgCheck = new BGCheck
        {
            Id = Guid.NewGuid(),
            Timestamp = now,
            Glucose = 110,
            Units = GlucoseUnit.MgDl,
        };

        var deviceEvent = new DeviceEvent
        {
            Id = Guid.NewGuid(),
            Timestamp = now,
            EventType = DeviceEventType.SiteChange,
        };

        var tempBasal = new TempBasal
        {
            Id = Guid.NewGuid(),
            StartTimestamp = now,
            EndTimestamp = now.AddHours(1),
            Rate = 0.8,
            Origin = TempBasalOrigin.Manual,
        };

        var systemEvent = new SystemEvent
        {
            Id = "evt-1",
            Mills = StartTime + 1000,
            EventType = SystemEventType.Alarm,
            Category = SystemEventCategory.Cgm,
        };

        var definitionId = Guid.NewGuid();
        var trackerDef = new TrackerDefinitionEntity
        {
            Id = definitionId,
            Name = "G7 Sensor",
            Category = TrackerCategory.Consumable,
            Mode = TrackerMode.Duration,
            LifespanHours = 10,
        };

        var trackerInstance = new TrackerInstanceEntity
        {
            Id = Guid.NewGuid(),
            DefinitionId = definitionId,
            StartedAt = DateTimeOffset.FromUnixTimeMilliseconds(StartTime).UtcDateTime,
            Definition = trackerDef,
        };

        var pumpModeSpan = new StateSpan
        {
            Id = "span-1",
            Category = StateSpanCategory.PumpMode,
            State = "Automatic",
            StartTimestamp = now,
            EndTimestamp = now.AddHours(1),
        };

        var stateSpans = new Dictionary<StateSpanCategory, IEnumerable<StateSpan>>
        {
            [StateSpanCategory.PumpMode] = [pumpModeSpan],
            [StateSpanCategory.Profile] = [],
            [StateSpanCategory.Override] = [],
            [StateSpanCategory.Exercise] = [],
            [StateSpanCategory.Illness] = [],
            [StateSpanCategory.Travel] = [],
        };

        _mockTreatmentFoodService
            .Setup(s => s.GetByCarbIntakeIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var context = new ChartDataContext
        {
            StartTime = StartTime,
            EndTime = EndTime,
            SensorGlucoseList = [glucose],
            DisplayBoluses = [bolus],
            DisplayCarbIntakes = [carbIntake],
            BgCheckList = [bgCheck],
            DeviceEventList = [deviceEvent],
            TempBasalList = [tempBasal],
            SystemEvents = [systemEvent],
            TrackerDefinitions = [trackerDef],
            TrackerInstances = [trackerInstance],
            StateSpans = stateSpans,
        };

        // Act
        var result = await _stage.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.GlucoseData.Should().HaveCount(1);
        result.GlucoseYMax.Should().BeGreaterThan(0);
        result.BolusMarkers.Should().HaveCount(1);
        result.CarbMarkers.Should().HaveCount(1);
        result.BgCheckMarkers.Should().HaveCount(1);
        result.DeviceEventMarkers.Should().HaveCount(1);
        result.PumpModeSpans.Should().HaveCount(1);
        result.PumpModeSpans[0].State.Should().Be("Automatic");
        result.ProfileSpans.Should().BeEmpty();
        result.OverrideSpans.Should().BeEmpty();
        result.ActivitySpans.Should().BeEmpty();
        result.BasalDeliverySpans.Should().HaveCount(1);
        result.TempBasalSpans.Should().HaveCount(1);
        result.SystemEventMarkers.Should().HaveCount(1);
        result.TrackerMarkers.Should().HaveCount(1);
    }

    [Fact]
    public async Task ExecuteAsync_SleepSessions_MapToSleepKindNotExercise()
    {
        // Arrange
        var overnightStart = DateTimeOffset.FromUnixTimeMilliseconds(StartTime).UtcDateTime;
        var overnight = new SleepSession
        {
            Id = "sleep-1",
            StartTime = overnightStart,
            EndTime = overnightStart.AddHours(8),
            Type = SleepSessionType.Overnight,
        };
        var nap = new SleepSession
        {
            Id = "sleep-2",
            StartTime = overnightStart.AddHours(14),
            EndTime = overnightStart.AddHours(15),
            Type = SleepSessionType.Nap,
        };

        _mockTreatmentFoodService
            .Setup(s => s.GetByCarbIntakeIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var context = new ChartDataContext
        {
            StartTime = StartTime,
            EndTime = EndTime,
            StateSpans = new Dictionary<StateSpanCategory, IEnumerable<StateSpan>>(),
            SleepSessions = [overnight, nap],
        };

        // Act
        var result = await _stage.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.ActivitySpans.Should().HaveCount(2);
        result.ActivitySpans.Should().OnlyContain(s => s.Kind == ChartSpanKind.Sleep);
        result.ActivitySpans.Should().OnlyContain(s => s.Category == null);
        result.ActivitySpans.Should().NotContain(s => s.Category == StateSpanCategory.Exercise);
        result.ActivitySpans.Should().OnlyContain(s => s.Color == ChartColor.ActivitySleep);

        var overnightSpan = result.ActivitySpans.Single(s => s.Id == "sleep-1");
        overnightSpan.State.Should().Be("Sleep");
        var napSpan = result.ActivitySpans.Single(s => s.Id == "sleep-2");
        napSpan.State.Should().Be("Nap");
    }

    [Fact]
    public async Task ExecuteAsync_ActivityStateSpans_MapToStateSpanKindWithCategory()
    {
        // Arrange
        var now = DateTimeOffset.FromUnixTimeMilliseconds(StartTime + 3600_000).UtcDateTime;
        var exerciseSpan = new StateSpan
        {
            Id = "ex-1",
            Category = StateSpanCategory.Exercise,
            State = "Running",
            StartTimestamp = now,
            EndTimestamp = now.AddHours(1),
        };

        var stateSpans = new Dictionary<StateSpanCategory, IEnumerable<StateSpan>>
        {
            [StateSpanCategory.Exercise] = [exerciseSpan],
        };

        _mockTreatmentFoodService
            .Setup(s => s.GetByCarbIntakeIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var context = new ChartDataContext
        {
            StartTime = StartTime,
            EndTime = EndTime,
            StateSpans = stateSpans,
        };

        // Act
        var result = await _stage.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.ActivitySpans.Should().HaveCount(1);
        var span = result.ActivitySpans[0];
        span.Kind.Should().Be(ChartSpanKind.StateSpan);
        span.Category.Should().Be(StateSpanCategory.Exercise);
        span.State.Should().Be("Running");
    }

    [Fact]
    public async Task ExecuteAsync_WithEmptyData_ReturnsEmptyCollections()
    {
        // Arrange
        var stateSpans = new Dictionary<StateSpanCategory, IEnumerable<StateSpan>>
        {
            [StateSpanCategory.PumpMode] = [],
            [StateSpanCategory.Profile] = [],
            [StateSpanCategory.Override] = [],
            [StateSpanCategory.Exercise] = [],
            [StateSpanCategory.Illness] = [],
            [StateSpanCategory.Travel] = [],
        };

        _mockTreatmentFoodService
            .Setup(s => s.GetByCarbIntakeIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var context = new ChartDataContext
        {
            StartTime = StartTime,
            EndTime = EndTime,
            StateSpans = stateSpans,
        };

        // Act
        var result = await _stage.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.GlucoseData.Should().BeEmpty();
        result.BolusMarkers.Should().BeEmpty();
        result.CarbMarkers.Should().BeEmpty();
        result.BgCheckMarkers.Should().BeEmpty();
        result.DeviceEventMarkers.Should().BeEmpty();
        result.PumpModeSpans.Should().BeEmpty();
        result.ProfileSpans.Should().BeEmpty();
        result.OverrideSpans.Should().BeEmpty();
        result.ActivitySpans.Should().BeEmpty();
        result.BasalDeliverySpans.Should().BeEmpty();
        result.TempBasalSpans.Should().BeEmpty();
        result.SystemEventMarkers.Should().BeEmpty();
        result.TrackerMarkers.Should().BeEmpty();
    }
}
