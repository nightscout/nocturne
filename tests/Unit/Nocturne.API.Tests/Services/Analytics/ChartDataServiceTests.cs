using Nocturne.Core.Clinical.LoopAnalysis;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Helpers;
using Nocturne.API.Services.Analytics;
using Nocturne.API.Services.ChartData;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Basal;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Tests.Services.Analytics;

/// <summary>
/// Unit tests for ChartDataService static helper methods and ChartColorMapper.
/// Instance-method tests (GetProfileThresholds, MapStateSpans, BuildIobCobSeries) are covered
/// by their respective stage test classes: ProfileLoadStageTests, DtoMappingStageTests,
/// IobCobComputeStageTests.
/// </summary>
public class ChartDataServiceTests
{
    // Common test timestamp: 2023-11-15T00:00:00Z in millis
    private const long TestMills = 1700000000000L;

    private static IStateSpanRepository NoOverrides() =>
        Mock.Of<IStateSpanRepository>(r => r.GetByCategories(
            It.IsAny<IEnumerable<StateSpanCategory>>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>())
            == Task.FromResult(new Dictionary<StateSpanCategory, List<StateSpan>>()));

    #region BuildGlucoseData Tests

    public class BuildGlucoseDataTests
    {
        [Fact]
        public void EmptyList_ReturnsEmptyData_And_DefaultYMax()
        {
            var (data, yMax) = ChartDataService.BuildGlucoseData(new List<SensorGlucose>());

            data.Should().BeEmpty();
            // maxSgv defaults to 280 when no data, so yMax = Min(400, Max(280, 280) + 20) = 300
            yMax.Should().Be(300);
        }

        [Fact]
        public void SingleReading_ReturnsOnePoint()
        {
            var readings = new List<SensorGlucose>
            {
                new()
                {
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills).UtcDateTime,
                    Mgdl = 120.0,
                    Direction = GlucoseDirection.Flat,
                },
            };

            var (data, yMax) = ChartDataService.BuildGlucoseData(readings);

            data.Should().HaveCount(1);
            data[0].Time.Should().Be(TestMills);
            data[0].Sgv.Should().Be(120.0);
            data[0].Direction.Should().Be("Flat");
            // maxSgv=120, yMax = Min(400, Max(280, 120) + 20) = Min(400, 300) = 300
            yMax.Should().Be(300);
        }

        [Fact]
        public void NotComputableDirection_UsesLegacyWireSpelling()
        {
            var readings = new List<SensorGlucose>
            {
                new()
                {
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills).UtcDateTime,
                    Mgdl = 120.0,
                    Direction = GlucoseDirection.NotComputable,
                },
            };

            var (data, _) = ChartDataService.BuildGlucoseData(readings);

