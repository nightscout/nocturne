using Nocturne.API.Services;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services;

/// <summary>
/// Complete IOB calculation tests with 1:1 legacy JavaScript compatibility
/// Tests exact algorithms from ClientApp/mocha-tests/iob.test.js
/// NO SIMPLIFICATIONS - Must match legacy behavior exactly
/// </summary>
[Parity("iob.test.js")]
public class IobServiceTests
{
    private readonly IobService _iobService;
    private readonly TestProfile _testProfile;

    public IobServiceTests()
    {
        _iobService = new IobService();
        _testProfile = new TestProfile();
    }

    [Fact]
    public void CalcTreatment_SingleBolusRightAfter_ShouldReturn1Point00IOB()
    {
        // Arrange - Exact test data from legacy iob.test.js
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatment = new Treatment
        {
            Mills = time - 1, // 1ms ago
            Insulin = 1.0,
        };

        // Act
        var result = _iobService.CalcTreatment(treatment, _testProfile, time);

        // Assert - Must match legacy exactly: "rightAfterBolus.display.should.equal("1.00");"
        Assert.Equal(1.0, result.IobContrib, 2);
    }

    [Fact]
    public void CalcTreatment_After1Hour_ShouldHaveLessIOBThan1()
    {
        // Arrange - Test from legacy: afterSomeTime.iob.should.be.lessThan(1);
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatment = new Treatment { Mills = time - 1, Insulin = 1.0 };

        // Act - 1 hour later
        var result = _iobService.CalcTreatment(treatment, _testProfile, time + 60 * 60 * 1000);

        // Assert
        Assert.True(result.IobContrib < 1.0);
        Assert.True(result.IobContrib > 0.0);
    }

    [Fact]
    public void CalcTreatment_After3Hours_ShouldHaveZeroIOB()
    {
        // Arrange - Test from legacy: afterDIA.iob.should.equal(0);
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatment = new Treatment { Mills = time - 1, Insulin = 1.0 };

        // Act - 3 hours later (DIA complete)
        var result = _iobService.CalcTreatment(treatment, _testProfile, time + 3 * 60 * 60 * 1000);

        // Assert
        Assert.Equal(0.0, result.IobContrib, 3);
    }

    [Fact]
    public void CalcTreatment_NoNegativeIOB_WhenApproachingZero()
    {
        // Arrange - Test from legacy: should not show a negative IOB when approaching 0
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatment = new Treatment { Mills = time, Insulin = 5.0 };

        // Act - At the exact moment IOB approaches 0
        var result = _iobService.CalcTreatment(
            treatment,
            _testProfile,
            time + 3 * 60 * 60 * 1000 - 90 * 1000
        );

        // Assert - Before fix we got: AssertionError: expected '-0.00' to be '0.00'
        Assert.True(result.IobContrib >= 0.0);
    }

    [Fact]
    public void CalcTreatment_4HourDIA_ShouldUseCorrectDuration()
    {
        // Arrange - Test from legacy: should calculate IOB using a 4 hour duration
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var profile = new TestProfile { DIA = 4.0 };
        var treatment = new Treatment { Mills = time - 1, Insulin = 1.0 };

        // Act - Right after bolus
        var rightAfter = _iobService.CalcTreatment(treatment, profile, time);

        // Act - After 1 hour with 4-hour DIA
        var afterHour = _iobService.CalcTreatment(treatment, profile, time + 60 * 60 * 1000);

        // Assert
        Assert.Equal(1.0, rightAfter.IobContrib, 2);
        Assert.True(afterHour.IobContrib > 0.5); // Should have more IOB remaining with longer DIA
    }

    [Fact]
    public void FromTreatments_MultipleTreatments_ShouldAggregateCorrectly()
    {
        // Arrange
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>
        {
            new() { Mills = time - 60 * 60 * 1000, Insulin = 2.0 }, // 1 hour ago
            new() { Mills = time - 30 * 60 * 1000, Insulin = 1.5 }, // 30 min ago
            new() { Mills = time - 10 * 60 * 1000, Insulin = 1.0 }, // 10 min ago
        };

        // Act
        var result = _iobService.FromTreatments(treatments, _testProfile, time);

        // Assert
        Assert.True(result.Iob > 0);
        Assert.True(result.Iob < 4.5); // Less than total insulin due to decay
        Assert.Equal("Care Portal", result.Source);
    }

