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
/// Tests for the V4-native <see cref="CobCalculator"/> operating on <see cref="CarbIntake"/>
/// and <see cref="Bolus"/> records. Verifies the same decay algorithm as <see cref="CobServiceTests"/>
/// but via the <see cref="ICobCalculator"/> interface, without legacy absorption adjustments.
/// </summary>
public class CobCalculatorTests
{
    private readonly CobCalculator _calculator;
    private readonly Mock<IApsSnapshotRepository> _apsSnapshotRepo;

    private const double DefaultCarbAbsorptionRate = 30.0;
    private const double DefaultSensitivity = 50.0;
    private const double DefaultCarbRatio = 18.0;
    private const double DefaultDIA = 3.0;
    private const double DefaultBasalRate = 1.0;

    public CobCalculatorTests()
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

        // Real IobCalculator — COB calculation calls IOB internally for activity
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

    #region CalculateTotalAsync Tests

    [Fact]
    public async Task CalculateTotal_RecentCarbs_ShouldReturnNonZeroCob()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var carbIntakes = new List<CarbIntake>
        {
            new()
            {
                Carbs = 50,
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 30 * 60 * 1000).UtcDateTime,
            },
        };

        var result = await _calculator.CalculateTotalAsync(carbIntakes, time: now);

        Assert.True(result.Cob > 0, "50g carbs 30 min ago should have non-zero COB");
        Assert.True(result.Cob <= 50, "COB should not exceed total carbs");
    }

    [Fact]
    public async Task CalculateTotal_FullyAbsorbedCarbs_ShouldReturnZeroCob()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var carbIntakes = new List<CarbIntake>
        {
            new()
            {
                Carbs = 10,
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 8 * 60 * 60 * 1000).UtcDateTime,
            },
        };

        var result = await _calculator.CalculateTotalAsync(carbIntakes, time: now);

        Assert.Equal(0.0, result.Cob);
    }

    [Fact]
    public async Task CalculateTotal_CustomAbsorptionTime_UsesOverride()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var fastIntake = new CarbIntake
        {
            Carbs = 30,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 90 * 60 * 1000).UtcDateTime,
            AbsorptionTime = 60,
        };
        var slowIntake = new CarbIntake
        {
            Carbs = 30,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 90 * 60 * 1000).UtcDateTime,
            AbsorptionTime = 240,
        };

        var fastResult = await _calculator.CalculateTotalAsync(new List<CarbIntake> { fastIntake }, time: now);
        var slowResult = await _calculator.CalculateTotalAsync(new List<CarbIntake> { slowIntake }, time: now);

        Assert.True(slowResult.Cob > fastResult.Cob,
            "Slow absorption (240 min) should have more COB remaining than fast absorption (60 min)");
    }

    #endregion

    #region FromCarbIntakes Tests

    [Fact]
    public void FromCarbIntakes_MultipleCarbIntakes_AggregatesCob()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var carbIntakes = new List<CarbIntake>
        {
            new()
            {
                Carbs = 30,
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 30 * 60 * 1000).UtcDateTime,
            },
            new()
            {
                Carbs = 20,
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 15 * 60 * 1000).UtcDateTime,
            },
        };

        var result = _calculator.FromCarbIntakes(carbIntakes, time: now);

        Assert.True(result.Cob > 0, "Multiple recent carb intakes should produce non-zero COB");
        Assert.True(result.Cob <= 50, "COB should not exceed total carbs (30 + 20)");
    }

    [Fact]
    public void FromCarbIntakes_TracksLastCarbs()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var olderIntake = new CarbIntake
        {
            Carbs = 30,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 60 * 60 * 1000).UtcDateTime,
        };
        var newerIntake = new CarbIntake
        {
            Carbs = 20,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 15 * 60 * 1000).UtcDateTime,
        };

        var result = _calculator.FromCarbIntakes(
            new List<CarbIntake> { olderIntake, newerIntake },
            time: now
        );

        Assert.NotNull(result.LastCarbs);
        Assert.Equal(20, result.LastCarbs!.Carbs);
    }

    #endregion
}
