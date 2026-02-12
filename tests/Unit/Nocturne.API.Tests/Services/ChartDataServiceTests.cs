using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Helpers;
using Nocturne.API.Services;
using Nocturne.Connectors.Core.Constants;
using Nocturne.Core.Contracts;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Tests.Services;

/// <summary>
/// Comprehensive unit tests for ChartDataService.
/// Covers static helper methods, instance methods via mocks, and ChartColorMapper.
/// </summary>
public class ChartDataServiceTests
{
    #region Test Infrastructure

    private readonly Mock<IIobService> _mockIobService = new();
    private readonly Mock<ICobService> _mockCobService = new();
    private readonly Mock<ITreatmentService> _mockTreatmentService = new();
    private readonly Mock<ITreatmentFoodService> _mockTreatmentFoodService = new();
    private readonly Mock<IEntryService> _mockEntryService = new();
    private readonly Mock<IDeviceStatusService> _mockDeviceStatusService = new();
    private readonly Mock<IProfileService> _mockProfileService = new();
    private readonly Mock<IProfileDataService> _mockProfileDataService = new();
    private readonly Mock<StateSpanRepository> _mockStateSpanRepo;
    private readonly Mock<SystemEventRepository> _mockSystemEventRepo;
    private readonly Mock<TrackerRepository> _mockTrackerRepo;
    private readonly ILogger<ChartDataService> _logger = NullLogger<ChartDataService>.Instance;

    private readonly ChartDataService _service;

    // Common test timestamp: 2023-11-15T00:00:00Z in millis
    private const long TestMills = 1700000000000L;

    public ChartDataServiceTests()
    {
        // StateSpanRepository, SystemEventRepository, TrackerRepository are concrete classes
        // with virtual methods, so Moq can mock them if we pass null for their constructor deps.
        // We use MockBehavior.Loose and suppress constructor args.
        _mockStateSpanRepo = new Mock<StateSpanRepository>(MockBehavior.Loose, null!, null!, null!);
        _mockSystemEventRepo = new Mock<SystemEventRepository>(MockBehavior.Loose, null!);
        _mockTrackerRepo = new Mock<TrackerRepository>(MockBehavior.Loose, null!);

        _service = new ChartDataService(
            _mockIobService.Object,
            _mockCobService.Object,
            _mockTreatmentService.Object,
            _mockTreatmentFoodService.Object,
            _mockEntryService.Object,
            _mockDeviceStatusService.Object,
            _mockProfileService.Object,
            _mockProfileDataService.Object,
            _mockStateSpanRepo.Object,
            _mockSystemEventRepo.Object,
            _mockTrackerRepo.Object,
            new MemoryCache(new MemoryCacheOptions()),
            _logger
        );
    }

    #endregion

    #region BuildGlucoseData Tests

    public class BuildGlucoseDataTests
    {
        [Fact]
        public void EmptyList_ReturnsEmptyData_And_DefaultYMax()
        {
            var (data, yMax) = ChartDataService.BuildGlucoseData(new List<Entry>());

            data.Should().BeEmpty();
            // maxSgv defaults to 280 when no data, so yMax = Min(400, Max(280, 280) + 20) = 300
            yMax.Should().Be(300);
        }

        [Fact]
        public void SingleEntry_ReturnsOnePoint()
        {
            var entries = new List<Entry>
            {
                new()
                {
                    Mills = TestMills,
                    Sgv = 120.0,
                    Direction = "Flat",
                },
            };

            var (data, yMax) = ChartDataService.BuildGlucoseData(entries);

            data.Should().HaveCount(1);
            data[0].Time.Should().Be(TestMills);
            data[0].Sgv.Should().Be(120.0);
            data[0].Direction.Should().Be("Flat");
            // maxSgv=120, yMax = Min(400, Max(280, 120) + 20) = Min(400, 300) = 300
            yMax.Should().Be(300);
        }

        [Fact]
        public void FiltersEntriesWithNullSgv()
        {
            var entries = new List<Entry>
            {
                new()
                {
                    Mills = TestMills,
                    Sgv = 120.0,
                    Direction = "Flat",
                },
                new()
                {
                    Mills = TestMills + 1000,
                    Sgv = null,
                    Direction = "Up",
                },
                new()
                {
                    Mills = TestMills + 2000,
                    Sgv = 150.0,
                    Direction = "FortyFiveUp",
                },
            };

            var (data, _) = ChartDataService.BuildGlucoseData(entries);

            data.Should().HaveCount(2);
            data.Should().OnlyContain(g => g.Sgv > 0);
        }

        [Fact]
        public void OrdersByTime()
        {
            var entries = new List<Entry>
            {
                new() { Mills = TestMills + 2000, Sgv = 150.0 },
                new() { Mills = TestMills, Sgv = 120.0 },
                new() { Mills = TestMills + 1000, Sgv = 130.0 },
            };

            var (data, _) = ChartDataService.BuildGlucoseData(entries);

            data.Should().HaveCount(3);
            data[0].Time.Should().Be(TestMills);
            data[1].Time.Should().Be(TestMills + 1000);
            data[2].Time.Should().Be(TestMills + 2000);
        }

        [Theory]
        [InlineData(100, 300)] // maxSgv=100 < 280, yMax = Min(400, 280+20) = 300
        [InlineData(280, 300)] // maxSgv=280 = 280, yMax = Min(400, 280+20) = 300
        [InlineData(300, 320)] // maxSgv=300 > 280, yMax = Min(400, 300+20) = 320
        [InlineData(390, 400)] // maxSgv=390, yMax = Min(400, 390+20) = 400
        [InlineData(500, 400)] // maxSgv=500, yMax = Min(400, 500+20) = 400
        public void YMaxCalculation_VariousSgvValues(double sgv, double expectedYMax)
        {
            var entries = new List<Entry>
            {
                new() { Mills = TestMills, Sgv = sgv },
            };

            var (_, yMax) = ChartDataService.BuildGlucoseData(entries);

            yMax.Should().Be(expectedYMax);
        }
    }

    #endregion

    #region CategorizeTreatments Tests

    public class CategorizeTreatmentsTests
    {
        [Fact]
        public void EmptyList_ReturnsEmptyResults()
        {
            var (bolus, carbs, deviceEvents, carbIds) = ChartDataService.CategorizeTreatments(
                new List<Treatment>(),
                null
            );

            bolus.Should().BeEmpty();
            carbs.Should().BeEmpty();
            deviceEvents.Should().BeEmpty();
            carbIds.Should().BeEmpty();
        }