    [Fact]
    public void FromTreatments_WithBasalTreatments_ShouldCalculateBasalIOB()
    {
        // Arrange
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>
        {
            new()
            {
                Mills = time - 60 * 60 * 1000,
                EventType = "Temp Basal",
                Absolute = 1.5, // 1.5 U/hr
                Duration = 120, // 2 hours
            },
        };

        // Act
        var result = _iobService.FromTreatments(treatments, _testProfile, time);

        // Assert
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
        // Arrange
        var deviceStatus = CreateDeviceStatusForSource(expectedSource, expectedIOB);

        // Act
        var result = _iobService.FromDeviceStatus(deviceStatus);

        // Assert
        Assert.Equal(expectedSource, result.Source);
        Assert.Equal(expectedIOB, result.Iob, 3);
    }

    [Fact]
    public void LastIobDeviceStatus_PrioritizesLoopOverOpenAPS()
    {
        // Arrange
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var deviceStatuses = new List<DeviceStatus>
        {
            CreateDeviceStatusForSource("OpenAPS", 0.5, time - 5 * 60 * 1000),
            CreateDeviceStatusForSource("Loop", 0.75, time - 10 * 60 * 1000),
        };

        // Act
        var result = _iobService.LastIobDeviceStatus(deviceStatuses, time);

        // Assert - Should prefer Loop even if older (within recency threshold)
        Assert.Equal("Loop", result.Source);
        Assert.Equal(0.75, result.Iob);
    }

    [Fact]
    public void LastIobDeviceStatus_IgnoresStaleData()
    {
        // Arrange
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var staleTime = time - 31 * 60 * 1000; // 31 minutes ago (beyond 30min threshold)
        var deviceStatuses = new List<DeviceStatus>
        {
            CreateDeviceStatusForSource("Loop", 0.75, staleTime),
        };

        // Act
        var result = _iobService.LastIobDeviceStatus(deviceStatuses, time);

        // Assert
        Assert.Equal(0.0, result.Iob);
    }

    [Fact]
    public void CalculateTotal_CombinesDeviceStatusAndTreatments()
    {
        // Arrange
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>
        {
            new() { Mills = time - 60 * 60 * 1000, Insulin = 1.0 },
        };
        var deviceStatuses = new List<DeviceStatus>
        {
            CreateDeviceStatusForSource("Loop", 1.5, time - 5 * 60 * 1000),
        };

        // Act
        var result = _iobService.CalculateTotal(treatments, deviceStatuses, _testProfile, time);

        // Assert
        Assert.Equal(1.5, result.Iob); // Device status takes priority
        Assert.True(result.TreatmentIob.HasValue); // Treatment IOB should be tracked separately
        Assert.Equal("Loop", result.Source);
    }

    [Fact]
    public void CalculateTotal_FallsBackToTreatments_WhenNoDeviceStatus()
    {
        // Arrange
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>
        {
            new() { Mills = time - 30 * 60 * 1000, Insulin = 2.0 },
        };

        // Act
        var result = _iobService.CalculateTotal(
            treatments,
            new List<DeviceStatus>(),
            _testProfile,
            time
        );

        // Assert
        Assert.True(result.Iob > 0);
        Assert.Equal("Care Portal", result.Source);
    }

    #region Exact Legacy Test Cases

    [Fact]
    public void IOB_ExactLegacyTestCase_100mgdl_1UnitIOB()
    {
        // Arrange - Exact test from boluswizardpreview.test.js
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>
        {
            new() { Mills = time, Insulin = 1.0 },
        };
        var profile = new TestProfile { DIA = 3, Sensitivity = 50 };

        // Act
        var result = _iobService.FromTreatments(treatments, profile, time);

        // Assert - Effect should be 50 mg/dL (1U * 50 ISF)
        Assert.Equal(1.0, result.Iob, 2);

        // Activity calculation: sens * insulin * (2 / dia / 60 / peak) * minAgo
        // At time=0, minAgo=0, so activity should be 0
        Assert.Equal(0.0, result.Activity ?? 0.0, 3);
    }

    [Fact]
    public void IOB_ExactPolynomialCurve_BeforePeak()
    {
        // Arrange - Test exact polynomial curve before 75-minute peak
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatment = new Treatment
        {
            Mills = time - 30 * 60 * 1000, // 30 minutes ago
            Insulin = 1.0,
        };
        var profile = new TestProfile { DIA = 3 };

        // Act
        var result = _iobService.CalcTreatment(treatment, profile, time);

        // Assert - Apply exact legacy formula: 1 - 0.001852 * x1^2 + 0.001852 * x1
        // scaleFactor = 3.0 / 3 = 1.0
        // minAgo = 1.0 * 30 = 30
        // x1 = 30/5 + 1 = 7
        // expected = 1.0 * (1 - 0.001852 * 49 + 0.001852 * 7) = 1.0 * (1 - 0.090748 + 0.012964) = 0.922216
        var expectedIob = 1.0 * (1.0 - 0.001852 * 49.0 + 0.001852 * 7.0);
        Assert.Equal(expectedIob, result.IobContrib, 5);
    }

