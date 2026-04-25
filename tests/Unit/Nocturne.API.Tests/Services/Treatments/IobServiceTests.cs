using Moq;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Treatments;

/// <summary>
/// Complete IOB calculation tests with 1:1 legacy JavaScript compatibility
/// Tests exact algorithms from ClientApp/mocha-tests/iob.test.js
/// NO SIMPLIFICATIONS - Must match legacy behavior exactly
/// </summary>
[Parity("iob.test.js")]
public class IobServiceTests
{
    private readonly IobService _iobService;

    // Default test profile values matching the old TestProfile
    private const double DefaultDIA = 3.0;
    private const double DefaultSensitivity = 95.0;
    private const double DefaultBasalRate = 1.0;

    public IobServiceTests()
    {
        var therapySettings = new Mock<ITherapySettingsResolver>();
        therapySettings
            .Setup(t => t.GetDIAAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultDIA);

        var sensitivityResolver = new Mock<ISensitivityResolver>();
        sensitivityResolver
            .Setup(s => s.GetSensitivityAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultSensitivity);

        var basalRateResolver = new Mock<IBasalRateResolver>();
        basalRateResolver
            .Setup(b => b.GetBasalRateAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultBasalRate);

        _iobService = new IobService(therapySettings.Object, sensitivityResolver.Object, basalRateResolver.Object);
    }

    private static IobService CreateServiceWithDIA(double dia)
    {
        var therapySettings = new Mock<ITherapySettingsResolver>();
        therapySettings
            .Setup(t => t.GetDIAAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(dia);

        var sensitivityResolver = new Mock<ISensitivityResolver>();
        sensitivityResolver
            .Setup(s => s.GetSensitivityAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultSensitivity);

        var basalRateResolver = new Mock<IBasalRateResolver>();
        basalRateResolver
            .Setup(b => b.GetBasalRateAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultBasalRate);

        return new IobService(therapySettings.Object, sensitivityResolver.Object, basalRateResolver.Object);
    }

    private static IobService CreateServiceWithSensitivity(double sens)
    {
        var therapySettings = new Mock<ITherapySettingsResolver>();
        therapySettings
            .Setup(t => t.GetDIAAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultDIA);

        var sensitivityResolver = new Mock<ISensitivityResolver>();
        sensitivityResolver
            .Setup(s => s.GetSensitivityAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(sens);

        var basalRateResolver = new Mock<IBasalRateResolver>();
        basalRateResolver
            .Setup(b => b.GetBasalRateAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultBasalRate);

        return new IobService(therapySettings.Object, sensitivityResolver.Object, basalRateResolver.Object);
    }

    [Fact]
    public void CalcTreatment_SingleBolusRightAfter_ShouldReturn1Point00IOB()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatment = new Treatment { Mills = time - 1, Insulin = 1.0 };

        var result = _iobService.CalcTreatment(treatment, time);

        Assert.Equal(1.0, result.IobContrib, 2);
    }

    [Fact]
    public void CalcTreatment_After1Hour_ShouldHaveLessIOBThan1()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatment = new Treatment { Mills = time - 1, Insulin = 1.0 };

        var result = _iobService.CalcTreatment(treatment, time + 60 * 60 * 1000);

        Assert.True(result.IobContrib < 1.0);
        Assert.True(result.IobContrib > 0.0);
    }

    [Fact]
    public void CalcTreatment_After3Hours_ShouldHaveZeroIOB()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatment = new Treatment { Mills = time - 1, Insulin = 1.0 };

        var result = _iobService.CalcTreatment(treatment, time + 3 * 60 * 60 * 1000);