        [Fact]
        public void MealBolus_CategorizedAsBolus()
        {
            var treatments = new List<Treatment>
            {
                new()
                {
                    Id = "treat-1",
                    EventType = "Meal Bolus",
                    Insulin = 5.0,
                    Mills = TestMills,
                },
            };

            var (bolus, _, _, _) = ChartDataService.CategorizeTreatments(treatments, null);

            bolus.Should().HaveCount(1);
            bolus[0].Insulin.Should().Be(5.0);
            bolus[0].BolusType.Should().Be(BolusType.MealBolus);
            bolus[0].Time.Should().Be(TestMills);
            bolus[0].TreatmentId.Should().Be("treat-1");
        }

        [Fact]
        public void CorrectionBolus_CategorizedAsBolus()
        {
            var treatments = new List<Treatment>
            {
                new()
                {
                    EventType = "Correction Bolus",
                    Insulin = 2.5,
                    Mills = TestMills,
                },
            };

            var (bolus, _, _, _) = ChartDataService.CategorizeTreatments(treatments, null);

            bolus.Should().HaveCount(1);
            bolus[0].BolusType.Should().Be(BolusType.CorrectionBolus);
        }

        [Fact]
        public void Smb_CategorizedAsBolus()
        {
            var treatments = new List<Treatment>
            {
                new()
                {
                    EventType = "SMB",
                    Insulin = 0.3,
                    Mills = TestMills,
                },
            };

            var (bolus, _, _, _) = ChartDataService.CategorizeTreatments(treatments, null);

            bolus.Should().HaveCount(1);
            bolus[0].BolusType.Should().Be(BolusType.Smb);
        }

        [Fact]
        public void UnknownBolusTypeWithBolusInName_StillCategorized()
        {
            var treatments = new List<Treatment>
            {
                new()
                {
                    EventType = "Extended Bolus",
                    Insulin = 3.0,
                    Mills = TestMills,
                },
            };

            var (bolus, _, _, _) = ChartDataService.CategorizeTreatments(treatments, null);

            // "Extended Bolus" is not in BolusEventTypeMap but contains "Bolus" case-insensitive
            bolus.Should().HaveCount(1);
        }

        [Fact]
        public void TreatmentWithZeroInsulin_NotCategorizedAsBolus()
        {
            var treatments = new List<Treatment>
            {
                new()
                {
                    EventType = "Meal Bolus",
                    Insulin = 0.0,
                    Mills = TestMills,
                },
            };

            var (bolus, _, _, _) = ChartDataService.CategorizeTreatments(treatments, null);

            bolus.Should().BeEmpty();
        }

        [Fact]
        public void TreatmentWithNullInsulin_NotCategorizedAsBolus()
        {
            var treatments = new List<Treatment>
            {
                new()
                {
                    EventType = "Meal Bolus",
                    Insulin = null,
                    Mills = TestMills,
                },
            };

            var (bolus, _, _, _) = ChartDataService.CategorizeTreatments(treatments, null);

            bolus.Should().BeEmpty();
        }

        [Fact]
        public void CarbTreatment_CategorizedAsCarb()
        {
            var treatmentId = Guid.NewGuid().ToString();
            var treatments = new List<Treatment>
            {
                new()
                {
                    Id = treatmentId,
                    EventType = "Carb Correction",
                    Carbs = 30.0,
                    Mills = TestMills,
                    FoodType = "Pizza",
                },
            };

            var (_, carbs, _, carbIds) = ChartDataService.CategorizeTreatments(treatments, null);

            carbs.Should().HaveCount(1);
            carbs[0].Carbs.Should().Be(30.0);
            carbs[0].Label.Should().Be("Pizza");
            carbs[0].Time.Should().Be(TestMills);
            carbs[0].IsOffset.Should().BeFalse();
            carbIds.Should().HaveCount(1);
            carbIds[0].Should().Be(Guid.Parse(treatmentId));
        }

        [Fact]
        public void CarbTreatment_WithoutFoodType_UsesNotesTruncated()
        {
            var treatments = new List<Treatment>
            {
                new()
                {
                    Id = Guid.NewGuid().ToString(),
                    EventType = "Carb Correction",
                    Carbs = 20.0,
                    Mills = TestMills,
                    FoodType = null,
                    Notes = "This is a really long note about what I ate",
                },
            };

            var (_, carbs, _, _) = ChartDataService.CategorizeTreatments(treatments, null);

            carbs.Should().HaveCount(1);
            carbs[0].Label.Should().Be("This is a really lon");
            carbs[0].Label!.Length.Should().BeLessThanOrEqualTo(20);
        }

        [Fact]
        public void CarbTreatment_WithoutFoodTypeOrNotes_UsesMealName()
        {
            // 12:00 UTC = Lunch time
            var noonUtcMills = DateTimeOffset
                .Parse("2023-11-15T12:00:00Z")
                .ToUnixTimeMilliseconds();
            var treatments = new List<Treatment>
            {
                new()
                {
                    Id = Guid.NewGuid().ToString(),
                    EventType = "Carb Correction",
                    Carbs = 20.0,
                    Mills = noonUtcMills,
                    FoodType = null,
                    Notes = null,
                },
            };

            var (_, carbs, _, _) = ChartDataService.CategorizeTreatments(treatments, null);

            carbs.Should().HaveCount(1);
            carbs[0].Label.Should().Be("Lunch");
        }

        [Fact]
        public void CarbTreatment_WithZeroCarbs_NotCategorized()
        {
            var treatments = new List<Treatment>
            {
                new()
                {
                    EventType = "Carb Correction",
                    Carbs = 0.0,
                    Mills = TestMills,
                },
            };

            var (_, carbs, _, _) = ChartDataService.CategorizeTreatments(treatments, null);

            carbs.Should().BeEmpty();
        }

        [Fact]
        public void DeviceEvent_SiteChange_Categorized()
        {
            var treatments = new List<Treatment>
            {
                new()
                {
                    EventType = "Site Change",
                    Mills = TestMills,
                    Notes = "Left arm",
                },
            };

            var (_, _, deviceEvents, _) = ChartDataService.CategorizeTreatments(treatments, null);

            deviceEvents.Should().HaveCount(1);
            deviceEvents[0].EventType.Should().Be(DeviceEventType.SiteChange);
            deviceEvents[0].Notes.Should().Be("Left arm");
            deviceEvents[0].Color.Should().Be(ChartColor.InsulinBolus);
        }

        [Fact]
        public void DeviceEvent_SensorStart_Categorized()
        {
            var treatments = new List<Treatment>
            {
                new() { EventType = "Sensor Start", Mills = TestMills },
            };

            var (_, _, deviceEvents, _) = ChartDataService.CategorizeTreatments(treatments, null);

            deviceEvents.Should().HaveCount(1);
            deviceEvents[0].EventType.Should().Be(DeviceEventType.SensorStart);
            deviceEvents[0].Color.Should().Be(ChartColor.GlucoseInRange);
        }

