using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.Treatments;
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

    // Default test profile values
    private const double DefaultCarbAbsorptionRate = 30.0;
    private const double DefaultSensitivity = 50.0;
    private const double DefaultCarbRatio = 18.0;
    private const double DefaultDIA = 3.0;
    private const double DefaultBasalRate = 1.0;

    public CobServiceTests()
    {
        var logger = new Mock<ILogger<Nocturne.API.Services.Treatments.CobService>>();
        IIobService iobService = OrefService.IsAvailable() ? new OrefIobAdapter() : CreateDefaultIobService();

        var sensitivity = new Mock<ISensitivityResolver>();
        sensitivity.Setup(s => s.GetSensitivityAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(DefaultSensitivity);

        var carbRatio = new Mock<ICarbRatioResolver>();
        carbRatio.Setup(c => c.GetCarbRatioAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(DefaultCarbRatio);

        var therapySettings = new Mock<ITherapySettingsResolver>();
        therapySettings.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        therapySettings.Setup(t => t.GetCarbAbsorptionRateAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(DefaultCarbAbsorptionRate);

        _cobService = new Nocturne.API.Services.Treatments.CobService(
            logger.Object, iobService, sensitivity.Object, carbRatio.Object, therapySettings.Object);
    }

    [Fact]
    public void CobTotal_MultipleTreatments_ShouldMatchLegacyResults()
    {
        var treatments = new List<Treatment>
        {
            new()
            {
                Carbs = 100,
                Mills = new DateTimeOffset(2015, 5, 29, 2, 3, 48, 827, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            },
            new()
            {
                Carbs = 10,
                Mills = new DateTimeOffset(2015, 5, 29, 3, 45, 10, 670, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            },
        };

        var after100Time = new DateTimeOffset(2015, 5, 29, 2, 3, 49, 827, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var before10Time = new DateTimeOffset(2015, 5, 29, 3, 45, 10, 670, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var after10Time = new DateTimeOffset(2015, 5, 29, 3, 45, 11, 670, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var after100 = _cobService.CobTotal(treatments, new List<DeviceStatus>(), after100Time);
        var before10 = _cobService.CobTotal(treatments, new List<DeviceStatus>(), before10Time);
        var after10 = _cobService.CobTotal(treatments, new List<DeviceStatus>(), after10Time);

        Assert.Equal(100.0, after100.Cob, 1);
        Assert.Equal(59.0, Math.Round(before10.Cob), 0);
        Assert.Equal(69.0, Math.Round(after10.Cob), 0);
    }

    [Fact]
    public void CobTotal_SingleTreatment_ShouldFollowAbsorptionCurve()
    {
        var treatment = new Treatment
        {
            Carbs = 8,
            Mills = new DateTimeOffset(2015, 5, 29, 4, 40, 40, 174, TimeSpan.Zero).ToUnixTimeMilliseconds(),
        };
        var treatments = new List<Treatment> { treatment };

        var rightAfterTime = new DateTimeOffset(2015, 5, 29, 4, 41, 40, 174, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var later1Time = new DateTimeOffset(2015, 5, 29, 5, 4, 40, 174, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var later2Time = new DateTimeOffset(2015, 5, 29, 5, 20, 0, 174, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var later3Time = new DateTimeOffset(2015, 5, 29, 5, 50, 0, 174, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var later4Time = new DateTimeOffset(2015, 5, 29, 6, 50, 0, 174, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var result1 = _cobService.CobTotal(treatments, new List<DeviceStatus>(), rightAfterTime);
        var result2 = _cobService.CobTotal(treatments, new List<DeviceStatus>(), later1Time);
        var result3 = _cobService.CobTotal(treatments, new List<DeviceStatus>(), later2Time);
        var result4 = _cobService.CobTotal(treatments, new List<DeviceStatus>(), later3Time);
        var result5 = _cobService.CobTotal(treatments, new List<DeviceStatus>(), later4Time);

        Assert.Equal(8.0, result1.Cob, 1);
        Assert.Equal(6.0, result2.Cob, 1);
        Assert.Equal(0.0, result3.Cob, 1);
        Assert.Equal(0.0, result4.Cob, 1);
        Assert.Equal(0.0, result5.Cob, 1);
    }

    [Fact]
    public void CalcTreatment_NoCarbs_ShouldReturnZero()
    {
        var treatment = new Treatment
        {
            Insulin = 1.0,
            Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        var result = _cobService.CalcTreatment(treatment, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        Assert.Equal(0.0, result.CobContrib);
        Assert.Equal(0.0, result.ActivityContrib);
    }

    [Fact]
    public void CalcTreatment_LinearAbsorption_ShouldDecreaseOverTime()
    {
        var startTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatment = new Treatment { Carbs = 60, Mills = startTime };

        var rightAfter = _cobService.CalcTreatment(treatment, startTime + 1000);
        var after30Min = _cobService.CalcTreatment(treatment, startTime + 30 * 60 * 1000);
        var after60Min = _cobService.CalcTreatment(treatment, startTime + 60 * 60 * 1000);
        var after120Min = _cobService.CalcTreatment(treatment, startTime + 120 * 60 * 1000);

        Assert.True(rightAfter.CobContrib > after30Min.CobContrib);
        Assert.True(after30Min.CobContrib > after60Min.CobContrib);
        Assert.True(after60Min.CobContrib > after120Min.CobContrib);
        Assert.True(after120Min.CobContrib >= 0);
    }

    [Fact]
    public void CalcTreatment_WithCustomAbsorptionTime_ShouldUseCustomTime()
    {
        var startTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var fastTreatment = new Treatment { Carbs = 30, Mills = startTime, AbsorptionTime = 60 };
        var slowTreatment = new Treatment { Carbs = 30, Mills = startTime, AbsorptionTime = 240 };

        var testTime = startTime + 90 * 60 * 1000;

        var fastResult = _cobService.CalcTreatment(fastTreatment, testTime);
        var slowResult = _cobService.CalcTreatment(slowTreatment, testTime);

        Assert.True(slowResult.CobContrib > fastResult.CobContrib);
        Assert.Equal(0.0, fastResult.CobContrib, 1);
    }

    [Fact]
    public void FromDeviceStatus_LoopCOB_ShouldParseProperly()
    {
        var deviceStatus = new DeviceStatus
        {
            Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Device = "loop://iPhone",
            Loop = new LoopStatus { Cob = new LoopCob { Cob = 25.5, Timestamp = DateTime.UtcNow.ToString("o") } },
        };

        var result = _cobService.FromDeviceStatus(deviceStatus);

        Assert.Equal(25.5, result.Cob);
        Assert.Equal("Loop", result.Source);
        Assert.Equal("loop://iPhone", result.Device);
    }

    [Fact]
    public void FromDeviceStatus_OpenAPSCOB_ShouldParseProperly()
    {
        var deviceStatus = new DeviceStatus
        {
            Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Device = "openaps://pi1",
            OpenAps = new OpenApsStatus { Cob = 42.0 },
        };

        var result = _cobService.FromDeviceStatus(deviceStatus);

        Assert.Equal(42.0, result.Cob);
        Assert.Equal("OpenAPS", result.Source);
        Assert.Equal("openaps://pi1", result.Device);
    }

    [Fact]
    public void CobTotal_PrioritizesDeviceStatusOverTreatments()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>
        {
            new() { Carbs = 30, Mills = time - 30 * 60 * 1000 },
        };
        var deviceStatuses = new List<DeviceStatus>
        {
            new()
            {
                Mills = time - 5 * 60 * 1000,
                Device = "loop://iPhone",
                Loop = new LoopStatus { Cob = new LoopCob { Cob = 15.0 } },
            },
        };

        var result = _cobService.CobTotal(treatments, deviceStatuses, time);

        Assert.Equal(15.0, result.Cob);
        Assert.Equal("Loop", result.Source);
    }

    [Fact]
    public void CobTotal_FallsBackToTreatments_WhenDeviceStatusStale()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>
        {
            new() { Carbs = 30, Mills = time - 30 * 60 * 1000 },
        };
        var deviceStatuses = new List<DeviceStatus>
        {
            new()
            {
                Mills = time - 35 * 60 * 1000,
                Device = "loop://iPhone",
                Loop = new LoopStatus { Cob = new LoopCob { Cob = 15.0 } },
            },
        };

        var result = _cobService.CobTotal(treatments, deviceStatuses, time);

        Assert.True(result.Cob > 0);
        Assert.Equal("Care Portal", result.Source);
    }

    [Parity]
    [Fact]
    public void CobTotal_FallsBackToTreatmentsIfNoDeviceStatus_ShouldMatchLegacy()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment> { new() { Carbs = 20, Mills = time - 1 } };

        var result = _cobService.CobTotal(treatments, new List<DeviceStatus>(), time);

        Assert.Equal("Care Portal", result.Source);
        Assert.True(result.Cob > 0);
    }

    [Parity]
    [Fact]
    public void CobTotal_FallsBackToTreatmentsIfOpenAPSDeviceStatusEmpty_ShouldMatchLegacy()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment> { new() { Carbs = 20, Mills = time - 1 } };
        var deviceStatuses = new List<DeviceStatus>
        {
            new() { Device = "openaps://pi1", Mills = time - 1, OpenAps = new OpenApsStatus() },
        };

        var result = _cobService.CobTotal(treatments, deviceStatuses, time);

        Assert.Equal("Care Portal", result.Source);
        Assert.True(result.Cob > 0);
    }

    [Parity]
    [Fact]
    public void CobTotal_FallsBackToTreatmentsIfOpenAPSDeviceStatusStale_ShouldMatchLegacy()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment> { new() { Carbs = 20, Mills = time - 1 } };
        var staleTime = time - (30 * 60 * 1000) - 1;
        var deviceStatuses = new List<DeviceStatus>
        {
            new() { Device = "openaps://pi1", Mills = staleTime, OpenAps = new OpenApsStatus { Cob = 5 } },
        };

        var result = _cobService.CobTotal(treatments, deviceStatuses, time);

        Assert.Equal("Care Portal", result.Source);
        Assert.True(result.Cob > 0);
    }

    [Parity]
    [Fact]
    public void CobTotal_ReturnsOpenAPSData_WhenRecent_ShouldMatchLegacy()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment> { new() { Carbs = 20, Mills = time - 1 } };
        var deviceStatuses = new List<DeviceStatus>
        {
            new() { Device = "openaps://pi1", Mills = time - 1, OpenAps = new OpenApsStatus { Cob = 5 } },
        };

        var result = _cobService.CobTotal(treatments, deviceStatuses, time);

        Assert.Equal(5.0, result.Cob);
        Assert.Equal("OpenAPS", result.Source);
        Assert.Equal("openaps://pi1", result.Device);
    }

    [Parity]
    [Fact]
    public void CobTotal_ReturnsLoopData_WhenRecent_ShouldMatchLegacy()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment> { new() { Carbs = 20, Mills = time - 1 } };
        var deviceStatuses = new List<DeviceStatus>
        {
            new() { Device = "loop://iPhone", Mills = time - 1, Loop = new LoopStatus { Cob = new LoopCob { Cob = 5.0 } } },
        };

        var result = _cobService.CobTotal(treatments, deviceStatuses, time);

        Assert.Equal(5.0, result.Cob);
        Assert.Equal("Loop", result.Source);
        Assert.Equal("loop://iPhone", result.Device);
    }

    #region Advanced COB Tests

    [Fact]
    public void CalcTreatmentAdvanced_HighFatMeal_ShouldUseSlowAbsorption()
    {
        var startTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var highFatTreatment = new Treatment { Carbs = 40, Fat = 25, Mills = startTime, Notes = "Pizza with extra cheese" };
        var normalTreatment = new Treatment { Carbs = 40, Mills = startTime };

        var testTime = startTime + 120 * 60 * 1000;

        var highFatResult = _cobService.CalcTreatment(highFatTreatment, testTime);
        var normalResult = _cobService.CalcTreatment(normalTreatment, testTime);

        Assert.True(highFatResult.CobContrib > normalResult.CobContrib);
    }

    [Fact]
    public void CalcTreatmentAdvanced_FastCarbMeal_ShouldUseFastAbsorption()
    {
        var startTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var fastTreatment = new Treatment { Carbs = 30, Mills = startTime, Notes = "Glucose tablets for low" };
        var normalTreatment = new Treatment { Carbs = 30, Mills = startTime };

        var testTime = startTime + 30 * 60 * 1000;

        var fastResult = _cobService.CalcTreatment(fastTreatment, testTime);
        var normalResult = _cobService.CalcTreatment(normalTreatment, testTime);

        Assert.True(fastResult.CobContrib < normalResult.CobContrib);
    }

    #endregion

    #region Helper Methods and Test Profile

    private static IobService CreateDefaultIobService()
    {
        var therapySettings = new Mock<ITherapySettingsResolver>();
        therapySettings.Setup(t => t.GetDIAAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(DefaultDIA);
        var sensitivity = new Mock<ISensitivityResolver>();
        sensitivity.Setup(s => s.GetSensitivityAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(DefaultSensitivity);
        var basalRate = new Mock<IBasalRateResolver>();
        basalRate.Setup(b => b.GetBasalRateAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(DefaultBasalRate);
        return new IobService(therapySettings.Object, sensitivity.Object, basalRate.Object);
    }

    private sealed class OrefIobAdapter : IIobService
    {
        public IobResult CalculateTotal(
            List<Treatment> treatments, List<DeviceStatus> deviceStatus,
            long? time = null, string? specProfile = null,
            List<Nocturne.Core.Models.V4.TempBasal>? tempBasals = null)
        {
            var currentTime = time ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var orefProfile = new OrefModels.OrefProfile
            {
                Dia = DefaultDIA, Sens = DefaultSensitivity,
                CarbRatio = DefaultCarbRatio, CurrentBasal = DefaultBasalRate, Curve = "bilinear",
            };
            var orefTreatments = BuildTreatments(treatments);
            var iobData = OrefService.CalculateIob(orefProfile, orefTreatments, DateTimeOffset.FromUnixTimeMilliseconds(currentTime));
            if (iobData == null)
                return new IobResult { Iob = 0.0, Activity = 0.0, Source = "Care Portal" };
            return new IobResult { Iob = iobData.Iob, Activity = iobData.Activity * DefaultSensitivity, Source = "Care Portal" };
        }

        public IobResult FromTreatments(List<Treatment> treatments, long? time = null, string? specProfile = null)
            => CalculateTotal(treatments, new List<DeviceStatus>(), time, specProfile);
        public IobResult FromDeviceStatus(DeviceStatus deviceStatusEntry) => new();
        public IobResult LastIobDeviceStatus(List<DeviceStatus> deviceStatus, long time) => new();
        public IobContribution CalcTreatment(Treatment treatment, long? time = null, string? specProfile = null) => new();
        public IobContribution CalcBasalTreatment(Treatment treatment, long? time = null, string? specProfile = null) => new();
        public IobContribution CalcTempBasalIob(Nocturne.Core.Models.V4.TempBasal tempBasal, long? time = null, string? specProfile = null) => new();
        public IobResult FromTempBasals(List<Nocturne.Core.Models.V4.TempBasal> tempBasals, long? time = null, string? specProfile = null) => new();

        private static List<OrefModels.OrefTreatment> BuildTreatments(IEnumerable<Treatment> treatments)
        {
            return treatments.Select(t => new OrefModels.OrefTreatment
            {
                EventType = t.EventType, Mills = t.Mills, Insulin = t.Insulin,
                Carbs = t.Carbs, Rate = t.Rate,
                Duration = t.Duration.HasValue ? (int?)Math.Round(t.Duration.Value) : null,
            }).ToList();
        }
    }

    #endregion
}
