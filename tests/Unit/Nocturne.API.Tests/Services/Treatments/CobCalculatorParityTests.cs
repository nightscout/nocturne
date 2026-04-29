using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Treatments;

/// <summary>
/// Parity tests proving <see cref="CobCalculator"/> produces identical results to
/// the legacy CobService for the same inputs. Every assertion value is copied verbatim
/// from CobServiceTests and CobTests — if a value differs, the math changed.
/// </summary>
/// <remarks>
/// Tests that relied on <c>ApplyAdvancedAbsorptionAdjustments</c> (fat/notes-based adjustments)
/// are intentionally omitted because <see cref="CobCalculator"/> does not implement those
/// adjustments. This is expected and correct — the removed behavior was never in the legacy
/// JavaScript implementation.
/// </remarks>
public class CobCalculatorParityTests
{
    private readonly CobCalculator _calculator;
    private readonly Mock<IApsSnapshotRepository> _apsSnapshotRepo;

    private const double DefaultCarbAbsorptionRate = 30.0;
    private const double DefaultSensitivity = 50.0;
    private const double DefaultCarbRatio = 18.0;
    private const double DefaultDIA = 3.0;
    private const double DefaultBasalRate = 1.0;

    public CobCalculatorParityTests()
    {
        var logger = new Mock<ILogger<CobCalculator>>();

        var sensitivityResolver = new Mock<ISensitivityResolver>();
        sensitivityResolver
            .Setup(s => s.GetSensitivityAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultSensitivity);

        var carbRatioResolver = new Mock<ICarbRatioResolver>();
        carbRatioResolver
            .Setup(c => c.GetCarbRatioAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultCarbRatio);

        var therapySettings = new Mock<ITherapySettingsResolver>();
        therapySettings
            .Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        therapySettings
            .Setup(t => t.GetCarbAbsorptionRateAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultCarbAbsorptionRate);
        therapySettings
            .Setup(t => t.GetDIAAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultDIA);

        _apsSnapshotRepo = new Mock<IApsSnapshotRepository>();
        _apsSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<ApsSnapshot>());

        // Real IobCalculator (not mocked) — COB calculation calls IOB internally for activity
        var basalRateResolver = new Mock<IBasalRateResolver>();
        basalRateResolver
            .Setup(b => b.GetBasalRateAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultBasalRate);