        [Fact]
        public void ComboTreatment_BothBolusAndCarbs()
        {
            var treatmentId = Guid.NewGuid().ToString();
            var treatments = new List<Treatment>
            {
                new()
                {
                    Id = treatmentId,
                    EventType = "Meal Bolus",
                    Insulin = 5.0,
                    Carbs = 30.0,
                    Mills = TestMills,
                    FoodType = "Pasta",
                },
            };

            var (bolus, carbs, _, carbIds) = ChartDataService.CategorizeTreatments(
                treatments,
                null
            );

            bolus.Should().HaveCount(1);
            carbs.Should().HaveCount(1);
            bolus[0].Insulin.Should().Be(5.0);
            carbs[0].Carbs.Should().Be(30.0);
            carbs[0].Label.Should().Be("Pasta");
            carbIds.Should().HaveCount(1);
        }

        [Fact]
        public void OverrideDetection_WhenSuggestedDiffersFromActual()
        {
            var treatments = new List<Treatment>
            {
                new()
                {
                    EventType = "Meal Bolus",
                    Insulin = 5.0,
                    Mills = TestMills,
                    InsulinRecommendationForCarbs = 3.0,
                    InsulinRecommendationForCorrection = 1.0,
                    // suggestedTotal = 4.0, actual = 5.0, diff = 1.0 > 0.05
                },
            };

            var (bolus, _, _, _) = ChartDataService.CategorizeTreatments(treatments, null);

            bolus.Should().HaveCount(1);
            bolus[0].IsOverride.Should().BeTrue();
        }

        [Fact]
        public void OverrideDetection_WhenSuggestedMatchesActual()
        {
            var treatments = new List<Treatment>
            {
                new()
                {
                    EventType = "Meal Bolus",
                    Insulin = 4.0,
                    Mills = TestMills,
                    InsulinRecommendationForCarbs = 3.0,
                    InsulinRecommendationForCorrection = 1.0,
                    // suggestedTotal = 4.0, actual = 4.0, diff = 0.0 <= 0.05
                },
            };

            var (bolus, _, _, _) = ChartDataService.CategorizeTreatments(treatments, null);

            bolus.Should().HaveCount(1);
            bolus[0].IsOverride.Should().BeFalse();
        }

        [Fact]
        public void OverrideDetection_WhenNoRecommendations()
        {
            var treatments = new List<Treatment>
            {
                new()
                {
                    EventType = "Bolus",
                    Insulin = 5.0,
                    Mills = TestMills,
                    InsulinRecommendationForCarbs = null,
                    InsulinRecommendationForCorrection = null,
                    // suggestedTotal = 0.0, so isOverride is false (suggestedTotal not > 0)
                },
            };

            var (bolus, _, _, _) = ChartDataService.CategorizeTreatments(treatments, null);

            bolus.Should().HaveCount(1);
            bolus[0].IsOverride.Should().BeFalse();
        }

        [Fact]
        public void AllDeviceEventTypes_Categorized()
        {
            var treatments = new List<Treatment>
            {
                new() { EventType = "Sensor Start", Mills = TestMills },
                new() { EventType = "Sensor Change", Mills = TestMills + 1000 },
                new() { EventType = "Sensor Stop", Mills = TestMills + 2000 },
                new() { EventType = "Site Change", Mills = TestMills + 3000 },
                new() { EventType = "Insulin Change", Mills = TestMills + 4000 },
                new() { EventType = "Pump Battery Change", Mills = TestMills + 5000 },
            };

            var (_, _, deviceEvents, _) = ChartDataService.CategorizeTreatments(treatments, null);

            deviceEvents.Should().HaveCount(6);
            deviceEvents[0].EventType.Should().Be(DeviceEventType.SensorStart);
            deviceEvents[1].EventType.Should().Be(DeviceEventType.SensorChange);
            deviceEvents[2].EventType.Should().Be(DeviceEventType.SensorStop);
            deviceEvents[3].EventType.Should().Be(DeviceEventType.SiteChange);
            deviceEvents[4].EventType.Should().Be(DeviceEventType.InsulinChange);
            deviceEvents[5].EventType.Should().Be(DeviceEventType.PumpBatteryChange);
        }

        [Fact]
        public void NonGuidTreatmentId_NotAddedToCarbIds()
        {
            var treatments = new List<Treatment>
            {
                new()
                {
                    Id = "not-a-guid",
                    Carbs = 20.0,
                    Mills = TestMills,
                },
            };

            var (_, carbs, _, carbIds) = ChartDataService.CategorizeTreatments(treatments, null);

            carbs.Should().HaveCount(1);
            carbIds.Should().BeEmpty();
        }
    }

    #endregion

    #region GetMealNameForTime Tests

    public class GetMealNameForTimeTests
    {
        [Theory]
        [InlineData(5, "Breakfast")]
        [InlineData(10, "Breakfast")]
        [InlineData(11, "Lunch")]
        [InlineData(14, "Lunch")]
        [InlineData(15, "Snack")]
        [InlineData(16, "Snack")]
        [InlineData(17, "Dinner")]
        [InlineData(20, "Dinner")]
        [InlineData(21, "Late Night")]
        [InlineData(23, "Late Night")]
        [InlineData(0, "Late Night")]
        [InlineData(4, "Late Night")]
        public void UtcTimeBuckets(int hourUtc, string expected)
        {
            // Build a mills value for 2023-11-15 at the specified UTC hour
            var dto = new DateTimeOffset(2023, 11, 15, hourUtc, 30, 0, TimeSpan.Zero);
            var mills = dto.ToUnixTimeMilliseconds();

            var result = ChartDataService.GetMealNameForTime(mills, null);

            result.Should().Be(expected);
        }

        [Fact]
        public void NullTimezone_FallsBackToUtc()
        {
            // 12:00 UTC = Lunch
            var dto = new DateTimeOffset(2023, 11, 15, 12, 0, 0, TimeSpan.Zero);
            var mills = dto.ToUnixTimeMilliseconds();

            var result = ChartDataService.GetMealNameForTime(mills, null);

            result.Should().Be("Lunch");
        }

        [Fact]
        public void TimezoneConversion_NewYork()
        {
            // 6:00 UTC in America/New_York (UTC-5 in November) = 1:00 AM local = "Late Night"
            var dto = new DateTimeOffset(2023, 11, 15, 6, 0, 0, TimeSpan.Zero);
            var mills = dto.ToUnixTimeMilliseconds();

            var result = ChartDataService.GetMealNameForTime(mills, "America/New_York");

            result.Should().Be("Late Night");
        }

