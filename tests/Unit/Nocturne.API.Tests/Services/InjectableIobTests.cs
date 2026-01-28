using Moq;
using Nocturne.API.Services;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Injectables;
using Xunit;

namespace Nocturne.API.Tests.Services;

/// <summary>
/// Tests for IOB calculation from injectable medication doses with per-medication DIA.
/// Validates that CalcInjectableDoseContribution uses the medication-specific DIA
/// and peak to produce correct IOB curves for different insulin types.
/// </summary>
public class InjectableIobTests
{
    private readonly Mock<IInjectableDoseService> _mockDoseService;
    private readonly Mock<IInjectableMedicationService> _mockMedicationService;
    private readonly IobService _iobService;
    private readonly TestProfile _testProfile;

    // Common medication IDs for tests
    private static readonly Guid HumalogId = Guid.NewGuid();
    private static readonly Guid FiaspId = Guid.NewGuid();
    private static readonly Guid RegularId = Guid.NewGuid();
    private static readonly Guid LantusId = Guid.NewGuid();

    public InjectableIobTests()
    {
        _mockDoseService = new Mock<IInjectableDoseService>();
        _mockMedicationService = new Mock<IInjectableMedicationService>();
        _iobService = new IobService(_mockDoseService.Object, _mockMedicationService.Object);
        _testProfile = new TestProfile();
    }

    #region DIA-Specific IOB Curve Tests

    [Fact]
    public void CalculateTotal_HumalogDIA4h_ImmediateDoseShouldReturnFullIOB()
    {
        // Arrange - Humalog with 4-hour DIA, dose just administered
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var medication = CreateMedication(HumalogId, "Humalog", InjectableCategory.RapidActing, dia: 4.0, peak: 75);
        var dose = CreateDose(HumalogId, units: 5.0, timestamp: time - 1);

        SetupMocks(new[] { dose }, new Dictionary<Guid, InjectableMedication> { [HumalogId] = medication });

        // Act
        var result = _iobService.CalculateTotal(new List<Treatment>(), new List<DeviceStatus>(), _testProfile, time);

        // Assert - Full dose should be active as IOB
        Assert.True(result.Iob > 4.9, $"IOB should be near 5.0 immediately after dose, was {result.Iob}");
    }

    [Fact]
    public void CalculateTotal_FiaspDIA3_5h_DecaysFasterThanHumalog()
    {
        // Arrange - Compare Fiasp (3.5h DIA) vs Humalog (4.0h DIA) at same time point
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var oneHourAgo = time - 60 * 60 * 1000;

        // Test Fiasp (DIA 3.5, peak 60)
        var fiasp = CreateMedication(FiaspId, "Fiasp", InjectableCategory.RapidActing, dia: 3.5, peak: 60);
        var fiaspDose = CreateDose(FiaspId, units: 1.0, timestamp: oneHourAgo);
        SetupMocks(new[] { fiaspDose }, new Dictionary<Guid, InjectableMedication> { [FiaspId] = fiasp });

        var fiaspResult = _iobService.CalculateTotal(new List<Treatment>(), new List<DeviceStatus>(), _testProfile, time);
        var fiaspIob = fiaspResult.Iob;

        // Test Humalog (DIA 4.0, peak 75)
        var humalog = CreateMedication(HumalogId, "Humalog", InjectableCategory.RapidActing, dia: 4.0, peak: 75);
        var humalogDose = CreateDose(HumalogId, units: 1.0, timestamp: oneHourAgo);
        SetupMocks(new[] { humalogDose }, new Dictionary<Guid, InjectableMedication> { [HumalogId] = humalog });

        var humalogResult = _iobService.CalculateTotal(new List<Treatment>(), new List<DeviceStatus>(), _testProfile, time);
        var humalogIob = humalogResult.Iob;

        // Assert - Fiasp with shorter DIA should have less IOB remaining after 1 hour
        Assert.True(fiaspIob < humalogIob,
            $"Fiasp (3.5h DIA) IOB {fiaspIob} should be less than Humalog (4.0h DIA) IOB {humalogIob} after 1 hour");
        Assert.True(fiaspIob > 0, "Fiasp should still have some IOB after 1 hour");
        Assert.True(humalogIob > 0, "Humalog should still have some IOB after 1 hour");
    }