    [Fact]
    public void IOB_ExactPolynomialCurve_AfterPeak()
    {
        // Arrange - Test exact polynomial curve after 75-minute peak
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatment = new Treatment
        {
            Mills = time - 120 * 60 * 1000, // 120 minutes ago
            Insulin = 1.0,
        };
        var profile = new TestProfile { DIA = 3 };

        // Act
        var result = _iobService.CalcTreatment(treatment, profile, time);

        // Assert - Apply exact legacy formula: 0.001323 * x2^2 - 0.054233 * x2 + 0.55556
        // minAgo = 120, x2 = (120 - 75) / 5 = 9
        // expected = 1.0 * (0.001323 * 81 - 0.054233 * 9 + 0.55556) = 1.0 * (0.107163 - 0.488097 + 0.55556) = 0.174626
        var expectedIob = 1.0 * (0.001323 * 81.0 - 0.054233 * 9.0 + 0.55556);
        Assert.Equal(expectedIob, result.IobContrib, 5);
    }

    #endregion

    #region Helper Methods

    private static DeviceStatus CreateDeviceStatusForSource(
        string source,
        double iob,
        long? mills = null
    )
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
                    Iob = new LoopIob
                    {
                        Iob = iob,
                        Timestamp = DateTimeOffset.UtcNow.ToString("O"),
                    },
                };
                break;

            case "OpenAPS":
                deviceStatus.OpenAps = new OpenApsStatus
                {
                    Iob = new OpenApsIobData
                    {
                        Iob = iob,
                        BasalIob = -0.298,
                        Activity = 0.0147,
                        Timestamp = DateTimeOffset.UtcNow.ToString("O"),
                    },
                };
                break;

            case "MM Connect":
                deviceStatus.Pump = new PumpStatus { Iob = new PumpIob { BolusIob = iob } };
                deviceStatus.Connect = new object(); // Indicates MM Connect
                break;
        }

        return deviceStatus;
    }

    #endregion

    #region Test Profile Implementation

    private class TestProfile : IProfileService
    {
        public double DIA { get; set; } = 3.0;
        public double Sensitivity { get; set; } = 95.0;
        public double BasalRate { get; set; } = 1.0;
        public double CarbRatio { get; set; } = 10.0;
        public double CarbAbsorptionRate { get; set; } = 30.0;
        public double LowBGTarget { get; set; } = 80.0;
        public double HighBGTarget { get; set; } = 120.0;

        public double GetDIA(long time, string? specProfile = null) => DIA;
        public double GetSensitivity(long time, string? specProfile = null) => Sensitivity;
        public double GetBasalRate(long time, string? specProfile = null) => BasalRate;
        public double GetCarbRatio(long time, string? specProfile = null) => CarbRatio;
        public double GetCarbAbsorptionRate(long time, string? specProfile = null) => CarbAbsorptionRate;
        public double GetLowBGTarget(long time, string? specProfile = null) => LowBGTarget;
        public double GetHighBGTarget(long time, string? specProfile = null) => HighBGTarget;
        public double GetValueByTime(long time, string valueType, string? specProfile = null) => 0.0;

        // Stub implementations for interface compliance
        public void LoadData(List<Profile> profileData) { }
        public bool HasData() => true;
        public void Clear() { }
        public Profile? GetCurrentProfile(long? time = null, string? specProfile = null) => null;
        public string? GetActiveProfileName(long? time = null) => "Default";
        public List<string> ListBasalProfiles() => new() { "Default" };
        public string? GetUnits(string? specProfile = null) => "mg/dl";
        public string? GetTimezone(string? specProfile = null) => "UTC";
        public void UpdateTreatments(List<Treatment>? profileTreatments = null, List<Treatment>? tempBasalTreatments = null, List<Treatment>? comboBolusTreatments = null) { }
        public Treatment? GetActiveProfileTreatment(long time) => null;
        public Treatment? GetTempBasalTreatment(long time) => null;
        public Treatment? GetComboBolusTreatment(long time) => null;
        public TempBasalResult GetTempBasal(long time, string? specProfile = null) => new();
    }

    #endregion
}