        [Fact]
        public void TimezoneConversion_NewYork_Breakfast()
        {
            // 12:00 UTC in America/New_York (UTC-5) = 7:00 AM local = "Breakfast"
            var dto = new DateTimeOffset(2023, 11, 15, 12, 0, 0, TimeSpan.Zero);
            var mills = dto.ToUnixTimeMilliseconds();

            var result = ChartDataService.GetMealNameForTime(mills, "America/New_York");

            result.Should().Be("Breakfast");
        }

        [Fact]
        public void InvalidTimezone_FallsBackToUtc()
        {
            // 12:00 UTC = Lunch (falls back to UTC on invalid tz)
            var dto = new DateTimeOffset(2023, 11, 15, 12, 0, 0, TimeSpan.Zero);
            var mills = dto.ToUnixTimeMilliseconds();

            var result = ChartDataService.GetMealNameForTime(mills, "Invalid/Timezone");

            result.Should().Be("Lunch");
        }
    }

    #endregion

    #region MapSystemEvents Tests

    public class MapSystemEventsTests
    {
        [Fact]
        public void NullInput_ReturnsEmptyList()
        {
            var result = ChartDataService.MapSystemEvents(null);

            result.Should().BeEmpty();
        }

        [Fact]
        public void EmptyList_ReturnsEmptyList()
        {
            var result = ChartDataService.MapSystemEvents(Enumerable.Empty<SystemEvent>());

            result.Should().BeEmpty();
        }

        [Fact]
        public void MapsFieldsCorrectly()
        {
            var events = new List<SystemEvent>
            {
                new()
                {
                    Id = "evt-1",
                    EventType = SystemEventType.Warning,
                    Category = SystemEventCategory.Pump,
                    Code = "LOW_RESERVOIR",
                    Description = "Low reservoir",
                    Mills = TestMills,
                },
            };

            var result = ChartDataService.MapSystemEvents(events);

            result.Should().HaveCount(1);
            result[0].Id.Should().Be("evt-1");
            result[0].Time.Should().Be(TestMills);
            result[0].EventType.Should().Be(SystemEventType.Warning);
            result[0].Category.Should().Be(SystemEventCategory.Pump);
            result[0].Code.Should().Be("LOW_RESERVOIR");
            result[0].Description.Should().Be("Low reservoir");
        }

        [Theory]
        [InlineData(SystemEventType.Alarm, ChartColor.SystemEventAlarm)]
        [InlineData(SystemEventType.Hazard, ChartColor.SystemEventHazard)]
        [InlineData(SystemEventType.Warning, ChartColor.SystemEventWarning)]
        [InlineData(SystemEventType.Info, ChartColor.SystemEventInfo)]
        public void ColorAssignment(SystemEventType eventType, ChartColor expectedColor)
        {
            var events = new List<SystemEvent>
            {
                new()
                {
                    Id = "e1",
                    EventType = eventType,
                    Mills = TestMills,
                },
            };

            var result = ChartDataService.MapSystemEvents(events);

            result[0].Color.Should().Be(expectedColor);
        }

        [Fact]
        public void MultipleEvents_AllMapped()
        {
            var events = new List<SystemEvent>
            {
                new()
                {
                    Id = "e1",
                    EventType = SystemEventType.Alarm,
                    Mills = TestMills,
                },
                new()
                {
                    Id = "e2",
                    EventType = SystemEventType.Info,
                    Mills = TestMills + 1000,
                },
                new()
                {
                    Id = "e3",
                    EventType = SystemEventType.Warning,
                    Mills = TestMills + 2000,
                },
            };

            var result = ChartDataService.MapSystemEvents(events);

            result.Should().HaveCount(3);
        }
    }

    #endregion

    #region MapTrackerMarkers Tests

    public class MapTrackerMarkersTests
    {
        [Fact]
        public void EmptyInputs_ReturnsEmpty()
        {
            var result = ChartDataService.MapTrackerMarkers(
                Enumerable.Empty<TrackerDefinitionEntity>(),
                Enumerable.Empty<TrackerInstanceEntity>(),
                TestMills,
                TestMills + 86400000L
            );

            result.Should().BeEmpty();
        }

        [Fact]
        public void FiltersInstancesByTimeRange_DurationMode()
        {
            var defId = Guid.NewGuid();
            var definition = new TrackerDefinitionEntity
            {
                Id = defId,
                UserId = "user1",
                Name = "CGM Sensor",
                Category = TrackerCategory.Sensor,
                Icon = "activity",
                Mode = TrackerMode.Duration,
                LifespanHours = 24, // 1 day lifespan
            };

            var startTime = TestMills;
            var endTime = TestMills + (48L * 60 * 60 * 1000); // +2 days

            // Instance started at startTime: ExpectedEndAt = startTime + 24h = within range
            var startedAtDateTime = DateTimeOffset.FromUnixTimeMilliseconds(startTime).UtcDateTime;
            var inRangeInstance = new TrackerInstanceEntity
            {
                Id = Guid.NewGuid(),
                DefinitionId = defId,
                UserId = "user1",
                StartedAt = startedAtDateTime, // expires at startTime + 24h
                Definition = definition,
            };

            var defs = new List<TrackerDefinitionEntity> { definition };
            var instances = new List<TrackerInstanceEntity> { inRangeInstance };

            var result = ChartDataService.MapTrackerMarkers(defs, instances, startTime, endTime);

            result.Should().HaveCount(1);
            result[0].Name.Should().Be("CGM Sensor");
            result[0].Category.Should().Be(TrackerCategory.Sensor);
            result[0].Icon.Should().Be("activity");
            result[0].Color.Should().Be(ChartColor.TrackerSensor);
            result[0].DefinitionId.Should().Be(defId.ToString());
        }

        [Fact]
        public void EventMode_UsesScheduledAt()
        {
            var defId = Guid.NewGuid();
            var definition = new TrackerDefinitionEntity
            {
                Id = defId,
                UserId = "user1",
                Name = "Doctor Appointment",
                Category = TrackerCategory.Appointment,
                Icon = "calendar",
                Mode = TrackerMode.Event,
            };

            var startTime = TestMills;
            var endTime = TestMills + (24L * 60 * 60 * 1000);

            // Scheduled within range
            var scheduledTime = DateTimeOffset
                .FromUnixTimeMilliseconds(TestMills + (12L * 60 * 60 * 1000))
                .UtcDateTime;
            var instance = new TrackerInstanceEntity
            {
                Id = Guid.NewGuid(),
                DefinitionId = defId,
                UserId = "user1",
                StartedAt = DateTime.UtcNow.AddDays(-7),
                ScheduledAt = scheduledTime,
                Definition = definition,
            };

            var result = ChartDataService.MapTrackerMarkers(
                new[] { definition },
                new[] { instance },
                startTime,
                endTime
            );

            result.Should().HaveCount(1);
            result[0].Name.Should().Be("Doctor Appointment");
            result[0].Category.Should().Be(TrackerCategory.Appointment);
            result[0].Color.Should().Be(ChartColor.TrackerAppointment);
        }