        Assert.Equal(0.0, result.IobContrib, 3);
    }

    [Fact]
    public void CalcTreatment_NoNegativeIOB_WhenApproachingZero()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatment = new Treatment { Mills = time, Insulin = 5.0 };

        var result = _iobService.CalcTreatment(treatment, time + 3 * 60 * 60 * 1000 - 90 * 1000);

        Assert.True(result.IobContrib >= 0.0);
    }

    [Fact]
    public void CalcTreatment_4HourDIA_ShouldUseCorrectDuration()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var service = CreateServiceWithDIA(4.0);
        var treatment = new Treatment { Mills = time - 1, Insulin = 1.0 };

        var rightAfter = service.CalcTreatment(treatment, time);
        var afterHour = service.CalcTreatment(treatment, time + 60 * 60 * 1000);

        Assert.Equal(1.0, rightAfter.IobContrib, 2);
        Assert.True(afterHour.IobContrib > 0.5);
    }

    [Fact]
    public void FromTreatments_MultipleTreatments_ShouldAggregateCorrectly()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>
        {
            new() { Mills = time - 60 * 60 * 1000, Insulin = 2.0 },
            new() { Mills = time - 30 * 60 * 1000, Insulin = 1.5 },
            new() { Mills = time - 10 * 60 * 1000, Insulin = 1.0 },
        };

        var result = _iobService.FromTreatments(treatments, time);

        Assert.True(result.Iob > 0);
        Assert.True(result.Iob < 4.5);
        Assert.Equal("Care Portal", result.Source);
    }

    [Fact]
    public void FromTreatments_WithBasalTreatments_ShouldCalculateBasalIOB()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>
        {
            new()
            {
                Mills = time - 60 * 60 * 1000,
                EventType = "Temp Basal",
                Absolute = 1.5,
                Duration = 120,
            },
        };

        var result = _iobService.FromTreatments(treatments, time);

        Assert.True(result.BasalIob.HasValue);
        Assert.True(result.BasalIob.Value > 0);
    }

    [Theory]
    [InlineData("Loop", 0.75)]
    [InlineData("OpenAPS", 0.047)]
    [InlineData("MM Connect", 0.87)]
    public void FromDeviceStatus_DifferentSources_ShouldParseProperly(
        string expectedSource,
        double expectedIOB
    )
    {
        var deviceStatus = CreateDeviceStatusForSource(expectedSource, expectedIOB);

        var result = _iobService.FromDeviceStatus(deviceStatus);

        Assert.Equal(expectedSource, result.Source);
        Assert.Equal(expectedIOB, result.Iob, 3);
    }

    [Fact]
    public void LastIobDeviceStatus_PrioritizesLoopOverOpenAPS()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var deviceStatuses = new List<DeviceStatus>
        {
            CreateDeviceStatusForSource("OpenAPS", 0.5, time - 5 * 60 * 1000),
            CreateDeviceStatusForSource("Loop", 0.75, time - 10 * 60 * 1000),
        };

        var result = _iobService.LastIobDeviceStatus(deviceStatuses, time);

        Assert.Equal("Loop", result.Source);
        Assert.Equal(0.75, result.Iob);
    }

    [Fact]
    public void LastIobDeviceStatus_IgnoresStaleData()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var staleTime = time - 31 * 60 * 1000;
        var deviceStatuses = new List<DeviceStatus>
        {
            CreateDeviceStatusForSource("Loop", 0.75, staleTime),
        };

        var result = _iobService.LastIobDeviceStatus(deviceStatuses, time);

        Assert.Equal(0.0, result.Iob);
    }

    [Fact]
    public void CalculateTotal_CombinesDeviceStatusAndTreatments()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>
        {
            new() { Mills = time - 60 * 60 * 1000, Insulin = 1.0 },
        };
        var deviceStatuses = new List<DeviceStatus>
        {
            CreateDeviceStatusForSource("Loop", 1.5, time - 5 * 60 * 1000),
        };

        var result = _iobService.CalculateTotal(treatments, deviceStatuses, time);

        Assert.Equal(1.5, result.Iob);
        Assert.True(result.TreatmentIob.HasValue);
        Assert.Equal("Loop", result.Source);
    }

    [Fact]
    public void CalculateTotal_FallsBackToTreatments_WhenNoDeviceStatus()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>
        {
            new() { Mills = time - 30 * 60 * 1000, Insulin = 2.0 },
        };

        var result = _iobService.CalculateTotal(treatments, new List<DeviceStatus>(), time);

        Assert.True(result.Iob > 0);
        Assert.Equal("Care Portal", result.Source);
    }

    #region CalcTempBasalIob Tests

    [Fact]
    public void CalcTempBasalIob_AboveScheduled_ShouldReturnPositiveIob()
    {
        var now = DateTimeOffset.UtcNow;
        var tempBasal = new TempBasal
        {
            StartTimestamp = now.AddMinutes(-30).UtcDateTime,
            EndTimestamp = now.AddMinutes(-1).UtcDateTime,
            Rate = 1.5,
            ScheduledRate = 0.5,
            Origin = TempBasalOrigin.Algorithm,
        };

        var result = _iobService.CalcTempBasalIob(tempBasal, now.ToUnixTimeMilliseconds());

        Assert.True(result.IobContrib > 0);
    }

    [Fact]
    public void CalcTempBasalIob_AtScheduledRate_ShouldReturnZero()
    {
        var now = DateTimeOffset.UtcNow;
        var tempBasal = new TempBasal
        {
            StartTimestamp = now.AddMinutes(-30).UtcDateTime,
            EndTimestamp = now.AddMinutes(-1).UtcDateTime,
            Rate = 1.0,
            ScheduledRate = 1.0,
            Origin = TempBasalOrigin.Algorithm,
        };

        var result = _iobService.CalcTempBasalIob(tempBasal, now.ToUnixTimeMilliseconds());

        Assert.Equal(0.0, result.IobContrib);
    }

    [Fact]
    public void CalcTempBasalIob_BelowScheduled_ShouldReturnZero()
    {
        var now = DateTimeOffset.UtcNow;
        var tempBasal = new TempBasal
        {
            StartTimestamp = now.AddMinutes(-30).UtcDateTime,
            EndTimestamp = now.AddMinutes(-1).UtcDateTime,
            Rate = 0.3,
            ScheduledRate = 1.0,
            Origin = TempBasalOrigin.Algorithm,
        };

        var result = _iobService.CalcTempBasalIob(tempBasal, now.ToUnixTimeMilliseconds());

        Assert.Equal(0.0, result.IobContrib);
    }

    [Fact]
    public void CalcTempBasalIob_Suspended_ShouldReturnZero()
    {
        var now = DateTimeOffset.UtcNow;
        var tempBasal = new TempBasal
        {
            StartTimestamp = now.AddMinutes(-30).UtcDateTime,
            EndTimestamp = now.AddMinutes(-1).UtcDateTime,
            Rate = 1.5,
            ScheduledRate = 0.5,
            Origin = TempBasalOrigin.Suspended,
        };

        var result = _iobService.CalcTempBasalIob(tempBasal, now.ToUnixTimeMilliseconds());

        Assert.Equal(0.0, result.IobContrib);
    }

    [Fact]
    public void CalcTempBasalIob_NoEndTime_ShouldReturnZero()
    {
        var now = DateTimeOffset.UtcNow;
        var tempBasal = new TempBasal
        {
            StartTimestamp = now.AddMinutes(-30).UtcDateTime,
            EndTimestamp = null,
            Rate = 2.0,
            ScheduledRate = 0.5,
            Origin = TempBasalOrigin.Algorithm,
        };

        var result = _iobService.CalcTempBasalIob(tempBasal, now.ToUnixTimeMilliseconds());

        Assert.Equal(0.0, result.IobContrib);
    }

    [Fact]
    public void CalcTempBasalIob_AfterDIA_ShouldReturnZero()
    {
        var now = DateTimeOffset.UtcNow;
        var tempBasal = new TempBasal
        {
            StartTimestamp = now.AddHours(-4).UtcDateTime,
            EndTimestamp = now.AddHours(-3.5).UtcDateTime,
            Rate = 2.0,
            ScheduledRate = 0.5,
            Origin = TempBasalOrigin.Algorithm,
        };

        var result = _iobService.CalcTempBasalIob(tempBasal, now.ToUnixTimeMilliseconds());

        Assert.Equal(0.0, result.IobContrib);
    }

    [Fact]
    public void CalcTempBasalIob_LinearDecay_ShouldDecreaseOverTime()
    {
        var now = DateTimeOffset.UtcNow;
        var tempBasal = new TempBasal
        {
            StartTimestamp = now.AddMinutes(-60).UtcDateTime,
            EndTimestamp = now.AddMinutes(-30).UtcDateTime,
            Rate = 2.0,
            ScheduledRate = 0.5,
            Origin = TempBasalOrigin.Algorithm,
        };

        var earlier = _iobService.CalcTempBasalIob(tempBasal, now.AddMinutes(-30).ToUnixTimeMilliseconds());
        var later = _iobService.CalcTempBasalIob(tempBasal, now.ToUnixTimeMilliseconds());

        Assert.True(earlier.IobContrib > later.IobContrib);
        Assert.True(later.IobContrib > 0);
    }

    #endregion

    #region FromTempBasals Tests

    [Fact]
    public void FromTempBasals_MultipleTempBasals_ShouldAggregateBasalIob()
    {
        var now = DateTimeOffset.UtcNow;
        var tempBasals = new List<TempBasal>
        {
            new()
            {
                StartTimestamp = now.AddMinutes(-60).UtcDateTime,
                EndTimestamp = now.AddMinutes(-30).UtcDateTime,
                Rate = 2.0, ScheduledRate = 0.5,
                Origin = TempBasalOrigin.Algorithm,
            },
            new()
            {
                StartTimestamp = now.AddMinutes(-30).UtcDateTime,
                EndTimestamp = now.AddMinutes(-5).UtcDateTime,
                Rate = 1.8, ScheduledRate = 0.5,
                Origin = TempBasalOrigin.Algorithm,
            },
        };

        var result = _iobService.FromTempBasals(tempBasals, now.ToUnixTimeMilliseconds());

        Assert.True(result.BasalIob.HasValue);
        Assert.True(result.BasalIob!.Value > 0);
    }

    [Fact]
    public void FromTempBasals_EmptyList_ShouldReturnZero()
    {
        var result = _iobService.FromTempBasals(new List<TempBasal>(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        Assert.Equal(0.0, result.Iob);
        Assert.Null(result.BasalIob);
    }

    [Fact]
    public void FromTempBasals_SetsBasalIobNotBolus()
    {
        var now = DateTimeOffset.UtcNow;
        var tempBasals = new List<TempBasal>
        {
            new()
            {
                StartTimestamp = now.AddMinutes(-30).UtcDateTime,
                EndTimestamp = now.AddMinutes(-5).UtcDateTime,
                Rate = 2.0, ScheduledRate = 0.5,
                Origin = TempBasalOrigin.Algorithm,
            },
        };

        var result = _iobService.FromTempBasals(tempBasals, now.ToUnixTimeMilliseconds());

        Assert.Equal(0.0, result.Iob);
        Assert.True(result.BasalIob.HasValue);
        Assert.True(result.BasalIob!.Value > 0);
    }

    #endregion

    #region Per-Treatment Insulin Context Tests

    [Fact]
    public void CalcTreatment_WithInsulinContext_ShouldUseContextDia()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatment = new Treatment
        {
            Mills = time - 1, Insulin = 1.0,
            InsulinContext = new TreatmentInsulinContext
            {
                PatientInsulinId = Guid.NewGuid(), InsulinName = "Fiasp",
                Dia = 5.0, Peak = 90, Curve = "rapid-acting", Concentration = 100,
            },
        };

        var result = _iobService.CalcTreatment(treatment, time + 3 * 60 * 60 * 1000);

        Assert.True(result.IobContrib > 0, "IOB should still be active at 3hrs with 5hr DIA");
    }

    [Fact]
    public void CalcTreatment_WithoutInsulinContext_ShouldUseProfileDia()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatment = new Treatment { Mills = time - 1, Insulin = 1.0, InsulinContext = null };

        var result = _iobService.CalcTreatment(treatment, time + 3 * 60 * 60 * 1000);

        Assert.Equal(0.0, result.IobContrib, 3);
    }

    [Fact]
    public void CalcTreatment_WithInsulinContext_ShouldUseContextPeak()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatmentWithContext = new Treatment
        {
            Mills = time - 1, Insulin = 1.0,
            InsulinContext = new TreatmentInsulinContext
            {
                PatientInsulinId = Guid.NewGuid(), InsulinName = "Regular",
                Dia = 3.0, Peak = 120, Curve = "rapid-acting", Concentration = 100,
            },
        };
        var treatmentWithoutContext = new Treatment { Mills = time - 1, Insulin = 1.0, InsulinContext = null };

        var atTime = time + 80 * 60 * 1000;
        var resultWithContext = _iobService.CalcTreatment(treatmentWithContext, atTime);
        var resultWithoutContext = _iobService.CalcTreatment(treatmentWithoutContext, atTime);

        Assert.NotEqual(Math.Round(resultWithContext.IobContrib, 5), Math.Round(resultWithoutContext.IobContrib, 5));
    }

    [Fact]
    public void FromTreatments_MixedContextAndNoContext_ShouldUseRespectiveDia()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>
        {
            new()
            {
                Mills = time - 1, Insulin = 1.0,
                InsulinContext = new TreatmentInsulinContext
                {
                    PatientInsulinId = Guid.NewGuid(), InsulinName = "Fiasp",
                    Dia = 5.0, Peak = 90, Curve = "rapid-acting", Concentration = 100,
                },
            },
            new() { Mills = time - 1, Insulin = 1.0, InsulinContext = null },
        };

        var result = _iobService.FromTreatments(treatments, time + 3 * 60 * 60 * 1000);

        Assert.True(result.Iob > 0, "Should have IOB from the 5hr DIA treatment");
        Assert.True(result.Iob < 1.0, "Should be less than full dose since one treatment is fully decayed");
    }

    #endregion

    #region Exact Legacy Test Cases

    [Fact]
    public void IOB_ExactLegacyTestCase_100mgdl_1UnitIOB()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment> { new() { Mills = time, Insulin = 1.0 } };
        var service = CreateServiceWithSensitivity(50);

        var result = service.FromTreatments(treatments, time);

        Assert.Equal(1.0, result.Iob, 2);
        Assert.Equal(0.0, result.Activity ?? 0.0, 3);
    }

    [Fact]
    public void IOB_ExactPolynomialCurve_BeforePeak()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatment = new Treatment { Mills = time - 30 * 60 * 1000, Insulin = 1.0 };

        var result = _iobService.CalcTreatment(treatment, time);

        var expectedIob = 1.0 * (1.0 - 0.001852 * 49.0 + 0.001852 * 7.0);
        Assert.Equal(expectedIob, result.IobContrib, 5);
    }

    [Fact]
    public void IOB_ExactPolynomialCurve_AfterPeak()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatment = new Treatment { Mills = time - 120 * 60 * 1000, Insulin = 1.0 };

        var result = _iobService.CalcTreatment(treatment, time);

        var expectedIob = 1.0 * (0.001323 * 81.0 - 0.054233 * 9.0 + 0.55556);
        Assert.Equal(expectedIob, result.IobContrib, 5);
    }

    #endregion

    #region Helper Methods

    private static DeviceStatus CreateDeviceStatusForSource(string source, double iob, long? mills = null)
    {
        var deviceStatus = new DeviceStatus
        {
            Mills = mills ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Device = $"{source.ToLower()}://test",
        };

        switch (source)
        {
            case "Loop":
                deviceStatus.Loop = new LoopStatus
                {
                    Iob = new LoopIob { Iob = iob, Timestamp = DateTimeOffset.UtcNow.ToString("O") },
                };
                break;
            case "OpenAPS":
                deviceStatus.OpenAps = new OpenApsStatus
                {
                    Iob = new OpenApsIobData
                    {
                        Iob = iob, BasalIob = -0.298, Activity = 0.0147,
                        Timestamp = DateTimeOffset.UtcNow.ToString("O"),
                    },
                };
                break;
            case "MM Connect":
                deviceStatus.Pump = new PumpStatus { Iob = new PumpIob { BolusIob = iob } };
                deviceStatus.Connect = new object();
                break;
        }

        return deviceStatus;
    }

    #endregion
}