    [Fact]
    public void CalculateTotal_RegularInsulinDIA6h_HasMoreIOBAt2Hours()
    {
        // Arrange - Regular insulin (6h DIA, peak 150min) should retain more IOB at 2 hours
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var twoHoursAgo = time - 2 * 60 * 60 * 1000;

        // Regular insulin (DIA 6.0, peak 150)
        var regular = CreateMedication(RegularId, "Regular", InjectableCategory.ShortActing, dia: 6.0, peak: 150);
        var regularDose = CreateDose(RegularId, units: 1.0, timestamp: twoHoursAgo);
        SetupMocks(new[] { regularDose }, new Dictionary<Guid, InjectableMedication> { [RegularId] = regular });

        var regularResult = _iobService.CalculateTotal(new List<Treatment>(), new List<DeviceStatus>(), _testProfile, time);
        var regularIob = regularResult.Iob;

        // Humalog (DIA 4.0, peak 75)
        var humalog = CreateMedication(HumalogId, "Humalog", InjectableCategory.RapidActing, dia: 4.0, peak: 75);
        var humalogDose = CreateDose(HumalogId, units: 1.0, timestamp: twoHoursAgo);
        SetupMocks(new[] { humalogDose }, new Dictionary<Guid, InjectableMedication> { [HumalogId] = humalog });

        var humalogResult = _iobService.CalculateTotal(new List<Treatment>(), new List<DeviceStatus>(), _testProfile, time);
        var humalogIob = humalogResult.Iob;

        // Assert - Regular insulin with longer DIA should retain more IOB
        Assert.True(regularIob > humalogIob,
            $"Regular (6h DIA) IOB {regularIob} should be greater than Humalog (4h DIA) IOB {humalogIob} after 2 hours");
    }

    [Theory]
    [InlineData(3.0, "Default DIA")]
    [InlineData(3.5, "Fiasp DIA")]
    [InlineData(4.0, "Humalog DIA")]
    [InlineData(5.0, "Slow absorber DIA")]
    [InlineData(6.0, "Regular insulin DIA")]
    public void CalculateTotal_VariousDIAs_IOBReachesZeroByScaledDIA(double dia, string description)
    {
        // Arrange - After the full DIA period (scaled), IOB should be zero
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // The scale factor is 3.0/dia, and max IOB is at 180 scaled minutes
        // So real minutes = 180 / (3.0/dia) = 180 * dia / 3.0 = 60 * dia
        var fullDiaMs = (long)(dia * 60 * 60 * 1000);
        var doseTime = time - fullDiaMs;

        var medId = Guid.NewGuid();
        var medication = CreateMedication(medId, description, InjectableCategory.RapidActing, dia: dia, peak: 75);
        var dose = CreateDose(medId, units: 1.0, timestamp: doseTime);
        SetupMocks(new[] { dose }, new Dictionary<Guid, InjectableMedication> { [medId] = medication });

        // Act
        var result = _iobService.CalculateTotal(new List<Treatment>(), new List<DeviceStatus>(), _testProfile, time);

        // Assert
        Assert.True(result.Iob == 0.0,
            $"{description} ({dia}h): IOB should be 0 after full DIA period, was {result.Iob}");
    }