        [Fact]
        public void MissingDefinition_UsesFallbackValues()
        {
            var defId = Guid.NewGuid();
            var otherDefId = Guid.NewGuid();

            // Definition for a different ID
            var otherDef = new TrackerDefinitionEntity
            {
                Id = otherDefId,
                Name = "Other",
                Category = TrackerCategory.Battery,
            };

            // Instance referencing a definition not in the list
            // Use event mode with ScheduledAt for simpler setup
            var scheduledTime = DateTimeOffset
                .FromUnixTimeMilliseconds(TestMills + (6L * 60 * 60 * 1000))
                .UtcDateTime;

            var instance = new TrackerInstanceEntity
            {
                Id = Guid.NewGuid(),
                DefinitionId = defId,
                UserId = "user1",
                StartedAt = DateTime.UtcNow,
                ScheduledAt = scheduledTime,
                Definition = new TrackerDefinitionEntity
                {
                    Id = defId,
                    Mode = TrackerMode.Event,
                    // Minimal definition just to make ExpectedEndAt work
                },
            };

            var startTime = TestMills;
            var endTime = TestMills + (24L * 60 * 60 * 1000);

            // Pass otherDef as the only definition in the list,
            // but instance references defId
            var result = ChartDataService.MapTrackerMarkers(
                new[] { otherDef },
                new[] { instance },
                startTime,
                endTime
            );

            result.Should().HaveCount(1);
            result[0].Name.Should().Be("Tracker"); // fallback
            result[0].Category.Should().Be(TrackerCategory.Custom); // fallback
            result[0].Color.Should().Be(ChartColor.TrackerCustom);
        }

        [Fact]
        public void InstanceWithoutExpectedEndAt_Excluded()
        {
            var defId = Guid.NewGuid();
            var definition = new TrackerDefinitionEntity
            {
                Id = defId,
                Name = "No Lifespan",
                Category = TrackerCategory.Custom,
                Mode = TrackerMode.Duration,
                LifespanHours = null, // no lifespan => ExpectedEndAt = null
            };

            var instance = new TrackerInstanceEntity
            {
                Id = Guid.NewGuid(),
                DefinitionId = defId,
                UserId = "user1",
                StartedAt = DateTime.UtcNow,
                Definition = definition,
            };

            var result = ChartDataService.MapTrackerMarkers(
                new[] { definition },
                new[] { instance },
                TestMills,
                TestMills + 86400000L
            );

            result.Should().BeEmpty();
        }

        [Fact]
        public void Results_OrderedByTime()
        {
            var defId = Guid.NewGuid();
            var definition = new TrackerDefinitionEntity
            {
                Id = defId,
                Name = "Sensor",
                Category = TrackerCategory.Sensor,
                Mode = TrackerMode.Event,
            };

            var startTime = TestMills;
            var endTime = TestMills + (24L * 60 * 60 * 1000);

            var instance1 = new TrackerInstanceEntity
            {
                Id = Guid.NewGuid(),
                DefinitionId = defId,
                UserId = "user1",
                StartedAt = DateTime.UtcNow,
                ScheduledAt = DateTimeOffset
                    .FromUnixTimeMilliseconds(TestMills + (18L * 60 * 60 * 1000))
                    .UtcDateTime,
                Definition = definition,
            };
            var instance2 = new TrackerInstanceEntity
            {
                Id = Guid.NewGuid(),
                DefinitionId = defId,
                UserId = "user1",
                StartedAt = DateTime.UtcNow,
                ScheduledAt = DateTimeOffset
                    .FromUnixTimeMilliseconds(TestMills + (6L * 60 * 60 * 1000))
                    .UtcDateTime,
                Definition = definition,
            };

            var result = ChartDataService.MapTrackerMarkers(
                new[] { definition },
                new[] { instance1, instance2 },
                startTime,
                endTime
            );

            result.Should().HaveCount(2);
            result[0].Time.Should().BeLessThan(result[1].Time);
        }
    }

    #endregion

    #region ExtractBasalDeliveryMetadata Tests

    public class ExtractBasalDeliveryMetadataTests
    {
        [Fact]
        public void NoMetadata_ReturnsDefaultRateAndScheduled()
        {
            var span = new StateSpan { Metadata = null };

            var (rate, origin) = ChartDataService.ExtractBasalDeliveryMetadata(span, 1.0);

            rate.Should().Be(1.0);
            origin.Should().Be(BasalDeliveryOrigin.Scheduled);
        }

        [Fact]
        public void EmptyMetadata_ReturnsDefaultRateAndScheduled()
        {
            var span = new StateSpan { Metadata = new Dictionary<string, object>() };

            var (rate, origin) = ChartDataService.ExtractBasalDeliveryMetadata(span, 1.0);

            rate.Should().Be(1.0);
            origin.Should().Be(BasalDeliveryOrigin.Scheduled);
        }

        [Fact]
        public void DoubleRate_ExtractsCorrectly()
        {
            var span = new StateSpan
            {
                Metadata = new Dictionary<string, object> { ["rate"] = 0.8 },
            };

            var (rate, _) = ChartDataService.ExtractBasalDeliveryMetadata(span, 1.0);

            rate.Should().Be(0.8);
        }

        [Fact]
        public void JsonElementRate_ExtractsCorrectly()
        {
            var rateElement = JsonSerializer.SerializeToElement(0.65);
            var span = new StateSpan
            {
                Metadata = new Dictionary<string, object> { ["rate"] = rateElement },
            };

            var (rate, _) = ChartDataService.ExtractBasalDeliveryMetadata(span, 1.0);

            rate.Should().Be(0.65);
        }

        [Fact]
        public void StringOrigin_Algorithm()
        {
            var span = new StateSpan
            {
                Metadata = new Dictionary<string, object> { ["origin"] = "Algorithm" },
            };

            var (_, origin) = ChartDataService.ExtractBasalDeliveryMetadata(span, 1.0);

            origin.Should().Be(BasalDeliveryOrigin.Algorithm);
        }

