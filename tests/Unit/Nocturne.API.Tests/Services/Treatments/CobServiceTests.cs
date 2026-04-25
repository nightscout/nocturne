using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Oref;
using OrefModels = Nocturne.Core.Oref.Models;
using Xunit;

namespace Nocturne.API.Tests.Services.Treatments;

/// <summary>
/// Complete COB calculation tests with 1:1 legacy JavaScript compatibility
/// Tests exact algorithms from ClientApp/mocha-tests/cob.test.js
/// NO SIMPLIFICATIONS - Must match legacy behavior exactly
/// </summary>
[Parity("cob.test.js")]
public class CobServiceTests
{
    private readonly ICobService _cobService;
    private readonly TestCobProfile _testProfile;

    public CobServiceTests()
    {
        var logger = new Mock<ILogger<Nocturne.API.Services.Treatments.CobService>>();
        IIobService iobService = OrefService.IsAvailable() ? new OrefIobAdapter() : CreateDefaultIobService();
        _cobService = new Nocturne.API.Services.Treatments.CobService(logger.Object, iobService);
        _testProfile = new TestCobProfile();
    }

    [Fact]
    public void CobTotal_MultipleTreatments_ShouldMatchLegacyResults()
    {
        // Arrange - Exact test case from legacy cob.test.js: "should calculate IOB, multiple treatments"
        var treatments = new List<Treatment>
        {
            new()
            {
                Carbs = 100,
                Mills = new DateTimeOffset(
                    2015,
                    5,
                    29,
                    2,
                    3,
                    48,
                    827,
                    TimeSpan.Zero
                ).ToUnixTimeMilliseconds(),
            },
            new()
            {
                Carbs = 10,
                Mills = new DateTimeOffset(
                    2015,
                    5,
                    29,
                    3,
                    45,
                    10,
                    670,
                    TimeSpan.Zero
                ).ToUnixTimeMilliseconds(),
            },
        };

        var after100Time = new DateTimeOffset(
            2015,
            5,
            29,
            2,
            3,
            49,
            827,
            TimeSpan.Zero
        ).ToUnixTimeMilliseconds();
        var before10Time = new DateTimeOffset(
            2015,
            5,
            29,
            3,
            45,
            10,
            670,
            TimeSpan.Zero
        ).ToUnixTimeMilliseconds();
        var after10Time = new DateTimeOffset(
            2015,
            5,
            29,
            3,
            45,
            11,
            670,
            TimeSpan.Zero
        ).ToUnixTimeMilliseconds();

        // Act
        var after100 = _cobService.CobTotal(
            treatments,
            new List<DeviceStatus>(),
            _testProfile,
            after100Time
        );
        var before10 = _cobService.CobTotal(
            treatments,
            new List<DeviceStatus>(),
            _testProfile,
            before10Time
        );
        var after10 = _cobService.CobTotal(
            treatments,
            new List<DeviceStatus>(),
            _testProfile,
            after10Time
        );

        // Assert - Exact legacy expectations
        Assert.Equal(100.0, after100.Cob, 1);
        Assert.Equal(59.0, Math.Round(before10.Cob), 0); // Legacy: Math.round(before10.cob).should.equal(59);
        Assert.Equal(69.0, Math.Round(after10.Cob), 0); // Legacy: Math.round(after10.cob).should.equal(69);
    }