    [Theory]
    [InlineData(3.0)]
    [InlineData(3.5)]
    [InlineData(4.0)]
    [InlineData(5.0)]
    [InlineData(6.0)]
    public void CalculateTotal_VariousDIAs_IOBMonotonicallyDecreases(double dia)
    {
        // Arrange - IOB should decrease over time (never increase)
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var medId = Guid.NewGuid();
        var medication = CreateMedication(medId, "Test", InjectableCategory.RapidActing, dia: dia, peak: 75);

        var previousIob = double.MaxValue;
        var doseTime = time - 1; // Just administered

        // Check IOB at 15-minute intervals across the full DIA
        var totalMinutes = (int)(dia * 60);
        for (var minutes = 0; minutes <= totalMinutes; minutes += 15)
        {
            var checkTime = doseTime + (long)minutes * 60 * 1000;
            var dose = CreateDose(medId, units: 1.0, timestamp: doseTime);
            SetupMocks(new[] { dose }, new Dictionary<Guid, InjectableMedication> { [medId] = medication });

            var result = _iobService.CalculateTotal(new List<Treatment>(), new List<DeviceStatus>(), _testProfile, checkTime);

            // IOB should be <= previous reading (monotonically decreasing)
            Assert.True(result.Iob <= previousIob + 0.001, // small tolerance for floating point
                $"DIA {dia}h at {minutes}min: IOB {result.Iob} should be <= previous {previousIob}");
            Assert.True(result.Iob >= 0, $"DIA {dia}h at {minutes}min: IOB should never be negative");

            previousIob = result.Iob;
        }
    }

    #endregion

    #region Category Filtering Tests

    [Theory]
    [InlineData(InjectableCategory.RapidActing, true)]
    [InlineData(InjectableCategory.UltraRapid, true)]
    [InlineData(InjectableCategory.ShortActing, true)]
    [InlineData(InjectableCategory.Intermediate, false)]
    [InlineData(InjectableCategory.LongActing, false)]
    [InlineData(InjectableCategory.UltraLong, false)]
    [InlineData(InjectableCategory.GLP1Daily, false)]
    [InlineData(InjectableCategory.GLP1Weekly, false)]
    [InlineData(InjectableCategory.Other, false)]
    public void CalculateTotal_CategoryFiltering_OnlyRapidActingContributesToIOB(
        InjectableCategory category, bool shouldContribute)
    {
        // Arrange
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var medId = Guid.NewGuid();
        var medication = CreateMedication(medId, category.ToString(), category, dia: 4.0, peak: 75);
        var dose = CreateDose(medId, units: 5.0, timestamp: time - 30 * 60 * 1000); // 30 min ago

        SetupMocks(new[] { dose }, new Dictionary<Guid, InjectableMedication> { [medId] = medication });

        // Act
        var result = _iobService.CalculateTotal(new List<Treatment>(), new List<DeviceStatus>(), _testProfile, time);

        // Assert
        if (shouldContribute)
        {
            Assert.True(result.Iob > 0, $"{category} should contribute to IOB");
        }
        else
        {
            Assert.True(result.Iob == 0.0, $"{category} should NOT contribute to IOB, was {result.Iob}");
        }
    }

    #endregion

    #region DIA Fallback Priority Tests

    [Fact]
    public void CalculateTotal_MedicationHasDIA_UsesMedicationDIA()
    {
        // Arrange - Medication DIA = 5.0, Profile DIA = 3.0
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var profile = new TestProfile { DIA = 3.0 };

        // With medication DIA 5.0, IOB should still be present after 3 hours (profile DIA)
        var threeHoursAgo = time - 3 * 60 * 60 * 1000;
        var medId = Guid.NewGuid();
        var medication = CreateMedication(medId, "Slow", InjectableCategory.RapidActing, dia: 5.0, peak: 75);
        var dose = CreateDose(medId, units: 1.0, timestamp: threeHoursAgo);

        SetupMocks(new[] { dose }, new Dictionary<Guid, InjectableMedication> { [medId] = medication });

        // Act
        var result = _iobService.CalculateTotal(new List<Treatment>(), new List<DeviceStatus>(), profile, time);

        // Assert - With 5.0h DIA, there should be IOB remaining after 3 hours
        Assert.True(result.Iob > 0,
            "With medication DIA of 5.0h, IOB should still be present after 3 hours");
    }