            data[0].Direction.Should().Be("NOT COMPUTABLE");
        }

        [Fact]
        public void AllReadingsIncluded()
        {
            var readings = new List<SensorGlucose>
            {
                new()
                {
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills).UtcDateTime,
                    Mgdl = 120.0,
                    Direction = GlucoseDirection.Flat,
                },
                new()
                {
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills + 2000).UtcDateTime,
                    Mgdl = 150.0,
                    Direction = GlucoseDirection.FortyFiveUp,
                },
            };

            var (data, _) = ChartDataService.BuildGlucoseData(readings);

            data.Should().HaveCount(2);
            data.Should().OnlyContain(g => g.Sgv > 0);
        }

        [Fact]
        public void OrdersByTime()
        {
            var readings = new List<SensorGlucose>
            {
                new() { Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills + 2000).UtcDateTime, Mgdl = 150.0 },
                new() { Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills).UtcDateTime, Mgdl = 120.0 },
                new() { Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills + 1000).UtcDateTime, Mgdl = 130.0 },
            };

            var (data, _) = ChartDataService.BuildGlucoseData(readings);

            data.Should().HaveCount(3);
            data[0].Time.Should().Be(TestMills);
            data[1].Time.Should().Be(TestMills + 1000);
            data[2].Time.Should().Be(TestMills + 2000);
        }

        [Fact]
        public void NullDirection_MapsToNullString()
        {
            var readings = new List<SensorGlucose>
            {
                new() { Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills).UtcDateTime, Mgdl = 120.0, Direction = null },
            };

            var (data, _) = ChartDataService.BuildGlucoseData(readings);

            data[0].Direction.Should().BeNull();
        }

        [Theory]
        [InlineData(100, 300)] // maxSgv=100 < 280, yMax = Min(400, 280+20) = 300
        [InlineData(280, 300)] // maxSgv=280 = 280, yMax = Min(400, 280+20) = 300
        [InlineData(300, 320)] // maxSgv=300 > 280, yMax = Min(400, 300+20) = 320
        [InlineData(390, 400)] // maxSgv=390, yMax = Min(400, 390+20) = 400
        [InlineData(500, 400)] // maxSgv=500, yMax = Min(400, 500+20) = 400
        public void YMaxCalculation_VariousMgdlValues(double mgdl, double expectedYMax)
        {
            var readings = new List<SensorGlucose>
            {
                new() { Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills).UtcDateTime, Mgdl = mgdl },
            };

            var (_, yMax) = ChartDataService.BuildGlucoseData(readings);

            yMax.Should().Be(expectedYMax);
        }
    }

    #endregion

    #region BuildBolusMarkers Tests

    public class BuildBolusMarkersTests
    {
        [Fact]
        public void EmptyList_ReturnsEmpty()
        {
            var result = ChartDataService.BuildBolusMarkers(new List<Bolus>());

            result.Should().BeEmpty();
        }

        [Fact]
        public void NormalBolus_MappedCorrectly()
        {
            var bolusId = Guid.NewGuid();
            var boluses = new List<Bolus>
            {
                new()
                {
                    Id = bolusId,
                    LegacyId = "treat-1",
                    Insulin = 5.0,
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills).UtcDateTime,
                    BolusType = Nocturne.Core.Models.V4.BolusType.Normal,
                    Automatic = false,
                },
            };

            var result = ChartDataService.BuildBolusMarkers(boluses);

            result.Should().HaveCount(1);
            result[0].Insulin.Should().Be(5.0);
            result[0].BolusType.Should().Be(Nocturne.Core.Models.BolusType.Bolus);
            result[0].Time.Should().Be(TestMills);
            result[0].TreatmentId.Should().Be("treat-1");
        }

        [Fact]
        public void AutomaticBolus_MappedToAutomaticBolusType()
        {
            var boluses = new List<Bolus>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    Insulin = 0.3,
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills).UtcDateTime,
                    Automatic = true,
                },
            };

            var result = ChartDataService.BuildBolusMarkers(boluses);

            result.Should().HaveCount(1);
            result[0].BolusType.Should().Be(Nocturne.Core.Models.BolusType.AutomaticBolus);
        }

        [Fact]
        public void SquareBolus_MappedToComboBolus()
        {
            var boluses = new List<Bolus>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    Insulin = 3.0,
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills).UtcDateTime,
                    BolusType = Nocturne.Core.Models.V4.BolusType.Square,
                    Automatic = false,
                },
            };

            var result = ChartDataService.BuildBolusMarkers(boluses);

            result.Should().HaveCount(1);
            result[0].BolusType.Should().Be(Nocturne.Core.Models.BolusType.ComboBolus);
        }

        [Fact]
        public void ZeroInsulin_NotIncluded()
        {
            var boluses = new List<Bolus>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    Insulin = 0.0,
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills).UtcDateTime,
                },
            };

            var result = ChartDataService.BuildBolusMarkers(boluses);

            result.Should().BeEmpty();
        }

        [Fact]
        public void NoLegacyId_UsesGuidId()
        {
            var bolusId = Guid.NewGuid();
            var boluses = new List<Bolus>
            {
                new()
                {
                    Id = bolusId,
                    LegacyId = null,
                    Insulin = 5.0,
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills).UtcDateTime,
                },
            };

            var result = ChartDataService.BuildBolusMarkers(boluses);

            result[0].TreatmentId.Should().Be(bolusId.ToString());
        }

        [Fact]
        public void IsOverride_AlwaysFalse()
        {
            var boluses = new List<Bolus>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    Insulin = 5.0,
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills).UtcDateTime,
                },
            };

            var result = ChartDataService.BuildBolusMarkers(boluses);

            result[0].IsOverride.Should().BeFalse();
        }
    }

    #endregion

    #region BuildCarbMarkers Tests

    public class BuildCarbMarkersTests
    {
        [Fact]
        public void EmptyList_ReturnsEmpty()
        {
            var result = ChartDataService.BuildCarbMarkers(new List<CarbIntake>(), null);

            result.Should().BeEmpty();
        }

        [Fact]
        public void CarbIntake_MappedCorrectly()
        {
            var carbId = Guid.NewGuid();
            var carbs = new List<CarbIntake>
            {
                new()
                {
                    Id = carbId,
                    LegacyId = "treat-1",
                    Carbs = 30.0,
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills).UtcDateTime,
                },
            };

            var result = ChartDataService.BuildCarbMarkers(carbs, TimeZoneInfo.Utc);

            result.Should().HaveCount(1);
            result[0].Carbs.Should().Be(30.0);
            result[0].MealPeriod.Should().NotBeNull();
            result[0].Time.Should().Be(TestMills);
            result[0].TreatmentId.Should().Be("treat-1");
            result[0].IsOffset.Should().BeFalse();
        }

        [Fact]
        public void WithoutFoodType_CarriesMealPeriod()
        {
            // 12:00 UTC = Lunch time
            var noonUtcMills = DateTimeOffset
                .Parse("2023-11-15T12:00:00Z")
                .ToUnixTimeMilliseconds();
            var carbs = new List<CarbIntake>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    Carbs = 20.0,
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(noonUtcMills).UtcDateTime,
                },
            };

            var result = ChartDataService.BuildCarbMarkers(carbs, TimeZoneInfo.Utc);

            result.Should().HaveCount(1);
            result[0].MealPeriod.Should().Be(MealPeriod.Lunch);
            result[0].Label.Should().BeNull();
        }

        [Fact]
        public void WithoutAZone_LeavesTheMealPeriodOut()
        {
            var noonUtcMills = DateTimeOffset
                .Parse("2023-11-15T12:00:00Z")
                .ToUnixTimeMilliseconds();
            var carbs = new List<CarbIntake>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    Carbs = 20.0,
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(noonUtcMills).UtcDateTime,
                },
            };

            var result = ChartDataService.BuildCarbMarkers(carbs, null);

            result.Should().ContainSingle().Which.MealPeriod.Should().BeNull();
        }

        [Fact]
        public void ZeroCarbs_NotIncluded()
        {
            var carbs = new List<CarbIntake>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    Carbs = 0.0,
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills).UtcDateTime,
                },
            };

            var result = ChartDataService.BuildCarbMarkers(carbs, null);

            result.Should().BeEmpty();
        }

        [Fact]
        public void NoLegacyId_UsesGuidId()
        {
            var carbId = Guid.NewGuid();
            var carbs = new List<CarbIntake>
            {
                new()
                {
                    Id = carbId,
                    LegacyId = null,
                    Carbs = 30.0,
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills).UtcDateTime,
                },
            };

            var result = ChartDataService.BuildCarbMarkers(carbs, null);

            result[0].TreatmentId.Should().Be(carbId.ToString());
        }
    }

    #endregion

    #region BuildDeviceEventMarkers Tests

    public class BuildDeviceEventMarkersTests
    {
        [Fact]
        public void EmptyList_ReturnsEmpty()
        {
            var result = ChartDataService.BuildDeviceEventMarkers(new List<DeviceEvent>());

            result.Should().BeEmpty();
        }

        [Fact]
        public void DeviceEvent_SiteChange_Categorized()
        {
            var deviceEvents = new List<DeviceEvent>
            {
                new()
                {
                    EventType = DeviceEventType.SiteChange,
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills).UtcDateTime,
                    Notes = "Left arm",
                },
            };

            var result = ChartDataService.BuildDeviceEventMarkers(deviceEvents);

            result.Should().HaveCount(1);
            result[0].EventType.Should().Be(DeviceEventType.SiteChange);
            result[0].Notes.Should().Be("Left arm");
            result[0].Color.Should().Be(ChartColor.InsulinBolus);
        }

        [Theory]
        [InlineData(Nocturne.Core.Constants.DataSources.SensorSessionInference, true)]
        [InlineData(Nocturne.Core.Constants.DataSources.XDrip, false)]
        [InlineData(null, false)]
        public void DeviceEvent_CarriesWhetherItWasInferred(string? dataSource, bool inferred)
        {
            var deviceEvents = new List<DeviceEvent>
            {
                new()
                {
                    EventType = DeviceEventType.SensorStart,
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills).UtcDateTime,
                    DataSource = dataSource,
                },
            };

            var result = ChartDataService.BuildDeviceEventMarkers(deviceEvents);

            result.Should().ContainSingle().Which.IsInferred.Should().Be(inferred);
        }

        [Fact]
        public void DeviceEvent_SensorStart_Categorized()
        {
            var deviceEvents = new List<DeviceEvent>
            {
                new() { EventType = DeviceEventType.SensorStart, Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills).UtcDateTime},
            };

            var result = ChartDataService.BuildDeviceEventMarkers(deviceEvents);

            result.Should().HaveCount(1);
            result[0].EventType.Should().Be(DeviceEventType.SensorStart);
            result[0].Color.Should().Be(ChartColor.GlucoseInRange);
        }

        [Fact]
        public void AllDeviceEventTypes_Categorized()
        {
            var deviceEvents = new List<DeviceEvent>
            {
                new() { EventType = DeviceEventType.SensorStart, Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills).UtcDateTime},
                new() { EventType = DeviceEventType.SensorChange, Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills + 1000).UtcDateTime},
                new() { EventType = DeviceEventType.SensorStop, Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills + 2000).UtcDateTime},
                new() { EventType = DeviceEventType.SiteChange, Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills + 3000).UtcDateTime},
                new() { EventType = DeviceEventType.InsulinChange, Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills + 4000).UtcDateTime},
                new() { EventType = DeviceEventType.PumpBatteryChange, Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills + 5000).UtcDateTime},
            };

            var result = ChartDataService.BuildDeviceEventMarkers(deviceEvents);

            result.Should().HaveCount(6);
            result[0].EventType.Should().Be(DeviceEventType.SensorStart);
            result[1].EventType.Should().Be(DeviceEventType.SensorChange);
            result[2].EventType.Should().Be(DeviceEventType.SensorStop);
            result[3].EventType.Should().Be(DeviceEventType.SiteChange);
            result[4].EventType.Should().Be(DeviceEventType.InsulinChange);
            result[5].EventType.Should().Be(DeviceEventType.PumpBatteryChange);
        }

        [Fact]
        public void NewDeviceEventTypes_Categorized()
        {
            var deviceEvents = new List<DeviceEvent>
            {
                new() { EventType = DeviceEventType.PodChange, Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills).UtcDateTime},
                new() { EventType = DeviceEventType.ReservoirChange, Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills + 1000).UtcDateTime},
                new() { EventType = DeviceEventType.CannulaChange, Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills + 2000).UtcDateTime},
                new() { EventType = DeviceEventType.TransmitterSensorInsert, Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills + 3000).UtcDateTime},
            };

            var result = ChartDataService.BuildDeviceEventMarkers(deviceEvents);

            result.Should().HaveCount(4);
            result[0].EventType.Should().Be(DeviceEventType.PodChange);
            result[0].Color.Should().Be(ChartColor.InsulinBolus);
            result[1].EventType.Should().Be(DeviceEventType.ReservoirChange);
            result[1].Color.Should().Be(ChartColor.InsulinBasal);
            result[2].EventType.Should().Be(DeviceEventType.CannulaChange);
            result[2].Color.Should().Be(ChartColor.InsulinBolus);
            result[3].EventType.Should().Be(DeviceEventType.TransmitterSensorInsert);
            result[3].Color.Should().Be(ChartColor.GlucoseInRange);
        }
    }

    #endregion

    #region BuildBgCheckMarkers Tests

    public class BuildBgCheckMarkersTests
    {
        [Fact]
        public void EmptyList_ReturnsEmpty()
        {
            var result = ChartDataService.BuildBgCheckMarkers(new List<BGCheck>());

            result.Should().BeEmpty();
        }

        [Fact]
        public void BgCheck_MappedCorrectly()
        {
            var checkId = Guid.NewGuid();
            var checks = new List<BGCheck>
            {
                new()
                {
                    Id = checkId,
                    LegacyId = "treat-bg-1",
                    Glucose = 120.0,
                    Units = GlucoseUnit.MgDl,
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills).UtcDateTime,
                    GlucoseType = GlucoseType.Finger,
                },
            };

            var result = ChartDataService.BuildBgCheckMarkers(checks);

            result.Should().HaveCount(1);
            result[0].Glucose.Should().Be(120.0);
            result[0].GlucoseType.Should().Be("Finger");
            result[0].Time.Should().Be(TestMills);
            result[0].TreatmentId.Should().Be("treat-bg-1");
        }

        [Fact]
        public void ZeroMgdl_NotIncluded()
        {
            var checks = new List<BGCheck>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    Glucose = 0.0,
                    Units = GlucoseUnit.MgDl,
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills).UtcDateTime,
                },
            };

            var result = ChartDataService.BuildBgCheckMarkers(checks);

            result.Should().BeEmpty();
        }

        [Fact]
        public void NoLegacyId_UsesGuidId()
        {
            var checkId = Guid.NewGuid();
            var checks = new List<BGCheck>
            {
                new()
                {
                    Id = checkId,
                    LegacyId = null,
                    Glucose = 95.0,
                    Units = GlucoseUnit.MgDl,
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills).UtcDateTime,
                },
            };

            var result = ChartDataService.BuildBgCheckMarkers(checks);

            result[0].TreatmentId.Should().Be(checkId.ToString());
        }

        [Fact]
        public void NullGlucoseType_MapsToNull()
        {
            var checks = new List<BGCheck>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    Glucose = 110.0,
                    Units = GlucoseUnit.MgDl,
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills).UtcDateTime,
                    GlucoseType = null,
                },
            };

            var result = ChartDataService.BuildBgCheckMarkers(checks);

            result[0].GlucoseType.Should().BeNull();
        }
    }

    #endregion

    #region MapV4BolusType Tests

    public class MapV4BolusTypeTests
    {
        [Fact]
        public void Automatic_ReturnsAutomaticBolus()
        {
            var result = ChartDataService.MapV4BolusType(Nocturne.Core.Models.V4.BolusType.Normal, true);

            result.Should().Be(Nocturne.Core.Models.BolusType.AutomaticBolus);
        }

        [Fact]
        public void Normal_ReturnsBolus()
        {
            var result = ChartDataService.MapV4BolusType(Nocturne.Core.Models.V4.BolusType.Normal, false);

            result.Should().Be(Nocturne.Core.Models.BolusType.Bolus);
        }

        [Fact]
        public void Square_ReturnsComboBolus()
        {
            var result = ChartDataService.MapV4BolusType(Nocturne.Core.Models.V4.BolusType.Square, false);

            result.Should().Be(Nocturne.Core.Models.BolusType.ComboBolus);
        }

        [Fact]
        public void Dual_ReturnsComboBolus()
        {
            var result = ChartDataService.MapV4BolusType(Nocturne.Core.Models.V4.BolusType.Dual, false);

            result.Should().Be(Nocturne.Core.Models.BolusType.ComboBolus);
        }

        [Fact]
        public void Null_ReturnsBolus()
        {
            var result = ChartDataService.MapV4BolusType(null, false);

            result.Should().Be(Nocturne.Core.Models.BolusType.Bolus);
        }

        [Fact]
        public void AutomaticOverridesBolusType()
        {
            // Even if it's a Square bolus, Automatic flag takes precedence
            var result = ChartDataService.MapV4BolusType(Nocturne.Core.Models.V4.BolusType.Square, true);

            result.Should().Be(Nocturne.Core.Models.BolusType.AutomaticBolus);
        }
    }

    #endregion

    #region GetMealPeriod Tests

    public class GetMealPeriodTests
    {
        private static long MillsAtUtcHour(int hourUtc) =>
            new DateTimeOffset(2023, 11, 15, hourUtc, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        [Theory]
        [InlineData(5, MealPeriod.Breakfast)]
        [InlineData(10, MealPeriod.Breakfast)]
        [InlineData(11, MealPeriod.Lunch)]
        [InlineData(14, MealPeriod.Lunch)]
        [InlineData(15, MealPeriod.Snack)]
        [InlineData(16, MealPeriod.Snack)]
        [InlineData(17, MealPeriod.Dinner)]
        [InlineData(20, MealPeriod.Dinner)]
        [InlineData(21, MealPeriod.LateNight)]
        [InlineData(23, MealPeriod.LateNight)]
        [InlineData(0, MealPeriod.LateNight)]
        [InlineData(4, MealPeriod.LateNight)]
        public void UtcZoneBuckets(int hourUtc, MealPeriod expected)
        {
            ChartDataService.GetMealPeriod(MillsAtUtcHour(hourUtc), TimeZoneInfo.Utc)
                .Should().Be(expected);
        }

        [Fact]
        public void NoZone_GivesNoPeriod()
        {
            ChartDataService.GetMealPeriod(MillsAtUtcHour(12), null).Should().BeNull();
        }

        [Fact]
        public void ZoneConversion_NewYork_LateNight()
        {
            var zone = TimeZoneHelper.GetTimeZoneInfoFromId("America/New_York");

            ChartDataService.GetMealPeriod(MillsAtUtcHour(6), zone).Should().Be(MealPeriod.LateNight);
        }

        [Fact]
        public void ZoneConversion_NewYork_Breakfast()
        {
            var zone = TimeZoneHelper.GetTimeZoneInfoFromId("America/New_York");

            ChartDataService.GetMealPeriod(MillsAtUtcHour(12), zone).Should().Be(MealPeriod.Breakfast);
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
                CreatedBy = "user1",
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
                CreatedBy = "user1",
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
                CreatedBy = "user1",
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
                CreatedBy = "user1",
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
                CreatedBy = "user1",
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
                CreatedBy = "user1",
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
                CreatedBy = "user1",
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
                CreatedBy = "user1",
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
        [InlineData(DeviceEventType.PodChange, ChartColor.InsulinBolus)]
        [InlineData(DeviceEventType.ReservoirChange, ChartColor.InsulinBasal)]
        [InlineData(DeviceEventType.CannulaChange, ChartColor.InsulinBolus)]
        [InlineData(DeviceEventType.TransmitterSensorInsert, ChartColor.GlucoseInRange)]
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
        [InlineData(BasalDeliveryOrigin.Scheduled, ChartColor.InsulinBasal)]
        [InlineData(BasalDeliveryOrigin.Algorithm, ChartColor.InsulinTempBasal)]
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

    #region LastPumpSyncMills Tests

    public class LastPumpSyncMillsTests
    {
        private const long Minute = 60 * 1000;
        private const long Now = TestMills + 10 * 60 * Minute;

        private static TempBasal Basal(long startMills, long? endMills) =>
            new()
            {
                StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(startMills).UtcDateTime,
                EndTimestamp = endMills.HasValue
                    ? DateTimeOffset.FromUnixTimeMilliseconds(endMills.Value).UtcDateTime
                    : null,
                Rate = 1.0,
                Origin = TempBasalOrigin.Algorithm,
            };

        private static PumpSnapshot Pump(long uploadedMills, string? clock) =>
            new()
            {
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(uploadedMills).UtcDateTime,
                Clock = clock,
            };

        private static string Iso(long mills) =>
            DateTimeOffset.FromUnixTimeMilliseconds(mills).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

        [Fact]
        public void APumpOnItsScheduledBasalDatesFromItsLastStatusNotItsLastTempBasal()
        {
            var lastTemp = Basal(Now - 120 * Minute, Now - 90 * Minute);
            var status = Pump(Now - 2 * Minute, Iso(Now - 3 * Minute));

            ChartDataService.LastPumpSyncMills([lastTemp], status).Should().Be(Now - 3 * Minute);
        }

        [Fact]
        public void APumpClockWithAnOffsetIsReadAsTheInstantItNames()
        {
            var status = Pump(Now, "2023-11-14T22:13:20+10:00");

            ChartDataService.LastPumpSyncMills([], status)
                .Should().Be(DateTimeOffset.Parse("2023-11-14T12:13:20Z").ToUnixTimeMilliseconds());
        }

        [Fact]
        public void AStatusWithoutAPumpClockDatesFromItsUpload()
        {
            ChartDataService.LastPumpSyncMills([], Pump(Now - Minute, null)).Should().Be(Now - Minute);
        }

        [Fact]
        public void APumpThatStoppedReportingDatesFromItsLastClock()
        {
            var running = Basal(Now - 200 * Minute, Now - 170 * Minute);
            var status = Pump(Now - 2 * Minute, Iso(Now - 180 * Minute));

            ChartDataService.LastPumpSyncMills([running], status).Should().Be(Now - 170 * Minute);
        }

        [Fact]
        public void WithoutPumpStatusTheNewestTempBasalEndDatesIt()
        {
            var earlier = Basal(Now - 60 * Minute, Now - 30 * Minute);
            var open = Basal(Now - 20 * Minute, null);

            ChartDataService.LastPumpSyncMills([earlier, open], null).Should().Be(Now - 20 * Minute);
        }

        [Fact]
        public void NothingToDateItFrom_IsNull()
        {
            ChartDataService.LastPumpSyncMills([], null).Should().BeNull();
        }
    }

    #endregion

    #region SelectDisplayTempBasals Tests

    public class SelectDisplayTempBasalsTests
    {
        private const long Start = TestMills;
        private const long End = TestMills + 6 * 60 * 60 * 1000;
        private const long Minute = 60 * 1000;

        private static TempBasal Basal(long startMills, long? endMills) =>
            new()
            {
                StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(startMills).UtcDateTime,
                EndTimestamp = endMills.HasValue
                    ? DateTimeOffset.FromUnixTimeMilliseconds(endMills.Value).UtcDateTime
                    : null,
                Rate = 1.0,
                Origin = TempBasalOrigin.Algorithm,
            };

        [Fact]
        public void StartedBeforeWindowAndEndedInside_IsSelected()
        {
            var running = Basal(Start - 20 * Minute, Start + 10 * Minute);

            Spans(ChartDataService.SelectDisplayTempBasals([running], Start, End, End)).Should().Equal(Span(running));
        }

        [Fact]
        public void EndedBeforeWindow_IsNotSelected()
        {
            var ended = Basal(Start - 60 * Minute, Start - 10 * Minute);

            ChartDataService.SelectDisplayTempBasals([ended], Start, End, End).Should().BeEmpty();
        }

        [Fact]
        public void StartedAfterWindow_IsNotSelected()
        {
            var later = Basal(End + Minute, End + 30 * Minute);

            ChartDataService.SelectDisplayTempBasals([later], Start, End, End).Should().BeEmpty();
        }

        [Fact]
        public void OpenEndedBeforeWindow_RunsUntilTheNextRow()
        {
            var running = Basal(Start - 3 * 60 * Minute, null);
            var later = Basal(Start + 30 * Minute, Start + 60 * Minute);

            Spans(ChartDataService.SelectDisplayTempBasals([running, later], Start, End, End))
                .Should().Equal((Start - 3 * 60 * Minute, Start + 30 * Minute), Span(later));
        }

        [Fact]
        public void DurationZeroCancelBeforeWindow_IsNotSelected()
        {
            var cancel = Cancel(Start - 4 * 60 * Minute);

            ChartDataService.SelectDisplayTempBasals([cancel], Start, End, End).Should().BeEmpty();
        }

        [Fact]
        public void EndingExactlyAtWindowStart_IsNotSelected()
        {
            var touching = Basal(Start - 30 * Minute, Start);
            var overlapping = Basal(Start - 30 * Minute, Start + Minute);

            Spans(ChartDataService.SelectDisplayTempBasals([touching, overlapping], Start, End, End)).Should().Equal(Span(overlapping));
        }

        [Fact]
        public void OpenEndedInsideWindow_RunsToTheWindowEnd()
        {
            var open = Basal(Start + 30 * Minute, null);

            Spans(ChartDataService.SelectDisplayTempBasals([open], Start, End, End))
                .Should().Equal((Start + 30 * Minute, End));
        }

        [Fact]
        public void OpenEndedInsideAWindowEndingAfterNow_RunsToNow()
        {
            var now = Start + 90 * Minute;
            var open = Basal(Start + 30 * Minute, null);

            Spans(ChartDataService.SelectDisplayTempBasals([open], Start, End, now))
                .Should().Equal((Start + 30 * Minute, now));
        }

        [Fact]
        public void CancelledJustBeforeWindow_IsNotDrawnIntoIt()
        {
            var cancelled = Basal(Start - 10 * Minute, Start + 20 * Minute);
            cancelled.Rate = 2.0;
            var cancel = Cancel(Start - 3 * Minute);

            ChartDataService.SelectDisplayTempBasals([cancelled, cancel], Start, End, End)
                .Should().NotContain(tb => tb.Rate == 2.0);
        }

        [Fact]
        public void AV1Cancel_EndsTheTempAndDrawsNoDeliveryOfItsOwn()
        {
            var temp = Basal(Start, Start + 30 * Minute);
            temp.Rate = 2.0;
            var cancel = Nocturne.API.Services.V4.TreatmentDecomposer.MapToTempBasal(
                new Treatment { EventType = "Temp Basal", Mills = Start + 10 * Minute, Duration = 0, Absolute = 0 },
                correlationId: null);

            var spans = ChartDataService.MapBasalDeliverySpans(
                ChartDataService.SelectDisplayTempBasals([temp, cancel], Start, End, End));

            spans.Select(s => (s.StartMills, s.EndMills, s.Rate)).Should().Equal((Start, Start + 10 * Minute, 2.0));
        }

        [Fact]
        public void MirrorOverlappingTheLoop_ShowsTheLoopWhereItRuns()
        {
            var loop = Basal(Start, Start + 30 * Minute);
            loop.Device = "openaps://Trio";
            var mirror = Basal(Start + 10 * Minute, Start + 40 * Minute);
            mirror.Device = "tidepool";

            ChartDataService.SelectDisplayTempBasals([mirror, loop], Start, End, End)
                .Select(tb => (tb.Device, tb.StartMills, tb.EndMills!.Value))
                .Should().Equal(
                    ("openaps://Trio", Start, Start + 30 * Minute),
                    ("tidepool", Start + 30 * Minute, Start + 40 * Minute));
        }

        private static TempBasal Cancel(long mills)
        {
            var cancel = Basal(mills, mills);
            cancel.Rate = 0;
            return cancel;
        }

        private static IEnumerable<(long, long)> Spans(IEnumerable<TempBasal> selected) =>
            selected.Select(tb => (tb.StartMills, tb.EndMills!.Value));

        private static (long, long) Span(TempBasal tb) => (tb.StartMills, tb.EndMills!.Value);
    }

    #endregion

    #region GetBasalSeriesAsync Tests

    public class GetBasalSeriesAsyncTests
    {
        private const long Start = TestMills;
        private const long End = TestMills + 60 * 60 * 1000;
        private const long Minute = 60 * 1000;

        [Fact]
        public async Task ReadsFromBufferAndDrawsRunningRowClippedToWindowStart()
        {
            var running = new TempBasal
            {
                StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(Start - 20 * Minute).UtcDateTime,
                EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(Start + 10 * Minute).UtcDateTime,
                Rate = 0.4,
                Origin = TempBasalOrigin.Algorithm,
            };
            var cancel = new TempBasal
            {
                StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(Start - 120 * Minute).UtcDateTime,
                EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(Start - 120 * Minute).UtcDateTime,
                Rate = 0,
                Origin = TempBasalOrigin.Algorithm,
            };
            var repository = new Mock<ITempBasalRepository>();
            DateTime? requestedFrom = null;
            repository
                .Setup(r => r.GetAsync(
                    It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                    It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .Callback<DateTime?, DateTime?, string?, string?, int, int, bool, CancellationToken>(
                    (from, _, _, _, _, _, _, _) => requestedFrom = from)
                .ReturnsAsync([cancel, running]);

            var series = await Service(repository.Object, TimeProvider.System).GetBasalSeriesAsync(Start, End);

            requestedFrom.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(Start - DailyProfileCalculator.ActingMs).UtcDateTime);
            series.Should().ContainSingle();
            series[0].Timestamp.Should().Be(Start);
            series[0].Rate.Should().BeApproximately(0.4, 0.001);
        }

        [Fact]
        public async Task WindowEndingAfterNow_StopsARunningTempAtNow()
        {
            var now = Start + 30 * Minute;
            var running = new TempBasal
            {
                StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(Start + 10 * Minute).UtcDateTime,
                Rate = 2.0,
                Origin = TempBasalOrigin.Algorithm,
            };
            var repository = new Mock<ITempBasalRepository>();
            repository
                .Setup(r => r.GetAsync(
                    It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                    It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([running]);

            var basalRate = new Mock<IBasalRateResolver>();
            basalRate
                .Setup(r => r.GetBasalRateAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(0.8);
            var segments = new Mock<IBasalSegmentService>();
            segments
                .Setup(s => s.GetSegmentsAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .Returns(AsyncEnumerable.Empty<BasalSegment>());

            var series = await Service(
                    repository.Object,
                    new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(now)),
                    hasTherapySettings: true,
                    basalRate.Object,
                    segments.Object)
                .GetBasalSeriesAsync(Start, End);

            series.Select(p => (p.Timestamp, p.Rate)).Should().Equal(
                (Start, 0.8),
                (Start + 10 * Minute, 2.0),
                (now, 0.8));
        }

        [Fact]
        public async Task ReadsTheOverridesOverTheWindow_AndScalesTheScheduleByThem()
        {
            var repository = new Mock<ITempBasalRepository>();
            repository
                .Setup(r => r.GetAsync(
                    It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                    It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            var segments = new Mock<IBasalSegmentService>();
            segments
                .Setup(s => s.GetSegmentsAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .Returns(new[] { new BasalSegment(Start, End, 1.0, 1.0, "Default") }.ToAsyncEnumerable());
            var stateSpans = new Mock<IStateSpanRepository>();
            stateSpans
                .Setup(r => r.GetByCategories(
                    It.IsAny<IEnumerable<StateSpanCategory>>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Dictionary<StateSpanCategory, List<StateSpan>>
                {
                    [StateSpanCategory.Override] =
                    [
                        new StateSpan
                        {
                            Category = StateSpanCategory.Override,
                            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(Start + 10 * Minute).UtcDateTime,
                            EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(Start + 30 * Minute).UtcDateTime,
                            Metadata = new Dictionary<string, object> { ["insulinNeedsScaleFactor"] = 0.5 },
                        },
                    ],
                });
            using var cts = new CancellationTokenSource();

            var series = await Service(
                    repository.Object, TimeProvider.System, hasTherapySettings: true,
                    segments: segments.Object, stateSpans: stateSpans.Object)
                .GetBasalSeriesAsync(Start, End, cts.Token);

            series.Select(p => (p.Timestamp, p.Rate)).Should().Equal(
                (Start, 1.0), (Start + 10 * Minute, 0.5), (Start + 30 * Minute, 1.0));
            stateSpans.Verify(
                r => r.GetByCategories(
                    It.Is<IEnumerable<StateSpanCategory>>(c => c.SequenceEqual(new[] { StateSpanCategory.Override })),
                    DateTimeOffset.FromUnixTimeMilliseconds(Start).UtcDateTime,
                    DateTimeOffset.FromUnixTimeMilliseconds(End).UtcDateTime,
                    cts.Token),
                Times.Once);
        }

        private static ChartDataService Service(
            ITempBasalRepository repository,
            TimeProvider timeProvider,
            bool hasTherapySettings = false,
            IBasalRateResolver? basalRate = null,
            IBasalSegmentService? segments = null,
            IStateSpanRepository? stateSpans = null)
        {
            var settings = new Mock<ITherapySettingsResolver>();
            settings.Setup(s => s.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(hasTherapySettings);
            return new ChartDataService(
                [],
                Mock.Of<IChartDataAssembler>(),
                new BasalSeriesBuilder(
                    settings.Object, segments ?? Mock.Of<IBasalSegmentService>(), NullLogger<BasalSeriesBuilder>.Instance),
                repository,
                stateSpans ?? NoOverrides(),
                settings.Object,
                basalRate ?? Mock.Of<IBasalRateResolver>(),
                timeProvider,
                NullLogger<ChartDataService>.Instance);
        }
    }

    #endregion

    #region GetBasalDeliverySpansAsync Tests

    public class GetBasalDeliverySpansAsyncTests
    {
        private const long Start = TestMills;
        private const long End = TestMills + 60 * 60 * 1000;
        private const long Minute = 60 * 1000;

        private static TempBasal Span(long startMills, long endMills, TempBasalOrigin origin, double rate) => new()
        {
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(startMills).UtcDateTime,
            EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(endMills).UtcDateTime,
            Rate = rate,
            Origin = origin,
        };

        [Fact]
        public async Task ReadsFromBufferAndReturnsEveryOriginWithSuspendedAtZero()
        {
            var rows = new[]
            {
                Span(Start - 20 * Minute, Start + 10 * Minute, TempBasalOrigin.Manual, 0.4),
                Span(Start + 10 * Minute, Start + 20 * Minute, TempBasalOrigin.Suspended, 0.9),
                Span(Start + 20 * Minute, Start + 30 * Minute, TempBasalOrigin.Inferred, 1.1),
                Span(Start - 5 * 60 * Minute, Start - 4 * 60 * Minute, TempBasalOrigin.Algorithm, 2.0),
            };
            var repository = new Mock<ITempBasalRepository>();
            DateTime? requestedFrom = null;
            repository
                .Setup(r => r.GetAsync(
                    It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                    It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .Callback<DateTime?, DateTime?, string?, string?, int, int, bool, CancellationToken>(
                    (from, _, _, _, _, _, _, _) => requestedFrom = from)
                .ReturnsAsync(rows);

            var service = new ChartDataService(
                [],
                Mock.Of<IChartDataAssembler>(),
                new BasalSeriesBuilder(Mock.Of<ITherapySettingsResolver>(), Mock.Of<IBasalSegmentService>(), NullLogger<BasalSeriesBuilder>.Instance),
                repository.Object,
                NoOverrides(),
                Mock.Of<ITherapySettingsResolver>(),
                Mock.Of<IBasalRateResolver>(),
                new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(End)),
                NullLogger<ChartDataService>.Instance);

            var spans = await service.GetBasalDeliverySpansAsync(Start, End);

            requestedFrom.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(Start - DailyProfileCalculator.ActingMs).UtcDateTime);
            spans.Select(s => (s.Origin, s.Rate)).Should().Equal(
                (BasalDeliveryOrigin.Manual, 0.4),
                (BasalDeliveryOrigin.Suspended, 0.0),
                (BasalDeliveryOrigin.Inferred, 1.1));
            spans[0].StartMills.Should().Be(Start - 20 * Minute);
        }
    }

    #endregion

    public class MapTempBasalSpansTests
    {
        [Theory]
        [InlineData(TempBasalOrigin.Manual, 1)]
        [InlineData(TempBasalOrigin.Algorithm, 0)]
        public void Only_a_manual_temp_draws_a_temp_basal_band(TempBasalOrigin origin, int bands)
        {
            var tempBasal = new TempBasal
            {
                Id = Guid.CreateVersion7(),
                StartTimestamp = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc),
                EndTimestamp = new DateTime(2026, 8, 1, 12, 30, 0, DateTimeKind.Utc),
                Rate = 1.2,
                Origin = origin,
            };

            ChartDataService.MapTempBasalSpans([tempBasal]).Should().HaveCount(bands);
        }

        [Fact]
        public void A_split_records_later_piece_has_the_same_id_in_every_window_that_holds_it()
        {
            const long Start = TestMills;
            const long Minute = 60 * 1000;
            var manual = new TempBasal
            {
                Id = Guid.CreateVersion7(),
                StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(Start).UtcDateTime,
                EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(Start + 60 * Minute).UtcDateTime,
                Rate = 1.2,
                Origin = TempBasalOrigin.Manual,
                Device = "pump",
            };
            var loop = new TempBasal
            {
                Id = Guid.CreateVersion7(),
                StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(Start + 10 * Minute).UtcDateTime,
                EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(Start + 20 * Minute).UtcDateTime,
                Rate = 0.5,
                Origin = TempBasalOrigin.Algorithm,
                Device = "openaps://Trio",
            };
            var records = new List<TempBasal> { manual, loop };

            var wide = ChartDataService.MapTempBasalSpans(
                ChartDataService.SelectDisplayTempBasals(records, Start - Minute, Start + 90 * Minute, Start + 90 * Minute));
            var late = ChartDataService.MapTempBasalSpans(
                ChartDataService.SelectDisplayTempBasals(records, Start + 30 * Minute, Start + 90 * Minute, Start + 90 * Minute));

            wide.Select(s => s.Id).Should().Equal(manual.Id.ToString(), $"{manual.Id}:{Start + 20 * Minute}");
            late.Select(s => s.Id).Should().Equal($"{manual.Id}:{Start + 20 * Minute}");
        }
    }
}