    [Fact]
    public void CobTotal_SingleTreatment_ShouldFollowAbsorptionCurve()
    {
        // Arrange - Exact test case from legacy: "should calculate IOB, single treatment"
        var treatment = new Treatment
        {
            Carbs = 8,
            Mills = new DateTimeOffset(
                2015,
                5,
                29,
                4,
                40,
                40,
                174,
                TimeSpan.Zero
            ).ToUnixTimeMilliseconds(),
        };
        var treatments = new List<Treatment> { treatment };

        var rightAfterTime = new DateTimeOffset(
            2015,
            5,
            29,
            4,
            41,
            40,
            174,
            TimeSpan.Zero
        ).ToUnixTimeMilliseconds();
        var later1Time = new DateTimeOffset(
            2015,
            5,
            29,
            5,
            4,
            40,
            174,
            TimeSpan.Zero
        ).ToUnixTimeMilliseconds();
        var later2Time = new DateTimeOffset(
            2015,
            5,
            29,
            5,
            20,
            0,
            174,
            TimeSpan.Zero
        ).ToUnixTimeMilliseconds();
        var later3Time = new DateTimeOffset(
            2015,
            5,
            29,
            5,
            50,
            0,
            174,
            TimeSpan.Zero
        ).ToUnixTimeMilliseconds();
        var later4Time = new DateTimeOffset(
            2015,
            5,
            29,
            6,
            50,
            0,
            174,
            TimeSpan.Zero
        ).ToUnixTimeMilliseconds();

        // Act
        var result1 = _cobService.CobTotal(
            treatments,
            new List<DeviceStatus>(),
            _testProfile,
            rightAfterTime
        );
        var result2 = _cobService.CobTotal(
            treatments,
            new List<DeviceStatus>(),
            _testProfile,
            later1Time
        );
        var result3 = _cobService.CobTotal(
            treatments,
            new List<DeviceStatus>(),
            _testProfile,
            later2Time
        );
        var result4 = _cobService.CobTotal(
            treatments,
            new List<DeviceStatus>(),
            _testProfile,
            later3Time
        );
        var result5 = _cobService.CobTotal(
            treatments,
            new List<DeviceStatus>(),
            _testProfile,
            later4Time
        );

        // Assert - Exact legacy expectations
        Assert.Equal(8.0, result1.Cob, 1); // Right after: full carbs
        Assert.Equal(6.0, result2.Cob, 1); // 24 minutes later: some absorption
        Assert.Equal(0.0, result3.Cob, 1); // 40 minutes later: mostly absorbed
        Assert.Equal(0.0, result4.Cob, 1); // 70 minutes later: fully absorbed
        Assert.Equal(0.0, result5.Cob, 1); // 130 minutes later: fully absorbed
    }