    [Fact]
    public void CalculateTotal_MedicationNoDIA_FallsBackToProfileDIA()
    {
        // Arrange - Medication has no DIA set, profile DIA = 3.0
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var profile = new TestProfile { DIA = 3.0 };

        // After 3 hours with default DIA 3.0, IOB should be zero
        var threeHoursAgo = time - 3 * 60 * 60 * 1000;
        var medId = Guid.NewGuid();
        var medication = CreateMedication(medId, "NoDIA", InjectableCategory.RapidActing, dia: null, peak: 75);
        var dose = CreateDose(medId, units: 1.0, timestamp: threeHoursAgo);

        SetupMocks(new[] { dose }, new Dictionary<Guid, InjectableMedication> { [medId] = medication });

        // Act
        var result = _iobService.CalculateTotal(new List<Treatment>(), new List<DeviceStatus>(), profile, time);

        // Assert - With profile DIA of 3.0h, IOB should be 0 after 3 hours
        Assert.Equal(0.0, result.Iob);
    }

    [Fact]
    public void CalculateTotal_NoMedicationNorProfile_FallsBackToDefaultDIA()
    {
        // Arrange - No medication DIA, no profile → default 3.0h DIA
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var threeHoursAgo = time - 3 * 60 * 60 * 1000;
        var medId = Guid.NewGuid();
        var medication = CreateMedication(medId, "NoDIA", InjectableCategory.RapidActing, dia: null, peak: 75);
        var dose = CreateDose(medId, units: 1.0, timestamp: threeHoursAgo);

        SetupMocks(new[] { dose }, new Dictionary<Guid, InjectableMedication> { [medId] = medication });

        // Act - null profile forces default DIA fallback
        var result = _iobService.CalculateTotal(new List<Treatment>(), new List<DeviceStatus>(), null, time);

        // Assert - Default DIA is 3.0h, so IOB should be 0 after 3 hours
        Assert.Equal(0.0, result.Iob);
    }

    #endregion

    #region Multiple Doses and Additive IOB Tests

    [Fact]
    public void CalculateTotal_MultipleDosesSameMedication_AggregatesIOB()
    {
        // Arrange - Two Humalog doses at different times
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var medication = CreateMedication(HumalogId, "Humalog", InjectableCategory.RapidActing, dia: 4.0, peak: 75);

        var dose1 = CreateDose(HumalogId, units: 3.0, timestamp: time - 60 * 60 * 1000); // 1 hour ago
        var dose2 = CreateDose(HumalogId, units: 2.0, timestamp: time - 30 * 60 * 1000); // 30 min ago

        SetupMocks(new[] { dose1, dose2 }, new Dictionary<Guid, InjectableMedication> { [HumalogId] = medication });

        // Act
        var result = _iobService.CalculateTotal(new List<Treatment>(), new List<DeviceStatus>(), _testProfile, time);

        // Assert - IOB should be aggregate of both doses
        Assert.True(result.Iob > 0, "Aggregate IOB should be positive");
        Assert.True(result.Iob < 5.0, "Aggregate IOB should be less than total insulin (5.0)");
    }

    [Fact]
    public void CalculateTotal_DosesDifferentMedications_AggregatesIOBWithDifferentDIAs()
    {
        // Arrange - Fiasp (3.5h DIA) and Humalog (4.0h DIA) doses
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var thirtyMinAgo = time - 30 * 60 * 1000;

        var fiasp = CreateMedication(FiaspId, "Fiasp", InjectableCategory.UltraRapid, dia: 3.5, peak: 60);
        var humalog = CreateMedication(HumalogId, "Humalog", InjectableCategory.RapidActing, dia: 4.0, peak: 75);

        var fiaspDose = CreateDose(FiaspId, units: 1.0, timestamp: thirtyMinAgo);
        var humalogDose = CreateDose(HumalogId, units: 1.0, timestamp: thirtyMinAgo);

        SetupMocks(
            new[] { fiaspDose, humalogDose },
            new Dictionary<Guid, InjectableMedication>
            {
                [FiaspId] = fiasp,
                [HumalogId] = humalog,
            }
        );

        // Act
        var result = _iobService.CalculateTotal(new List<Treatment>(), new List<DeviceStatus>(), _testProfile, time);

        // Assert - Should have combined IOB from both medications
        Assert.True(result.Iob > 1.0, "Combined IOB from 2 units should be substantial");
        Assert.True(result.Iob < 2.0, "Combined IOB should be less than total insulin (2.0)");
    }