        [Fact]
        public void StringOrigin_Manual()
        {
            var span = new StateSpan
            {
                Metadata = new Dictionary<string, object> { ["origin"] = "Manual" },
            };

            var (_, origin) = ChartDataService.ExtractBasalDeliveryMetadata(span, 1.0);

            origin.Should().Be(BasalDeliveryOrigin.Manual);
        }

        [Fact]
        public void StringOrigin_Suspended()
        {
            var span = new StateSpan
            {
                Metadata = new Dictionary<string, object> { ["origin"] = "Suspended" },
            };

            var (_, origin) = ChartDataService.ExtractBasalDeliveryMetadata(span, 1.0);

            origin.Should().Be(BasalDeliveryOrigin.Suspended);
        }

        [Fact]
        public void StringOrigin_Scheduled_IsDefault()
        {
            var span = new StateSpan
            {
                Metadata = new Dictionary<string, object> { ["origin"] = "Scheduled" },
            };

            var (_, origin) = ChartDataService.ExtractBasalDeliveryMetadata(span, 1.0);

            origin.Should().Be(BasalDeliveryOrigin.Scheduled);
        }

        [Fact]
        public void StringOrigin_Unknown_DefaultsToScheduled()
        {
            var span = new StateSpan
            {
                Metadata = new Dictionary<string, object> { ["origin"] = "SomethingElse" },
            };

            var (_, origin) = ChartDataService.ExtractBasalDeliveryMetadata(span, 1.0);

            origin.Should().Be(BasalDeliveryOrigin.Scheduled);
        }

        [Fact]
        public void JsonElementOrigin_ExtractsCorrectly()
        {
            var originElement = JsonSerializer.SerializeToElement("Algorithm");
            var span = new StateSpan
            {
                Metadata = new Dictionary<string, object> { ["origin"] = originElement },
            };

            var (_, origin) = ChartDataService.ExtractBasalDeliveryMetadata(span, 1.0);

            origin.Should().Be(BasalDeliveryOrigin.Algorithm);
        }

        [Fact]
        public void BothRateAndOrigin_ExtractedTogether()
        {
            var span = new StateSpan
            {
                Metadata = new Dictionary<string, object> { ["rate"] = 0.5, ["origin"] = "Manual" },
            };

            var (rate, origin) = ChartDataService.ExtractBasalDeliveryMetadata(span, 1.0);

            rate.Should().Be(0.5);
            origin.Should().Be(BasalDeliveryOrigin.Manual);
        }

        [Fact]
        public void CaseInsensitiveOrigin()
        {
            var span = new StateSpan
            {
                Metadata = new Dictionary<string, object> { ["origin"] = "ALGORITHM" },
            };

            var (_, origin) = ChartDataService.ExtractBasalDeliveryMetadata(span, 1.0);

            origin.Should().Be(BasalDeliveryOrigin.Algorithm);
        }
    }

    #endregion

    #region GetProfileThresholds Tests

    [Fact]
    public void GetProfileThresholds_NoProfileData_ReturnsDefaults()
    {
        _mockProfileService.Setup(p => p.HasData()).Returns(false);

        var result = _service.GetProfileThresholds(TestMills);

        result.VeryLow.Should().Be(54);
        result.Low.Should().Be(70);
        result.High.Should().Be(180);
        result.VeryHigh.Should().Be(250);
    }

    [Fact]
    public void GetProfileThresholds_WithProfileData_UsesProfileValues()
    {
        _mockProfileService.Setup(p => p.HasData()).Returns(true);
        _mockProfileService.Setup(p => p.GetLowBGTarget(TestMills, null)).Returns(75);
        _mockProfileService.Setup(p => p.GetHighBGTarget(TestMills, null)).Returns(170);

        var result = _service.GetProfileThresholds(TestMills);

        result.VeryLow.Should().Be(54); // always 54
        result.Low.Should().Be(75);
        result.High.Should().Be(170);
        result.VeryHigh.Should().Be(250); // always 250
    }

    #endregion

    #region MapStateSpans Tests

    [Fact]
    public void MapStateSpans_PumpMode_CorrectColorMapping()
    {
        var spans = new List<StateSpan>
        {
            new()
            {
                Id = "span-1",
                State = "Automatic",
                StartMills = TestMills,
                EndMills = TestMills + 3600000,
            },
        };

        var result = _service.MapStateSpans(spans, StateSpanCategory.PumpMode);

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("span-1");
        result[0].Category.Should().Be(StateSpanCategory.PumpMode);
        result[0].State.Should().Be("Automatic");
        result[0].StartMills.Should().Be(TestMills);
        result[0].EndMills.Should().Be(TestMills + 3600000);
        result[0].Color.Should().Be(ChartColor.PumpModeAutomatic);
    }

    [Fact]
    public void MapStateSpans_Profile_UsesProfileColor()
    {
        var spans = new List<StateSpan>
        {
            new()
            {
                Id = "span-2",
                State = "Active",
                StartMills = TestMills,
                EndMills = TestMills + 3600000,
            },
        };

        var result = _service.MapStateSpans(spans, StateSpanCategory.Profile);

        result[0].Color.Should().Be(ChartColor.Profile);
    }

    [Fact]
    public void MapStateSpans_Override_CorrectColorMapping()
    {
        var spans = new List<StateSpan>
        {
            new()
            {
                Id = "span-3",
                State = "Boost",
                StartMills = TestMills,
                EndMills = TestMills + 3600000,
            },
        };

        var result = _service.MapStateSpans(spans, StateSpanCategory.Override);

        result[0].Color.Should().Be(ChartColor.PumpModeBoost);
    }

    [Fact]
    public void MapStateSpans_ActivityCategories_CorrectColorMapping()
    {
        var sleepSpan = new StateSpan
        {
            Id = "sleep-1",
            State = "Sleeping",
            StartMills = TestMills,
            EndMills = TestMills + 28800000, // 8 hours
        };

        var exerciseSpan = new StateSpan
        {
            Id = "exercise-1",
            State = "Running",
            StartMills = TestMills,
            EndMills = TestMills + 3600000,
        };

        var sleepResult = _service.MapStateSpans(new[] { sleepSpan }, StateSpanCategory.Sleep);
        var exerciseResult = _service.MapStateSpans(
            new[] { exerciseSpan },
            StateSpanCategory.Exercise
        );

        sleepResult[0].Color.Should().Be(ChartColor.ActivitySleep);
        exerciseResult[0].Color.Should().Be(ChartColor.ActivityExercise);
    }

    [Fact]
    public void MapStateSpans_NullState_DefaultsToUnknown()
    {
        var spans = new List<StateSpan>
        {
            new()
            {
                Id = "span-null",
                State = null,
                StartMills = TestMills,
                EndMills = TestMills + 3600000,
            },
        };

        var result = _service.MapStateSpans(spans, StateSpanCategory.PumpMode);

        result[0].State.Should().Be("Unknown");
    }