        var iobApsRepo = new Mock<IApsSnapshotRepository>();
        iobApsRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<ApsSnapshot>());

        var pumpRepo = new Mock<IPumpSnapshotRepository>();
        pumpRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<PumpSnapshot>());

        var iobCalculator = new IobCalculator(
            therapySettings.Object,
            sensitivityResolver.Object,
            basalRateResolver.Object,
            iobApsRepo.Object,
            pumpRepo.Object
        );

        _calculator = new CobCalculator(
            logger.Object,
            iobCalculator,
            sensitivityResolver.Object,
            carbRatioResolver.Object,
            therapySettings.Object,
            _apsSnapshotRepo.Object
        );
    }

    private static CarbIntake MakeCarbIntake(long mills, double carbs, int? absorptionTime = null)
    {
        return new CarbIntake
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(mills).UtcDateTime,
            Carbs = carbs,
            AbsorptionTime = absorptionTime,
        };
    }

    #region CobServiceTests Parity — CobTotal_MultipleTreatments

    [Fact]
    public async Task CobTotal_MultipleCarbIntakes_ShouldMatchLegacyResults()
    {
        var carbIntakes = new List<CarbIntake>
        {
            MakeCarbIntake(
                new DateTimeOffset(2015, 5, 29, 2, 3, 48, 827, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                100),
            MakeCarbIntake(
                new DateTimeOffset(2015, 5, 29, 3, 45, 10, 670, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                10),
        };

        var after100Time = new DateTimeOffset(2015, 5, 29, 2, 3, 49, 827, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var before10Time = new DateTimeOffset(2015, 5, 29, 3, 45, 10, 670, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var after10Time = new DateTimeOffset(2015, 5, 29, 3, 45, 11, 670, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var after100 = await _calculator.CalculateTotalAsync(carbIntakes, time: after100Time);
        var before10 = await _calculator.CalculateTotalAsync(carbIntakes, time: before10Time);
        var after10 = await _calculator.CalculateTotalAsync(carbIntakes, time: after10Time);

        Assert.Equal(100.0, after100.Cob, 1);
        Assert.Equal(59.0, Math.Round(before10.Cob), 0);
        Assert.Equal(69.0, Math.Round(after10.Cob), 0);
    }

    #endregion

    #region CobServiceTests Parity — CobTotal_SingleTreatment

    [Fact]
    public async Task CobTotal_SingleCarbIntake_ShouldFollowAbsorptionCurve()
    {
        var carbIntakes = new List<CarbIntake>
        {
            MakeCarbIntake(
                new DateTimeOffset(2015, 5, 29, 4, 40, 40, 174, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                8),
        };

        var rightAfterTime = new DateTimeOffset(2015, 5, 29, 4, 41, 40, 174, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var later1Time = new DateTimeOffset(2015, 5, 29, 5, 4, 40, 174, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var later2Time = new DateTimeOffset(2015, 5, 29, 5, 20, 0, 174, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var later3Time = new DateTimeOffset(2015, 5, 29, 5, 50, 0, 174, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var later4Time = new DateTimeOffset(2015, 5, 29, 6, 50, 0, 174, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var result1 = await _calculator.CalculateTotalAsync(carbIntakes, time: rightAfterTime);
        var result2 = await _calculator.CalculateTotalAsync(carbIntakes, time: later1Time);
        var result3 = await _calculator.CalculateTotalAsync(carbIntakes, time: later2Time);
        var result4 = await _calculator.CalculateTotalAsync(carbIntakes, time: later3Time);
        var result5 = await _calculator.CalculateTotalAsync(carbIntakes, time: later4Time);

        Assert.Equal(8.0, result1.Cob, 1);
        Assert.Equal(6.0, result2.Cob, 1);
        Assert.Equal(0.0, result3.Cob, 1);
        Assert.Equal(0.0, result4.Cob, 1);
        Assert.Equal(0.0, result5.Cob, 1);
    }

    #endregion

    #region CobServiceTests Parity — CalcTreatment tests mapped to CalcCarbIntake

    [Fact]
    public void CalcCarbIntake_NoCarbs_ShouldReturnZero()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var carbIntake = MakeCarbIntake(now, 0);

        var result = _calculator.CalcCarbIntake(carbIntake, now);

        Assert.Equal(0.0, result.CobContrib);
        Assert.Equal(0.0, result.ActivityContrib);
    }

    [Fact]
    public void CalcCarbIntake_LinearAbsorption_ShouldDecreaseOverTime()
    {
        var startTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var carbIntake = MakeCarbIntake(startTime, 60);

        var rightAfter = _calculator.CalcCarbIntake(carbIntake, startTime + 1000);
        var after30Min = _calculator.CalcCarbIntake(carbIntake, startTime + 30 * 60 * 1000);
        var after60Min = _calculator.CalcCarbIntake(carbIntake, startTime + 60 * 60 * 1000);
        var after120Min = _calculator.CalcCarbIntake(carbIntake, startTime + 120 * 60 * 1000);

        Assert.True(rightAfter.CobContrib > after30Min.CobContrib);
        Assert.True(after30Min.CobContrib > after60Min.CobContrib);
        Assert.True(after60Min.CobContrib > after120Min.CobContrib);
        Assert.True(after120Min.CobContrib >= 0);
    }

    [Fact]
    public void CalcCarbIntake_WithCustomAbsorptionTime_ShouldUseCustomTime()
    {
        var startTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var fastIntake = MakeCarbIntake(startTime, 30, absorptionTime: 60);
        var slowIntake = MakeCarbIntake(startTime, 30, absorptionTime: 240);

        var testTime = startTime + 90 * 60 * 1000;

        var fastResult = _calculator.CalcCarbIntake(fastIntake, testTime);
        var slowResult = _calculator.CalcCarbIntake(slowIntake, testTime);

        Assert.True(slowResult.CobContrib > fastResult.CobContrib);
        Assert.Equal(0.0, fastResult.CobContrib, 1);
    }

    #endregion

    #region CobServiceTests Parity — ApsSnapshot COB Priority Tests

    [Fact]
    public async Task CobTotal_UsesApsSnapshotCob_WhenAvailable()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var carbIntakes = new List<CarbIntake>
        {
            MakeCarbIntake(time - 30 * 60 * 1000, 30),
        };

        var apsSnapshot = new ApsSnapshot
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(time - 5 * 60 * 1000).UtcDateTime,
            Cob = 15.0,
            AidAlgorithm = AidAlgorithm.Loop,
            Device = "loop://iPhone",
        };

        _apsSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { apsSnapshot });

        var result = await _calculator.CalculateTotalAsync(carbIntakes, time: time);

        Assert.Equal(15.0, result.Cob);
        Assert.Equal("Loop", result.Source);
        Assert.Equal("loop://iPhone", result.Device);
    }

    [Fact]
    public async Task CobTotal_UsesOpenApsSnapshotCob_WhenAvailable()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var carbIntakes = new List<CarbIntake>
        {
            MakeCarbIntake(time - 1, 20),
        };

        var apsSnapshot = new ApsSnapshot
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(time - 1).UtcDateTime,
            Cob = 5.0,
            AidAlgorithm = AidAlgorithm.OpenAps,
            Device = "openaps://pi1",
        };

        _apsSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { apsSnapshot });

        var result = await _calculator.CalculateTotalAsync(carbIntakes, time: time);

        Assert.Equal(5.0, result.Cob);
        Assert.Equal("OpenAPS", result.Source);
        Assert.Equal("openaps://pi1", result.Device);
    }

    [Fact]
    public async Task CobTotal_FallsBackToCarbIntakes_WhenNoApsSnapshot()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var carbIntakes = new List<CarbIntake> { MakeCarbIntake(time - 1, 20) };

        var result = await _calculator.CalculateTotalAsync(carbIntakes, time: time);

        Assert.Equal("Care Portal", result.Source);
        Assert.True(result.Cob > 0);
    }

    [Fact]
    public async Task CobTotal_FallsBackToCarbIntakes_WhenApsSnapshotStale()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var carbIntakes = new List<CarbIntake>
        {
            MakeCarbIntake(time - 30 * 60 * 1000, 30),
        };

        _apsSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<ApsSnapshot>());

        var result = await _calculator.CalculateTotalAsync(carbIntakes, time: time);

        Assert.True(result.Cob > 0);
        Assert.Equal("Care Portal", result.Source);
    }

    [Fact]
    public async Task CobTotal_FallsBackToCarbIntakes_WhenApsSnapshotHasZeroCob()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var carbIntakes = new List<CarbIntake> { MakeCarbIntake(time - 1, 20) };

        var apsSnapshot = new ApsSnapshot
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(time - 1).UtcDateTime,
            Cob = 0,
            AidAlgorithm = AidAlgorithm.OpenAps,
            Device = "openaps://pi1",
        };

        _apsSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { apsSnapshot });

        var result = await _calculator.CalculateTotalAsync(carbIntakes, time: time);

        Assert.Equal("Care Portal", result.Source);
        Assert.True(result.Cob > 0);
    }

    #endregion

    #region CobTests Parity — CobTotal_ShouldCalculateFromMultipleTreatments

    [Fact]
    public async Task CobTotal_ShouldCalculateFromMultipleCarbIntakes()
    {
        // Uses sensitivity=95.0 from CobTests (not 50.0 from CobServiceTests)
        // But our calculator defaults to 50.0 — the CobTests used a mocked IOB service,
        // so the IOB activity contribution was zero. With a real IobCalculator and no boluses,
        // the activity contribution is also zero, producing the same COB results.

        var firstTime = new DateTime(2015, 5, 29, 2, 3, 48, 827, DateTimeKind.Utc);
        var secondTime = new DateTime(2015, 5, 29, 3, 45, 10, 670, DateTimeKind.Utc);

        var carbIntakes = new List<CarbIntake>
        {
            MakeCarbIntake(((DateTimeOffset)firstTime).ToUnixTimeMilliseconds(), 100),
            MakeCarbIntake(((DateTimeOffset)secondTime).ToUnixTimeMilliseconds(), 10),
        };

        var after100 = ((DateTimeOffset)firstTime.AddSeconds(1)).ToUnixTimeMilliseconds();
        var before10 = ((DateTimeOffset)secondTime).ToUnixTimeMilliseconds();
        var after10 = ((DateTimeOffset)secondTime.AddSeconds(1)).ToUnixTimeMilliseconds();

        var result1 = await _calculator.CalculateTotalAsync(carbIntakes, time: after100);
        var result2 = await _calculator.CalculateTotalAsync(carbIntakes, time: before10);
        var result3 = await _calculator.CalculateTotalAsync(carbIntakes, time: after10);

        Assert.Equal(100, result1.Cob);
        Assert.Equal(59, Math.Round(result2.Cob));
        Assert.Equal(69, Math.Round(result3.Cob));
    }

    #endregion

    #region CobTests Parity — CobTotal_ShouldCalculateFromSingleTreatment

    [Fact]
    public async Task CobTotal_ShouldCalculateFromSingleCarbIntake()
    {
        var treatmentTime = new DateTime(2015, 5, 29, 4, 40, 40, 174, DateTimeKind.Utc);
        var carbIntakes = new List<CarbIntake>
        {
            MakeCarbIntake(((DateTimeOffset)treatmentTime).ToUnixTimeMilliseconds(), 8),
        };

        var rightAfter = ((DateTimeOffset)treatmentTime.AddMinutes(1)).ToUnixTimeMilliseconds();
        var later1 = ((DateTimeOffset)treatmentTime.AddMinutes(24)).ToUnixTimeMilliseconds();
        var later2 = ((DateTimeOffset)treatmentTime.AddMinutes(40)).ToUnixTimeMilliseconds();
        var later3 = ((DateTimeOffset)treatmentTime.AddMinutes(70)).ToUnixTimeMilliseconds();
        var later4 = ((DateTimeOffset)treatmentTime.AddMinutes(130)).ToUnixTimeMilliseconds();

        var result1 = await _calculator.CalculateTotalAsync(carbIntakes, time: rightAfter);
        var result2 = await _calculator.CalculateTotalAsync(carbIntakes, time: later1);
        var result3 = await _calculator.CalculateTotalAsync(carbIntakes, time: later2);
        var result4 = await _calculator.CalculateTotalAsync(carbIntakes, time: later3);
        var result5 = await _calculator.CalculateTotalAsync(carbIntakes, time: later4);

        Assert.Equal(8, result1.Cob);
        Assert.Equal(6, result2.Cob);
        Assert.Equal(0, result3.Cob);
        Assert.Equal(0, result4.Cob);
        Assert.Equal(0, result5.Cob);
    }

    #endregion

    #region CobTests Parity — Edge Cases

    [Fact]
    public async Task CobTotal_ShouldHandleZeroCarbs()
    {
        var carbIntakes = new List<CarbIntake>
        {
            MakeCarbIntake(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 0),
        };

        var result = await _calculator.CalculateTotalAsync(carbIntakes, time: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        Assert.Equal(0, result.Cob);
    }

    [Fact]
    public async Task CobTotal_ShouldUseDefaultAbsorptionRate()
    {
        var carbIntakes = new List<CarbIntake>
        {
            MakeCarbIntake(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (30 * 60 * 1000), 30),
        };

        var result = await _calculator.CalculateTotalAsync(carbIntakes, time: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        Assert.True(result.Cob > 0);
        Assert.True(result.Cob < 30);
    }

    #endregion
}
