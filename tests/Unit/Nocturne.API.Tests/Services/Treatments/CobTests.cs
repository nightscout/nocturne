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
/// Tests for COB (Carbs on Board) functionality with 1:1 legacy compatibility
/// Based on legacy cob.test.js
/// </summary>
[Parity("cob.test.js")]
public class CobTests
{
    private readonly Nocturne.API.Services.Treatments.CobService _cobService;

    public CobTests()
    {
        var mockLogger = new Mock<ILogger<Nocturne.API.Services.Treatments.CobService>>();
        var mockIobService = new Mock<IIobService>();

        var sensitivity = new Mock<ISensitivityResolver>();
        sensitivity.Setup(s => s.GetSensitivityAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(95.0);
        var carbRatio = new Mock<ICarbRatioResolver>();
        carbRatio.Setup(c => c.GetCarbRatioAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(18.0);
        var therapySettings = new Mock<ITherapySettingsResolver>();
        therapySettings.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        therapySettings.Setup(t => t.GetCarbAbsorptionRateAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(30.0);

        var apsSnapshotRepo = new Mock<IApsSnapshotRepository>();
        apsSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<ApsSnapshot>());

        _cobService = new Nocturne.API.Services.Treatments.CobService(
            mockLogger.Object, mockIobService.Object, sensitivity.Object, carbRatio.Object, therapySettings.Object, apsSnapshotRepo.Object);
    }

    [Fact]
    public async Task CobTotal_ShouldCalculateFromMultipleTreatments()
    {
        var firstTreatmentTime = new DateTime(2015, 5, 29, 2, 3, 48, 827, DateTimeKind.Utc);
        var secondTreatmentTime = new DateTime(2015, 5, 29, 3, 45, 10, 670, DateTimeKind.Utc);

        var treatments = new List<Treatment>
        {
            new() { Carbs = 100, Mills = ((DateTimeOffset)firstTreatmentTime).ToUnixTimeMilliseconds() },
            new() { Carbs = 10, Mills = ((DateTimeOffset)secondTreatmentTime).ToUnixTimeMilliseconds() },
        };

        var after100 = ((DateTimeOffset)firstTreatmentTime.AddSeconds(1)).ToUnixTimeMilliseconds();
        var before10 = ((DateTimeOffset)secondTreatmentTime).ToUnixTimeMilliseconds();
        var after10 = ((DateTimeOffset)secondTreatmentTime.AddSeconds(1)).ToUnixTimeMilliseconds();

        var result1 = await _cobService.CobTotalAsync(treatments, after100);
        var result2 = await _cobService.CobTotalAsync(treatments, before10);
        var result3 = await _cobService.CobTotalAsync(treatments, after10);

        Assert.Equal(100, result1.Cob);
        Assert.Equal(59, Math.Round(result2.Cob));
        Assert.Equal(69, Math.Round(result3.Cob));
    }

    [Fact]
    public async Task CobTotal_ShouldCalculateFromSingleTreatment()
    {
        var treatmentTime = new DateTime(2015, 5, 29, 4, 40, 40, 174, DateTimeKind.Utc);
        var treatments = new List<Treatment>
        {
            new() { Carbs = 8, Mills = ((DateTimeOffset)treatmentTime).ToUnixTimeMilliseconds() },
        };

        var rightAfter = ((DateTimeOffset)treatmentTime.AddMinutes(1)).ToUnixTimeMilliseconds();
        var later1 = ((DateTimeOffset)treatmentTime.AddMinutes(24)).ToUnixTimeMilliseconds();
        var later2 = ((DateTimeOffset)treatmentTime.AddMinutes(40)).ToUnixTimeMilliseconds();
        var later3 = ((DateTimeOffset)treatmentTime.AddMinutes(70)).ToUnixTimeMilliseconds();
        var later4 = ((DateTimeOffset)treatmentTime.AddMinutes(130)).ToUnixTimeMilliseconds();

        var result1 = await _cobService.CobTotalAsync(treatments, rightAfter);
        var result2 = await _cobService.CobTotalAsync(treatments, later1);
        var result3 = await _cobService.CobTotalAsync(treatments, later2);
        var result4 = await _cobService.CobTotalAsync(treatments, later3);
        var result5 = await _cobService.CobTotalAsync(treatments, later4);

        Assert.Equal(8, result1.Cob);
        Assert.Equal(6, result2.Cob);
        Assert.Equal(0, result3.Cob);
        Assert.Equal(0, result4.Cob);
        Assert.Equal(0, result5.Cob);
    }

    [Fact]
    public async Task CobTotal_ShouldHandleZeroCarbs()
    {
        var treatments = new List<Treatment>
        {
            new() { Carbs = 0, Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
        };

        var result = await _cobService.CobTotalAsync(treatments, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        Assert.Equal(0, result.Cob);
    }

    [Fact]
    public async Task CobTotal_ShouldIgnoreNullCarbs()
    {
        var treatments = new List<Treatment>
        {
            new() { Carbs = null, Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
        };

        var result = await _cobService.CobTotalAsync(treatments, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        Assert.Equal(0, result.Cob);
    }

    [Fact]
    public async Task CobTotal_ShouldUseDefaultAbsorptionRate()
    {
        var treatments = new List<Treatment>
        {
            new() { Carbs = 30, Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (30 * 60 * 1000) },
        };

        var result = await _cobService.CobTotalAsync(treatments, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        Assert.True(result.Cob > 0);
        Assert.True(result.Cob < 30);
    }
}

/// <summary>
/// Profile data for COB calculations
/// </summary>
public class CobProfile
{
    public double CarbsHr { get; set; } = 30.0;
    public double Sens { get; set; } = 95.0;
    public double CarbRatio { get; set; } = 18.0;
}