    [Fact]
    public void MapStateSpans_NullId_DefaultsToEmptyString()
    {
        var spans = new List<StateSpan>
        {
            new()
            {
                Id = null,
                State = "Active",
                StartMills = TestMills,
                EndMills = TestMills + 3600000,
            },
        };

        var result = _service.MapStateSpans(spans, StateSpanCategory.PumpMode);

        result[0].Id.Should().Be("");
    }

    [Fact]
    public void MapStateSpans_PreservesMetadata()
    {
        var metadata = new Dictionary<string, object> { ["key"] = "value" };
        var spans = new List<StateSpan>
        {
            new()
            {
                Id = "span-meta",
                State = "Active",
                StartMills = TestMills,
                EndMills = TestMills + 3600000,
                Metadata = metadata,
            },
        };

        var result = _service.MapStateSpans(spans, StateSpanCategory.PumpMode);

        result[0].Metadata.Should().BeSameAs(metadata);
    }

    #endregion

    #region MapBasalDeliverySpans Tests

    [Fact]
    public void MapBasalDeliverySpans_MapsRateAndOrigin()
    {
        var spans = new List<StateSpan>
        {
            new()
            {
                Id = "basal-1",
                StartMills = TestMills,
                EndMills = TestMills + 3600000,
                Source = "pump",
                Metadata = new Dictionary<string, object>
                {
                    ["rate"] = 0.8,
                    ["origin"] = "Algorithm",
                },
            },
        };

        var result = _service.MapBasalDeliverySpans(spans, 1.0);

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("basal-1");
        result[0].Rate.Should().Be(0.8);
        result[0].Origin.Should().Be(BasalDeliveryOrigin.Algorithm);
        result[0].Source.Should().Be("pump");
        result[0].FillColor.Should().Be(ChartColor.InsulinBasal);
        result[0].StrokeColor.Should().Be(ChartColor.InsulinBolus);
    }

    [Fact]
    public void MapBasalDeliverySpans_SuspendedOrigin_ZeroRate()
    {
        var spans = new List<StateSpan>
        {
            new()
            {
                Id = "basal-susp",
                StartMills = TestMills,
                EndMills = TestMills + 3600000,
                Metadata = new Dictionary<string, object>
                {
                    ["rate"] = 0.8,
                    ["origin"] = "Suspended",
                },
            },
        };

        var result = _service.MapBasalDeliverySpans(spans, 1.0);

        result[0].Rate.Should().Be(0);
        result[0].Origin.Should().Be(BasalDeliveryOrigin.Suspended);
        result[0].FillColor.Should().Be(ChartColor.PumpModeSuspended);
        result[0].StrokeColor.Should().Be(ChartColor.PumpModeSuspended);
    }

    [Fact]
    public void MapBasalDeliverySpans_ManualOrigin_ColorMapping()
    {
        var spans = new List<StateSpan>
        {
            new()
            {
                Id = "basal-manual",
                StartMills = TestMills,
                EndMills = TestMills + 3600000,
                Metadata = new Dictionary<string, object> { ["rate"] = 1.5, ["origin"] = "Manual" },
            },
        };

        var result = _service.MapBasalDeliverySpans(spans, 1.0);

        result[0].FillColor.Should().Be(ChartColor.InsulinTempBasal);
        result[0].StrokeColor.Should().Be(ChartColor.InsulinBolus);
    }

    #endregion

    #region MapTempBasalSpans Tests

    [Fact]
    public void MapTempBasalSpans_OnlyIncludesManualOrigin()
    {
        var spans = new List<StateSpan>
        {
            new()
            {
                Id = "manual-1",
                StartMills = TestMills,
                EndMills = TestMills + 3600000,
                Metadata = new Dictionary<string, object> { ["rate"] = 1.5, ["origin"] = "Manual" },
            },
            new()
            {
                Id = "algo-1",
                StartMills = TestMills + 3600000,
                EndMills = TestMills + 7200000,
                Metadata = new Dictionary<string, object>
                {
                    ["rate"] = 0.8,
                    ["origin"] = "Algorithm",
                },
            },
            new()
            {
                Id = "scheduled-1",
                StartMills = TestMills + 7200000,
                EndMills = TestMills + 10800000,
                Metadata = new Dictionary<string, object>
                {
                    ["rate"] = 1.0,
                    ["origin"] = "Scheduled",
                },
            },
        };

        var result = _service.MapTempBasalSpans(spans, 1.0);

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("manual-1");
        result[0].Category.Should().Be(StateSpanCategory.BasalDelivery);
        result[0].State.Should().Be("TempBasal");
        result[0].Color.Should().Be(ChartColor.InsulinBasal);
    }

    [Fact]
    public void MapTempBasalSpans_NoManualSpans_ReturnsEmpty()
    {
        var spans = new List<StateSpan>
        {
            new()
            {
                Id = "algo-1",
                StartMills = TestMills,
                EndMills = TestMills + 3600000,
                Metadata = new Dictionary<string, object>
                {
                    ["rate"] = 0.8,
                    ["origin"] = "Algorithm",
                },
            },
        };

        var result = _service.MapTempBasalSpans(spans, 1.0);

        result.Should().BeEmpty();
    }

    #endregion

    #region ChartColorMapper Tests

    public class ChartColorMapperTests
    {
        #region FromPumpMode

        [Theory]
        [InlineData("Automatic", ChartColor.PumpModeAutomatic)]
        [InlineData("Limited", ChartColor.PumpModeLimited)]
        [InlineData("Manual", ChartColor.PumpModeManual)]
        [InlineData("Boost", ChartColor.PumpModeBoost)]
        [InlineData("EaseOff", ChartColor.PumpModeEaseOff)]
        [InlineData("Sleep", ChartColor.PumpModeSleep)]
        [InlineData("Exercise", ChartColor.PumpModeExercise)]
        [InlineData("Suspended", ChartColor.PumpModeSuspended)]
        [InlineData("Off", ChartColor.PumpModeOff)]
        public void FromPumpMode_KnownStates(string state, ChartColor expected)
        {
            ChartColorMapper.FromPumpMode(state).Should().Be(expected);
        }

        [Fact]
        public void FromPumpMode_UnknownState_DefaultsToManual()
        {
            ChartColorMapper.FromPumpMode("UnknownMode").Should().Be(ChartColor.PumpModeManual);
        }

        #endregion

        #region FromSystemEvent