    [Fact]
    public void CalcTreatment_NoCarbs_ShouldReturnZero()
    {
        // Arrange
        var treatment = new Treatment
        {
            Insulin = 1.0, // No carbs
            Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        // Act
        var result = _cobService.CalcTreatment(
            treatment,
            _testProfile,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        );

        // Assert
        Assert.Equal(0.0, result.CobContrib);
        Assert.Equal(0.0, result.ActivityContrib);
    }

    [Fact]
    public void CalcTreatment_LinearAbsorption_ShouldDecreaseOverTime()
    {
        // Arrange
        var startTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatment = new Treatment
        {
            Carbs = 60, // 60g carbs
            Mills = startTime,
        };

        // Act - Test at different time intervals
        var rightAfter = _cobService.CalcTreatment(treatment, _testProfile, startTime + 1000); // 1 second later
        var after30Min = _cobService.CalcTreatment(
            treatment,
            _testProfile,
            startTime + 30 * 60 * 1000
        ); // 30 minutes later
        var after60Min = _cobService.CalcTreatment(
            treatment,
            _testProfile,
            startTime + 60 * 60 * 1000
        ); // 60 minutes later
        var after120Min = _cobService.CalcTreatment(
            treatment,
            _testProfile,
            startTime + 120 * 60 * 1000
        ); // 120 minutes later

        // Assert - COB should decrease over time
        Assert.True(rightAfter.CobContrib > after30Min.CobContrib);
        Assert.True(after30Min.CobContrib > after60Min.CobContrib);
        Assert.True(after60Min.CobContrib > after120Min.CobContrib);

        // Should approach zero but not be negative
        Assert.True(after120Min.CobContrib >= 0);
    }

    [Fact]
    public void CalcTreatment_WithCustomAbsorptionTime_ShouldUseCustomTime()
    {
        // Arrange
        var startTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var fastTreatment = new Treatment
        {
            Carbs = 30,
            Mills = startTime,
            AbsorptionTime = 60, // 60 minutes custom absorption
        };
        var slowTreatment = new Treatment
        {
            Carbs = 30,
            Mills = startTime,
            AbsorptionTime = 240, // 240 minutes custom absorption
        };

        var testTime = startTime + 90 * 60 * 1000; // 90 minutes later

        // Act
        var fastResult = _cobService.CalcTreatment(fastTreatment, _testProfile, testTime);
        var slowResult = _cobService.CalcTreatment(slowTreatment, _testProfile, testTime);

        // Assert - Slow absorption should have more COB remaining
        Assert.True(slowResult.CobContrib > fastResult.CobContrib);
        Assert.Equal(0.0, fastResult.CobContrib, 1); // Fast should be fully absorbed by 90 minutes
    }

    [Fact]
    public void FromDeviceStatus_LoopCOB_ShouldParseProperly()
    {
        // Arrange
        var deviceStatus = new DeviceStatus
        {
            Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Device = "loop://iPhone",
            Loop = new LoopStatus
            {
                Cob = new LoopCob { Cob = 25.5, Timestamp = DateTime.UtcNow.ToString("o") },
            },
        };

        // Act
        var result = _cobService.FromDeviceStatus(deviceStatus);

        // Assert
        Assert.Equal(25.5, result.Cob);
        Assert.Equal("Loop", result.Source);
        Assert.Equal("loop://iPhone", result.Device);
    }

    [Fact]
    public void FromDeviceStatus_OpenAPSCOB_ShouldParseProperly()
    {
        // Arrange
        var deviceStatus = new DeviceStatus
        {
            Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Device = "openaps://pi1",
            OpenAps = new OpenApsStatus { Cob = 42.0 },
        };

        // Act
        var result = _cobService.FromDeviceStatus(deviceStatus);

        // Assert
        Assert.Equal(42.0, result.Cob);
        Assert.Equal("OpenAPS", result.Source);
        Assert.Equal("openaps://pi1", result.Device);
    }

    [Fact]
    public void CobTotal_PrioritizesDeviceStatusOverTreatments()
    {
        // Arrange
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>
        {
            new() { Carbs = 30, Mills = time - 30 * 60 * 1000 }, // Should have some COB
        };
        var deviceStatuses = new List<DeviceStatus>
        {
            new()
            {
                Mills = time - 5 * 60 * 1000, // Recent
                Device = "loop://iPhone",
                Loop = new LoopStatus { Cob = new LoopCob { Cob = 15.0 } },
            },
        };

        // Act
        var result = _cobService.CobTotal(treatments, deviceStatuses, _testProfile, time);

        // Assert - Should prefer device status COB
        Assert.Equal(15.0, result.Cob);
        Assert.Equal("Loop", result.Source);
    }

    [Fact]
    public void CobTotal_FallsBackToTreatments_WhenDeviceStatusStale()
    {
        // Arrange
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>
        {
            new() { Carbs = 30, Mills = time - 30 * 60 * 1000 },
        };
        var deviceStatuses = new List<DeviceStatus>
        {
            new()
            {
                Mills = time - 35 * 60 * 1000, // Stale (older than 30min threshold)
                Device = "loop://iPhone",
                Loop = new LoopStatus { Cob = new LoopCob { Cob = 15.0 } },
            },
        };

        // Act
        var result = _cobService.CobTotal(treatments, deviceStatuses, _testProfile, time);

        // Assert - Should fall back to treatment-based COB
        Assert.True(result.Cob > 0); // Should have some COB from treatments
        Assert.Equal("Care Portal", result.Source);
    }

    [Parity]
    [Fact]
    public void CobTotal_FallsBackToTreatmentsIfNoDeviceStatus_ShouldMatchLegacy()
    {
        // Arrange - Legacy test: "should fall back to treatment data if no devicestatus data"
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>
        {
            new() { Carbs = 20, Mills = time - 1 },
        };
        var deviceStatuses = new List<DeviceStatus>(); // Empty device statuses

        // Act
        var result = _cobService.CobTotal(treatments, deviceStatuses, _testProfile, time);

        // Assert
        Assert.Equal("Care Portal", result.Source);
        Assert.True(result.Cob > 0); // Should have COB from treatments
    }

    [Parity]
    [Fact]
    public void CobTotal_FallsBackToTreatmentsIfOpenAPSDeviceStatusEmpty_ShouldMatchLegacy()
    {
        // Arrange - Legacy test: "should fall back to treatments if openaps devicestatus is present but empty"
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>
        {
            new() { Carbs = 20, Mills = time - 1 },
        };
        var deviceStatuses = new List<DeviceStatus>
        {
            new()
            {
                Device = "openaps://pi1",
                Mills = time - 1,
                OpenAps = new OpenApsStatus(), // Empty OpenAPS status
            },
        };

        // Act
        var result = _cobService.CobTotal(treatments, deviceStatuses, _testProfile, time);

        // Assert
        Assert.Equal("Care Portal", result.Source);
        Assert.True(result.Cob > 0); // Should fall back to treatment COB
    }

    [Parity]
    [Fact]
    public void CobTotal_FallsBackToTreatmentsIfOpenAPSDeviceStatusStale_ShouldMatchLegacy()
    {
        // Arrange - Legacy test: "should fall back to treatments if openaps devicestatus is present but too stale"
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>
        {
            new() { Carbs = 20, Mills = time - 1 },
        };
        var staleTime = time - (30 * 60 * 1000) - 1; // Older than 30min threshold
        var deviceStatuses = new List<DeviceStatus>
        {
            new DeviceStatus
            {
                Device = "openaps://pi1",
                Mills = staleTime,
                OpenAps = new OpenApsStatus { Cob = 5 },
            },
        };

        // Act
        var result = _cobService.CobTotal(treatments, deviceStatuses, _testProfile, time);

        // Assert
        Assert.Equal("Care Portal", result.Source);
        Assert.True(result.Cob > 0); // Should fall back to treatment COB
    }

    [Parity]
    [Fact]
    public void CobTotal_ReturnsOpenAPSData_WhenRecent_ShouldMatchLegacy()
    {
        // Arrange - Legacy test: "should return COB data from OpenAPS"
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>
        {
            new() { Carbs = 20, Mills = time - 1 },
        };

        var deviceStatuses = new List<DeviceStatus>
        {
            new DeviceStatus
            {
                Device = "openaps://pi1",
                Mills = time - 1,
                OpenAps = new OpenApsStatus { Cob = 5 },
            },
        };

        // Act
        var result = _cobService.CobTotal(treatments, deviceStatuses, _testProfile, time);

        // Assert
        Assert.Equal(5.0, result.Cob);
        Assert.Equal("OpenAPS", result.Source);
        Assert.Equal("openaps://pi1", result.Device);
    }

    [Parity]
    [Fact]
    public void CobTotal_ReturnsLoopData_WhenRecent_ShouldMatchLegacy()
    {
        // Arrange - Legacy test: "should return COB data from Loop"
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>
        {
            new() { Carbs = 20, Mills = time - 1 },
        };

        var deviceStatuses = new List<DeviceStatus>
        {
            new DeviceStatus
            {
                Device = "loop://iPhone",
                Mills = time - 1,
                Loop = new LoopStatus { Cob = new LoopCob { Cob = 5.0 } },
            },
        };

        // Act
        var result = _cobService.CobTotal(treatments, deviceStatuses, _testProfile, time);

        // Assert
        Assert.Equal(5.0, result.Cob);
        Assert.Equal("Loop", result.Source);
        Assert.Equal("loop://iPhone", result.Device);
    }

    #region Advanced COB Tests

    [Fact]
    public void CalcTreatmentAdvanced_HighFatMeal_ShouldUseSlowAbsorption()
    {
        // Arrange
        var startTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var highFatTreatment = new Treatment
        {
            Carbs = 40,
            Fat = 25, // High fat content
            Mills = startTime,
            Notes = "Pizza with extra cheese",
        };
        var normalTreatment = new Treatment { Carbs = 40, Mills = startTime };

        var testTime = startTime + 120 * 60 * 1000; // 2 hours later        // Act
        var highFatResult = _cobService.CalcTreatment(highFatTreatment, _testProfile, testTime);
        var normalResult = _cobService.CalcTreatment(normalTreatment, _testProfile, testTime);

        // Assert - High fat meal should have more COB remaining
        Assert.True(highFatResult.CobContrib > normalResult.CobContrib);
    }

    [Fact]
    public void CalcTreatmentAdvanced_FastCarbMeal_ShouldUseFastAbsorption()
    {
        // Arrange
        var startTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var fastTreatment = new Treatment
        {
            Carbs = 30,
            Mills = startTime,
            Notes = "Glucose tablets for low",
        };
        var normalTreatment = new Treatment { Carbs = 30, Mills = startTime };

        var testTime = startTime + 30 * 60 * 1000; // 30 minutes later

        // Act
        var fastResult = _cobService.CalcTreatment(fastTreatment, _testProfile, testTime);
        var normalResult = _cobService.CalcTreatment(normalTreatment, _testProfile, testTime);

        // Assert - Fast carbs should have less COB remaining
        Assert.True(fastResult.CobContrib < normalResult.CobContrib);
    }

    #endregion
    #region Helper Methods and Test Profile

    private static IobService CreateDefaultIobService()
    {
        var therapySettings = new Mock<ITherapySettingsResolver>();
        therapySettings.Setup(t => t.GetDIAAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(3.0);
        var sensitivity = new Mock<ISensitivityResolver>();
        sensitivity.Setup(s => s.GetSensitivityAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(50.0);
        var basalRate = new Mock<IBasalRateResolver>();
        basalRate.Setup(b => b.GetBasalRateAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(1.0);
        return new IobService(therapySettings.Object, sensitivity.Object, basalRate.Object);
    }

    private sealed class OrefIobAdapter : IIobService
    {
        // Default profile values for Oref calculations
        private const double DefaultDia = 3.0;
        private const double DefaultSens = 50.0;
        private const double DefaultCarbRatio = 18.0;
        private const double DefaultBasalRate = 1.0;

        public IobResult CalculateTotal(
            List<Treatment> treatments,
            List<DeviceStatus> deviceStatus,
            long? time = null,
            string? specProfile = null,
            List<Nocturne.Core.Models.V4.TempBasal>? tempBasals = null
        )
        {
            var currentTime = time ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var orefProfile = new OrefModels.OrefProfile
            {
                Dia = DefaultDia,
                Sens = DefaultSens,
                CarbRatio = DefaultCarbRatio,
                CurrentBasal = DefaultBasalRate,
                Curve = "bilinear",
            };
            var orefTreatments = BuildTreatments(treatments);

            var iobData = OrefService.CalculateIob(
                orefProfile,
                orefTreatments,
                DateTimeOffset.FromUnixTimeMilliseconds(currentTime)
            );

            if (iobData == null)
            {
                return new IobResult { Iob = 0.0, Activity = 0.0, Source = "Care Portal" };
            }

            return new IobResult
            {
                Iob = iobData.Iob,
                Activity = iobData.Activity * DefaultSens,
                Source = "Care Portal",
            };
        }

        public IobResult FromTreatments(
            List<Treatment> treatments,
            long? time = null,
            string? specProfile = null
        )
        {
            return CalculateTotal(treatments, new List<DeviceStatus>(), time, specProfile);
        }

        public IobResult FromDeviceStatus(DeviceStatus deviceStatusEntry) => new();

        public IobResult LastIobDeviceStatus(List<DeviceStatus> deviceStatus, long time) => new();

        public IobContribution CalcTreatment(
            Treatment treatment,
            long? time = null,
            string? specProfile = null
        )
        {
            return new IobContribution();
        }

        public IobContribution CalcBasalTreatment(
            Treatment treatment,
            long? time = null,
            string? specProfile = null
        )
        {
            return new IobContribution();
        }

        public IobContribution CalcTempBasalIob(
            Nocturne.Core.Models.V4.TempBasal tempBasal,
            long? time = null,
            string? specProfile = null
        )
        {
            return new IobContribution();
        }

        public IobResult FromTempBasals(
            List<Nocturne.Core.Models.V4.TempBasal> tempBasals,
            long? time = null,
            string? specProfile = null
        )
        {
            return new IobResult();
        }

        private static List<OrefModels.OrefTreatment> BuildTreatments(IEnumerable<Treatment> treatments)
        {
            return treatments
                .Select(t => new OrefModels.OrefTreatment
                {
                    EventType = t.EventType,
                    Mills = t.Mills,
                    Insulin = t.Insulin,
                    Carbs = t.Carbs,
                    Rate = t.Rate,
                    Duration = t.Duration.HasValue ? (int?)Math.Round(t.Duration.Value) : null,
                })
                .ToList();
        }
    }

    private class TestCobProfile : IProfileService
    {
        public double CarbAbsorptionRate { get; set; } = 30.0; // 30g/hr default
        public double CarbRatio { get; set; } = 18.0; // 18g carbs per 1U insulin
        public double Sensitivity { get; set; } = 50.0; // 50 mg/dL per 1U insulin
        public double DIA { get; set; } = 3.0; // 3 hours Duration of Insulin Action
        public double BasalRate { get; set; } = 1.0; // 1.0 U/hr basal rate
        public int? CustomAbsorptionTime { get; set; } = null;

        // COB-specific methods
        public double GetCarbAbsorptionRate(long time, string? specProfile = null) =>
            CarbAbsorptionRate;

        public double GetCarbRatio(long time, string? specProfile = null) => CarbRatio;

        public double GetSensitivity(long time, string? specProfile = null) => Sensitivity;

        // IOB-specific methods
        public double GetDIA(long time, string? specProfile = null) => DIA;

        public double GetBasalRate(long time, string? specProfile = null) => BasalRate;

        // Profile management methods
        public bool HasData() => true;

        public void LoadData(
            List<Profile> profileData
        ) { /* Test profile doesn't use Profile objects */
        }

        public Profile? GetCurrentProfile(long? time = null, string? specProfile = null) => null;

        public int? GetAbsorptionTime(long time, string? specProfile = null) =>
            CustomAbsorptionTime;

        // New interface methods with default implementations for tests
        public void Clear() { }

        public string? GetActiveProfileName(long? time = null) => "Default";

        public List<string> ListBasalProfiles() => new List<string> { "Default" };

        public string? GetUnits(string? specProfile = null) => "mg/dl";

        public string? GetTimezone(string? specProfile = null) => null;

        public double GetValueByTime(long time, string valueType, string? specProfile = null) =>
            0.0;

        public double GetLowBGTarget(long time, string? specProfile = null) => 70.0;

        public double GetHighBGTarget(long time, string? specProfile = null) => 180.0;

        public void UpdateTreatments(
            List<Treatment>? profileTreatments = null,
            List<Treatment>? tempBasalTreatments = null,
            List<Treatment>? comboBolusTreatments = null
        ) { }

        public Treatment? GetActiveProfileTreatment(long time) => null;

        public Treatment? GetTempBasalTreatment(long time) => null;

        public Treatment? GetComboBolusTreatment(long time) => null;

        public TempBasalResult GetTempBasal(long time, string? specProfile = null) =>
            new TempBasalResult
            {
                Basal = BasalRate,
                TempBasal = BasalRate,
                TotalBasal = BasalRate,
            };
    }

    #endregion
}
