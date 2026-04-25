using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services.Treatments;

/// <summary>
/// Simple validation tests for the COB service with V4 resolvers
/// </summary>
public class CobServiceValidationTests
{
    private readonly ICobService _cobService;

    public CobServiceValidationTests()
    {
        var logger = new Mock<ILogger<Nocturne.API.Services.Treatments.CobService>>();
        var iobService = new Mock<IIobService>();

        var sensitivity = new Mock<ISensitivityResolver>();
        sensitivity.Setup(s => s.GetSensitivityAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(50.0);
        var carbRatio = new Mock<ICarbRatioResolver>();
        carbRatio.Setup(c => c.GetCarbRatioAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(15.0);
        var therapySettings = new Mock<ITherapySettingsResolver>();
        therapySettings.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        therapySettings.Setup(t => t.GetCarbAbsorptionRateAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(30.0);

        _cobService = new Nocturne.API.Services.Treatments.CobService(
            logger.Object, iobService.Object, sensitivity.Object, carbRatio.Object, therapySettings.Object);
    }

    [Fact]
    public void CobTotal_WithCarbs_ShouldReturnPositiveCob()
    {
        var treatments = new List<Treatment>
        {
            new() { Carbs = 50, Mills = DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeMilliseconds() },
        };

        var result = _cobService.CobTotal(treatments, new List<DeviceStatus>());

        Assert.True(result.Cob >= 0, "COB should be non-negative");
        Assert.Equal("Care Portal", result.Source);
    }

    [Fact]
    public void CobTotal_WithoutCarbs_ShouldReturnZeroCob()
    {
        var treatments = new List<Treatment>
        {
            new() { Insulin = 5.0, Mills = DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeMilliseconds() },
        };

        var result = _cobService.CobTotal(treatments, new List<DeviceStatus>());

        Assert.Equal(0.0, result.Cob);
    }

    [Fact]
    public void CobTotal_WithRecentDeviceStatus_ShouldPrioritizeDeviceStatus()
    {
        var treatments = new List<Treatment>
        {
            new() { Carbs = 50, Mills = DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeMilliseconds() },
        };
        var deviceStatus = new List<DeviceStatus>
        {
            new()
            {
                Device = "Loop",
                Mills = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds(),
                Loop = new LoopStatus { Cob = new LoopCob { Cob = 25.5 } },
            },
        };

        var result = _cobService.CobTotal(treatments, deviceStatus);

        Assert.Equal(25.5, result.Cob);
        Assert.Equal("Loop", result.Source);
    }

    [Fact]
    public void FromDeviceStatus_WithLoopCob_ShouldExtractCorrectly()
    {
        var deviceStatus = new DeviceStatus
        {
            Device = "MyLoop",
            Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Loop = new LoopStatus { Cob = new LoopCob { Cob = 15.7 } },
        };

        var result = _cobService.FromDeviceStatus(deviceStatus);

        Assert.Equal(15.7, result.Cob);
        Assert.Equal("Loop", result.Source);
        Assert.Equal("MyLoop", result.Device);
    }
}