        [Theory]
        [InlineData(SystemEventType.Alarm, ChartColor.SystemEventAlarm)]
        [InlineData(SystemEventType.Hazard, ChartColor.SystemEventHazard)]
        [InlineData(SystemEventType.Warning, ChartColor.SystemEventWarning)]
        [InlineData(SystemEventType.Info, ChartColor.SystemEventInfo)]
        public void FromSystemEvent_AllTypes(SystemEventType type, ChartColor expected)
        {
            ChartColorMapper.FromSystemEvent(type).Should().Be(expected);
        }

        [Fact]
        public void FromSystemEvent_DefaultCase_ReturnsInfo()
        {
            // Cast an invalid enum value
            ChartColorMapper
                .FromSystemEvent((SystemEventType)999)
                .Should()
                .Be(ChartColor.SystemEventInfo);
        }

        #endregion

        #region FromDeviceEvent

        [Theory]
        [InlineData(DeviceEventType.SensorStart, ChartColor.GlucoseInRange)]
        [InlineData(DeviceEventType.SensorChange, ChartColor.GlucoseInRange)]
        [InlineData(DeviceEventType.SensorStop, ChartColor.GlucoseLow)]
        [InlineData(DeviceEventType.SiteChange, ChartColor.InsulinBolus)]
        [InlineData(DeviceEventType.InsulinChange, ChartColor.InsulinBasal)]
        [InlineData(DeviceEventType.PumpBatteryChange, ChartColor.Carbs)]
        public void FromDeviceEvent_AllTypes(DeviceEventType type, ChartColor expected)
        {
            ChartColorMapper.FromDeviceEvent(type).Should().Be(expected);
        }

        [Fact]
        public void FromDeviceEvent_DefaultCase_ReturnsMutedForeground()
        {
            ChartColorMapper
                .FromDeviceEvent((DeviceEventType)999)
                .Should()
                .Be(ChartColor.MutedForeground);
        }

        #endregion

        #region FromTracker

        [Theory]
        [InlineData(TrackerCategory.Sensor, ChartColor.TrackerSensor)]
        [InlineData(TrackerCategory.Cannula, ChartColor.TrackerCannula)]
        [InlineData(TrackerCategory.Reservoir, ChartColor.TrackerReservoir)]
        [InlineData(TrackerCategory.Battery, ChartColor.TrackerBattery)]
        [InlineData(TrackerCategory.Consumable, ChartColor.TrackerConsumable)]
        [InlineData(TrackerCategory.Appointment, ChartColor.TrackerAppointment)]
        [InlineData(TrackerCategory.Reminder, ChartColor.TrackerReminder)]
        [InlineData(TrackerCategory.Custom, ChartColor.TrackerCustom)]
        public void FromTracker_AllCategories(TrackerCategory category, ChartColor expected)
        {
            ChartColorMapper.FromTracker(category).Should().Be(expected);
        }

        [Fact]
        public void FromTracker_DefaultCase_ReturnsMutedForeground()
        {
            ChartColorMapper
                .FromTracker((TrackerCategory)999)
                .Should()
                .Be(ChartColor.MutedForeground);
        }

        #endregion

        #region FromActivity

        [Theory]
        [InlineData(StateSpanCategory.Sleep, ChartColor.ActivitySleep)]
        [InlineData(StateSpanCategory.Exercise, ChartColor.ActivityExercise)]
        [InlineData(StateSpanCategory.Illness, ChartColor.ActivityIllness)]
        [InlineData(StateSpanCategory.Travel, ChartColor.ActivityTravel)]
        public void FromActivity_AllCategories(StateSpanCategory category, ChartColor expected)
        {
            ChartColorMapper.FromActivity(category).Should().Be(expected);
        }

        [Fact]
        public void FromActivity_NonActivityCategory_ReturnsMutedForeground()
        {
            ChartColorMapper
                .FromActivity(StateSpanCategory.PumpMode)
                .Should()
                .Be(ChartColor.MutedForeground);
        }

        #endregion

        #region FromOverride

        [Theory]
        [InlineData("Boost", ChartColor.PumpModeBoost)]
        [InlineData("Exercise", ChartColor.PumpModeExercise)]
        [InlineData("Sleep", ChartColor.PumpModeSleep)]
        [InlineData("EaseOff", ChartColor.PumpModeEaseOff)]
        public void FromOverride_KnownStates(string state, ChartColor expected)
        {
            ChartColorMapper.FromOverride(state).Should().Be(expected);
        }

        [Fact]
        public void FromOverride_UnknownState_DefaultsToOverride()
        {
            ChartColorMapper.FromOverride("Custom").Should().Be(ChartColor.Override);
        }

        #endregion

        #region FillFromBasalOrigin

        [Theory]
        [InlineData(BasalDeliveryOrigin.Algorithm, ChartColor.InsulinBasal)]
        [InlineData(BasalDeliveryOrigin.Manual, ChartColor.InsulinTempBasal)]
        [InlineData(BasalDeliveryOrigin.Suspended, ChartColor.PumpModeSuspended)]
        [InlineData(BasalDeliveryOrigin.Inferred, ChartColor.InsulinBasal)]
        public void FillFromBasalOrigin_AllOrigins(BasalDeliveryOrigin origin, ChartColor expected)
        {
            ChartColorMapper.FillFromBasalOrigin(origin).Should().Be(expected);
        }

        [Fact]
        public void FillFromBasalOrigin_DefaultCase_ReturnsInsulinBasal()
        {
            ChartColorMapper
                .FillFromBasalOrigin((BasalDeliveryOrigin)999)
                .Should()
                .Be(ChartColor.InsulinBasal);
        }

        #endregion

        #region StrokeFromBasalOrigin

        [Theory]
        [InlineData(BasalDeliveryOrigin.Algorithm, ChartColor.InsulinBolus)]
        [InlineData(BasalDeliveryOrigin.Manual, ChartColor.InsulinBolus)]
        [InlineData(BasalDeliveryOrigin.Suspended, ChartColor.PumpModeSuspended)]
        [InlineData(BasalDeliveryOrigin.Inferred, ChartColor.InsulinBasal)]
        public void StrokeFromBasalOrigin_AllOrigins(
            BasalDeliveryOrigin origin,
            ChartColor expected
        )
        {
            ChartColorMapper.StrokeFromBasalOrigin(origin).Should().Be(expected);
        }

        [Fact]
        public void StrokeFromBasalOrigin_DefaultCase_ReturnsInsulinBasal()
        {
            ChartColorMapper
                .StrokeFromBasalOrigin((BasalDeliveryOrigin)999)
                .Should()
                .Be(ChartColor.InsulinBasal);
        }

        #endregion
    }

    #endregion
}