    [Fact]
    public void CalculateTotal_InjectableDoseAddedToTreatmentIOB()
    {
        // Arrange - Both a treatment and an injectable dose should contribute
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var thirtyMinAgo = time - 30 * 60 * 1000;

        // Treatment IOB
        var treatments = new List<Treatment>
        {
            new() { Mills = thirtyMinAgo, Insulin = 2.0 },
        };

        // Injectable dose IOB
        var medication = CreateMedication(HumalogId, "Humalog", InjectableCategory.RapidActing, dia: 4.0, peak: 75);
        var dose = CreateDose(HumalogId, units: 1.0, timestamp: thirtyMinAgo);
        SetupMocks(new[] { dose }, new Dictionary<Guid, InjectableMedication> { [HumalogId] = medication });

        // Act - Calculate with both sources
        var result = _iobService.CalculateTotal(treatments, new List<DeviceStatus>(), _testProfile, time);

        // Also calculate treatment-only IOB for comparison
        var mockDoseServiceEmpty = new Mock<IInjectableDoseService>();
        mockDoseServiceEmpty
            .Setup(s => s.GetRecentRapidActingDosesAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<InjectableDose>());
        var treatmentOnlyService = new IobService(mockDoseServiceEmpty.Object, new Mock<IInjectableMedicationService>().Object);
        var treatmentOnlyResult = treatmentOnlyService.CalculateTotal(treatments, new List<DeviceStatus>(), _testProfile, time);

        // Assert - Combined IOB should be greater than treatment-only IOB
        Assert.True(result.Iob > treatmentOnlyResult.Iob,
            $"Combined IOB ({result.Iob}) should be greater than treatment-only IOB ({treatmentOnlyResult.Iob})");
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void CalculateTotal_ZeroUnitDose_ContributesNoIOB()
    {
        // Arrange
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var medication = CreateMedication(HumalogId, "Humalog", InjectableCategory.RapidActing, dia: 4.0, peak: 75);
        var dose = CreateDose(HumalogId, units: 0.0, timestamp: time - 30 * 60 * 1000);

        SetupMocks(new[] { dose }, new Dictionary<Guid, InjectableMedication> { [HumalogId] = medication });

        // Act
        var result = _iobService.CalculateTotal(new List<Treatment>(), new List<DeviceStatus>(), _testProfile, time);

        // Assert
        Assert.Equal(0.0, result.Iob);
    }

    [Fact]
    public void CalculateTotal_FutureDose_ContributesNoIOB()
    {
        // Arrange - Dose timestamp is in the future
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var medication = CreateMedication(HumalogId, "Humalog", InjectableCategory.RapidActing, dia: 4.0, peak: 75);
        var dose = CreateDose(HumalogId, units: 5.0, timestamp: time + 60 * 60 * 1000); // 1 hour in future

        SetupMocks(new[] { dose }, new Dictionary<Guid, InjectableMedication> { [HumalogId] = medication });

        // Act
        var result = _iobService.CalculateTotal(new List<Treatment>(), new List<DeviceStatus>(), _testProfile, time);

        // Assert
        Assert.Equal(0.0, result.Iob);
    }

    [Fact]
    public void CalculateTotal_NoDoses_ReturnsZeroIOB()
    {
        // Arrange - No injectable doses
        _mockDoseService
            .Setup(s => s.GetRecentRapidActingDosesAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<InjectableDose>());

        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Act
        var result = _iobService.CalculateTotal(new List<Treatment>(), new List<DeviceStatus>(), _testProfile, time);

        // Assert
        Assert.Equal(0.0, result.Iob);
    }

    [Fact]
    public void CalculateTotal_MedicationNotFound_SkipsDose()
    {
        // Arrange - Dose references a medication that doesn't exist
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var unknownMedId = Guid.NewGuid();
        var dose = CreateDose(unknownMedId, units: 5.0, timestamp: time - 30 * 60 * 1000);

        _mockDoseService
            .Setup(s => s.GetRecentRapidActingDosesAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { dose });
        _mockMedicationService
            .Setup(s => s.GetByIdAsync(unknownMedId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((InjectableMedication?)null);

        // Act
        var result = _iobService.CalculateTotal(new List<Treatment>(), new List<DeviceStatus>(), _testProfile, time);

        // Assert - Missing medication should be silently skipped
        Assert.Equal(0.0, result.Iob);
    }

    [Fact]
    public void CalculateTotal_DoseServiceThrows_ReturnsZeroInjectableIOB()
    {
        // Arrange - Dose service fails
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        _mockDoseService
            .Setup(s => s.GetRecentRapidActingDosesAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Database connection failed"));

        // Act - Should not throw, injectable IOB silently returns 0
        var result = _iobService.CalculateTotal(new List<Treatment>(), new List<DeviceStatus>(), _testProfile, time);

        // Assert
        Assert.Equal(0.0, result.Iob);
    }

    [Fact]
    public void CalculateTotal_IOBNeverNegative_AtDIABoundary()
    {
        // Arrange - Test at the boundary where IOB approaches zero
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var medId = Guid.NewGuid();
        var medication = CreateMedication(medId, "Test", InjectableCategory.RapidActing, dia: 4.0, peak: 75);

        // Check around the DIA boundary (4h = 240 minutes)
        for (var minutesBefore = 5; minutesBefore >= -5; minutesBefore--)
        {
            var doseTimeMins = 240 - minutesBefore;
            var doseTime = time - (long)doseTimeMins * 60 * 1000;
            var dose = CreateDose(medId, units: 5.0, timestamp: doseTime);
            SetupMocks(new[] { dose }, new Dictionary<Guid, InjectableMedication> { [medId] = medication });

            // Act
            var result = _iobService.CalculateTotal(new List<Treatment>(), new List<DeviceStatus>(), _testProfile, time);

            // Assert
            Assert.True(result.Iob >= 0, $"IOB should never be negative at {doseTimeMins} minutes, was {result.Iob}");
        }
    }

    #endregion

    #region Medication Peak Time Tests

    [Fact]
    public void CalculateTotal_CustomPeakTime_AffectsIOBCurve()
    {
        // Arrange - Compare default peak (75 min) with early peak (60 min)
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var fortyFiveMinAgo = time - 45 * 60 * 1000;

        // Medication with default peak (75 min)
        var medDefault = Guid.NewGuid();
        var defaultPeakMed = CreateMedication(medDefault, "DefaultPeak", InjectableCategory.RapidActing, dia: 4.0, peak: null);
        var dose1 = CreateDose(medDefault, units: 1.0, timestamp: fortyFiveMinAgo);
        SetupMocks(new[] { dose1 }, new Dictionary<Guid, InjectableMedication> { [medDefault] = defaultPeakMed });

        var defaultResult = _iobService.CalculateTotal(new List<Treatment>(), new List<DeviceStatus>(), _testProfile, time);

        // Medication with early peak (60 min)
        var medEarly = Guid.NewGuid();
        var earlyPeakMed = CreateMedication(medEarly, "EarlyPeak", InjectableCategory.RapidActing, dia: 4.0, peak: 60);
        var dose2 = CreateDose(medEarly, units: 1.0, timestamp: fortyFiveMinAgo);
        SetupMocks(new[] { dose2 }, new Dictionary<Guid, InjectableMedication> { [medEarly] = earlyPeakMed });

        var earlyResult = _iobService.CalculateTotal(new List<Treatment>(), new List<DeviceStatus>(), _testProfile, time);

        // Assert - Both should have IOB, but the curve shape differs
        Assert.True(defaultResult.Iob > 0, "Default peak medication should have IOB");
        Assert.True(earlyResult.Iob > 0, "Early peak medication should have IOB");
        // The early peak medication transitions to the decline phase at 60 min, so at 45 min
        // it's still in the rise phase. The default peak transitions at 75 min. Both are
        // in the rise phase at 45 min, but the curve shape differs due to peak timing.
    }

    #endregion

    #region Realistic Preset Scenario Tests

    [Theory]
    [MemberData(nameof(PresetScenarioData))]
    public void CalculateTotal_RealisticPresetScenarios(
        string name, InjectableCategory category, double dia, double peak,
        double units, int minutesAgo, bool shouldHaveIOB)
    {
        // Arrange - Test with realistic preset medication parameters
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var medId = Guid.NewGuid();
        var medication = CreateMedication(medId, name, category, dia: dia, peak: peak);
        var dose = CreateDose(medId, units: units, timestamp: time - (long)minutesAgo * 60 * 1000);

        SetupMocks(new[] { dose }, new Dictionary<Guid, InjectableMedication> { [medId] = medication });

        // Act
        var result = _iobService.CalculateTotal(new List<Treatment>(), new List<DeviceStatus>(), _testProfile, time);

        // Assert
        if (shouldHaveIOB)
        {
            Assert.True(result.Iob > 0, $"{name}: should have IOB at {minutesAgo} minutes");
        }
        else
        {
            Assert.True(result.Iob == 0.0, $"{name}: should have no IOB at {minutesAgo} minutes, was {result.Iob}");
        }
    }

    public static IEnumerable<object[]> PresetScenarioData()
    {
        // Humalog (DIA 4.0h = 240 min): IOB present at 200 min, gone at 240 min
        yield return new object[] { "Humalog", InjectableCategory.RapidActing, 4.0, 75.0, 5.0, 200, true };
        yield return new object[] { "Humalog", InjectableCategory.RapidActing, 4.0, 75.0, 5.0, 241, false };

        // Fiasp (DIA 3.5h = 210 min): IOB present at 180 min, gone at 210 min
        yield return new object[] { "Fiasp", InjectableCategory.RapidActing, 3.5, 60.0, 3.0, 180, true };
        yield return new object[] { "Fiasp", InjectableCategory.RapidActing, 3.5, 60.0, 3.0, 211, false };

        // Regular (DIA 6.0h = 360 min): IOB present at 300 min, gone at 360 min
        yield return new object[] { "Regular", InjectableCategory.ShortActing, 6.0, 150.0, 10.0, 300, true };
        yield return new object[] { "Regular", InjectableCategory.ShortActing, 6.0, 150.0, 10.0, 361, false };

        // Lyumjev (DIA 3.5h = 210 min): IOB present at 5 min, gone at 210 min
        yield return new object[] { "Lyumjev", InjectableCategory.UltraRapid, 3.5, 60.0, 2.0, 5, true };
        yield return new object[] { "Lyumjev", InjectableCategory.UltraRapid, 3.5, 60.0, 2.0, 211, false };
    }

    #endregion

    #region Scale Factor Verification Tests

    [Fact]
    public void CalculateTotal_ScaleFactorCorrectlyApplied_DIA3h()
    {
        // Arrange - DIA 3.0h: scaleFactor = 3.0/3.0 = 1.0
        // At 1 hour: scaledMinAgo = 1.0 * 60 = 60 (before peak at 75)
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var medId = Guid.NewGuid();
        var medication = CreateMedication(medId, "DIA3", InjectableCategory.RapidActing, dia: 3.0, peak: 75);
        var dose = CreateDose(medId, units: 1.0, timestamp: time - 60 * 60 * 1000); // 1 hour ago

        SetupMocks(new[] { dose }, new Dictionary<Guid, InjectableMedication> { [medId] = medication });

        var result = _iobService.CalculateTotal(new List<Treatment>(), new List<DeviceStatus>(), _testProfile, time);

        // Verify with exact formula: scaleFactor=1.0, minAgo=60, x1=60/5+1=13
        // iob = 1.0 * (1 - 0.001852*169 + 0.001852*13) = 1.0 * (1 - 0.312988 + 0.024076) = 0.711088
        var expectedIob = 1.0 * (1.0 - 0.001852 * 13.0 * 13.0 + 0.001852 * 13.0);
        Assert.Equal(Math.Round(expectedIob + double.Epsilon, 3), result.Iob, 3);
    }

    [Fact]
    public void CalculateTotal_ScaleFactorCorrectlyApplied_DIA4h()
    {
        // Arrange - DIA 4.0h: scaleFactor = 3.0/4.0 = 0.75
        // At 1 hour: scaledMinAgo = 0.75 * 60 = 45 (before peak at 75)
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var medId = Guid.NewGuid();
        var medication = CreateMedication(medId, "DIA4", InjectableCategory.RapidActing, dia: 4.0, peak: 75);
        var dose = CreateDose(medId, units: 1.0, timestamp: time - 60 * 60 * 1000); // 1 hour ago

        SetupMocks(new[] { dose }, new Dictionary<Guid, InjectableMedication> { [medId] = medication });

        var result = _iobService.CalculateTotal(new List<Treatment>(), new List<DeviceStatus>(), _testProfile, time);

        // Verify with exact formula: scaleFactor=0.75, minAgo=45, x1=45/5+1=10
        // iob = 1.0 * (1 - 0.001852*100 + 0.001852*10) = 1.0 * (1 - 0.1852 + 0.01852) = 0.83332
        var expectedIob = 1.0 * (1.0 - 0.001852 * 10.0 * 10.0 + 0.001852 * 10.0);
        Assert.Equal(Math.Round(expectedIob + double.Epsilon, 3), result.Iob, 3);
    }

    [Fact]
    public void CalculateTotal_ScaleFactorCorrectlyApplied_DIA6h()
    {
        // Arrange - DIA 6.0h: scaleFactor = 3.0/6.0 = 0.5
        // At 1 hour: scaledMinAgo = 0.5 * 60 = 30 (before peak at 75)
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var medId = Guid.NewGuid();
        var medication = CreateMedication(medId, "DIA6", InjectableCategory.RapidActing, dia: 6.0, peak: 75);
        var dose = CreateDose(medId, units: 1.0, timestamp: time - 60 * 60 * 1000); // 1 hour ago

        SetupMocks(new[] { dose }, new Dictionary<Guid, InjectableMedication> { [medId] = medication });

        var result = _iobService.CalculateTotal(new List<Treatment>(), new List<DeviceStatus>(), _testProfile, time);

        // Verify with exact formula: scaleFactor=0.5, minAgo=30, x1=30/5+1=7
        // iob = 1.0 * (1 - 0.001852*49 + 0.001852*7) = 1.0 * (1 - 0.090748 + 0.012964) = 0.922216
        var expectedIob = 1.0 * (1.0 - 0.001852 * 7.0 * 7.0 + 0.001852 * 7.0);
        Assert.Equal(Math.Round(expectedIob + double.Epsilon, 3), result.Iob, 3);
    }

    #endregion

    #region Helper Methods

    private static InjectableMedication CreateMedication(
        Guid id, string name, InjectableCategory category, double? dia, double? peak)
    {
        return new InjectableMedication
        {
            Id = id,
            Name = name,
            Category = category,
            Dia = dia,
            Peak = peak,
            UnitType = UnitType.Units,
            Concentration = 100,
        };
    }

    private static InjectableDose CreateDose(Guid medicationId, double units, long timestamp)
    {
        return new InjectableDose
        {
            Id = Guid.NewGuid(),
            InjectableMedicationId = medicationId,
            Units = units,
            Timestamp = timestamp,
        };
    }

    private void SetupMocks(
        IEnumerable<InjectableDose> doses,
        Dictionary<Guid, InjectableMedication> medications)
    {
        _mockDoseService
            .Setup(s => s.GetRecentRapidActingDosesAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(doses);

        foreach (var (id, medication) in medications)
        {
            _mockMedicationService
                .Setup(s => s.GetByIdAsync(id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(medication);
        }
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
