using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.Profiles;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services.Glucose;

/// <summary>
/// Tests for AR2 forecasting with 1:1 legacy JavaScript compatibility
/// Based on legacy ar2.test.js test cases
/// </summary>
[Parity("ar2.test.js")]
public class Ar2Tests
{
    private readonly Mock<ILogger<Ar2Service>> _mockLogger;
    private readonly Ar2Service _ar2Service;

    public Ar2Tests()
    {
        _mockLogger = new Mock<ILogger<Ar2Service>>();
        _ar2Service = new Ar2Service(_mockLogger.Object);
    }

    [Fact]
    public async Task CalculateForecastAsync_ShouldReturnEmptyForecast_WhenCannotForecast()
    {
        // Arrange
        var ddata = new DData();
        var bgNowProperties = new Dictionary<string, object>(); // Missing required properties
        var deltaProperties = new Dictionary<string, object>();
        var settings = new Dictionary<string, object>();

        // Act
        var result = await _ar2Service.CalculateForecastAsync(
            ddata,
            bgNowProperties,
            deltaProperties,
            settings,
            CancellationToken.None
        );

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.Forecast);
        Assert.Empty(result.Forecast.Predicted);
        Assert.Equal(0, result.Forecast.AvgLoss);
    }

    [Fact]
    public async Task PropertiesForecast_FromTwoReadingsInRange_PredictsWithoutAlarm()
    {
        var ar2 = await GetAr2PropertyAsync(100, 105);

        Assert.Equal(6, ar2.GetProperty("forecast").GetProperty("predicted").GetArrayLength());
        Assert.StartsWith("BG 15m: ", ar2.GetProperty("displayLine").GetString());
        Assert.False(ar2.TryGetProperty("level", out _));
    }

    [Fact]
    public async Task PropertiesForecast_RisingAboveTarget_WarnsHighWithFifteenMinuteLine()
    {
        var ar2 = await GetAr2PropertyAsync(150, 170);

        Assert.Equal("BG 15m: 206 mg/dl", ar2.GetProperty("displayLine").GetString());
        Assert.Equal("warn", ar2.GetProperty("level").GetString());
        Assert.Equal("high", ar2.GetProperty("eventName").GetString());
    }

    [Fact]
    public async Task PropertiesForecast_AtTheCeiling_ClampsPredictionsToBounds()
    {
        var ar2 = await GetAr2PropertyAsync(400, 400);

        var predicted = ar2.GetProperty("forecast").GetProperty("predicted").EnumerateArray().ToList();
        Assert.Equal(6, predicted.Count);
        Assert.All(predicted, p => Assert.InRange(p.GetProperty("mgdl").GetInt32(), 36, 400));
    }

    private async Task<JsonElement> GetAr2PropertyAsync(double mgdlFiveMinutesAgo, double mgdlNow)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ddata = new DData
        {
            Sgvs = new List<Entry>
            {
                new() { Type = "sgv", Mills = now - 5 * 60 * 1000, Mgdl = mgdlFiveMinutesAgo },
                new() { Type = "sgv", Mills = now, Mgdl = mgdlNow },
            },
        };

        var ddataService = new Mock<IDDataService>();
        ddataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ddata);

        var service = new PropertiesService(
            ddataService.Object,
            Mock.Of<ILogger<PropertiesService>>(),
            Mock.Of<IIobCalculator>(),
            Mock.Of<ICobCalculator>(),
            Mock.Of<IBolusRepository>(),
            Mock.Of<ICarbIntakeRepository>(),
            Mock.Of<ITempBasalRepository>(),
            _ar2Service,
            Mock.Of<IDeviceAgeService>()
        );

        var properties = await service.GetPropertiesAsync(new[] { "bgnow", "delta", "ar2" });
        var json = JsonSerializer.Serialize(properties, NightscoutJsonOptions.Create());
        return JsonDocument.Parse(json).RootElement.GetProperty("ar2").Clone();
    }

    [Fact]
    public void CanForecast_ShouldReturnFalse_WhenBgNowMissing()
    {
        // Arrange
        var bgNowProperties = new Dictionary<string, object>(); // Missing mean
        var deltaProperties = new Dictionary<string, object> { ["mean5MinsAgo"] = 100.0 };

        // Act
        var result = _ar2Service.CanForecast(bgNowProperties, deltaProperties);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void CanForecast_ShouldReturnFalse_WhenDeltaMissing()
    {
        // Arrange
        var bgNowProperties = new Dictionary<string, object> { ["mean"] = 105.0 };
        var deltaProperties = new Dictionary<string, object>(); // Missing mean5MinsAgo

        // Act
        var result = _ar2Service.CanForecast(bgNowProperties, deltaProperties);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void CanForecast_ShouldReturnFalse_WhenBgTooLow()
    {
        // Arrange
        var bgNowProperties = new Dictionary<string, object>
        {
            ["mean"] = 35.0, // Below BG_MIN (36)
        };
        var deltaProperties = new Dictionary<string, object> { ["mean5MinsAgo"] = 30.0 };

        // Act
        var result = _ar2Service.CanForecast(bgNowProperties, deltaProperties);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void CanForecast_ShouldReturnTrue_WithValidData()
    {
        // Arrange
        var bgNowProperties = new Dictionary<string, object> { ["mean"] = 105.0 };
        var deltaProperties = new Dictionary<string, object> { ["mean5MinsAgo"] = 100.0 };

        // Act
        var result = _ar2Service.CanForecast(bgNowProperties, deltaProperties);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task GenerateForecastConeAsync_ShouldReturnEmptyList_WhenCannotForecast()
    {
        // Arrange
        var ddata = new DData();
        var bgNowProperties = new Dictionary<string, object>(); // Missing required properties
        var deltaProperties = new Dictionary<string, object>();

        // Act
        var result = await _ar2Service.GenerateForecastConeAsync(
            ddata,
            bgNowProperties,
            deltaProperties,
            cancellationToken: CancellationToken.None
        );

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GenerateForecastConeAsync_ShouldGenerateConePoints_WithValidData()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var before = now - (5 * 60 * 1000);

        var ddata = new DData
        {
            Sgvs = new List<Entry>
            {
                new() { Mills = before, Mgdl = 100 },
                new() { Mills = now, Mgdl = 105 },
            },
        };

        var bgNowProperties = new Dictionary<string, object> { ["mean"] = 105.0, ["mills"] = now };

        var deltaProperties = new Dictionary<string, object> { ["mean5MinsAgo"] = 100.0 };

        // Act
        var result = await _ar2Service.GenerateForecastConeAsync(
            ddata,
            bgNowProperties,
            deltaProperties,
            2.0,
            CancellationToken.None
        );

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result);

        // Should generate cone points for 13 steps (from legacy CONE_STEPS array)
        // Each step generates 2 points (positive and negative cone), so 26 total
        Assert.Equal(26, result.Count);

        // All points should have cyan color
        Assert.All(result, point => Assert.Equal("cyan", point.Color));

        // Points should have valid mg/dL values (between 36-400)
        Assert.All(
            result,
            point =>
            {
                Assert.True(point.Mgdl >= 36);
                Assert.True(point.Mgdl <= 400);
            }
        );
    }

    [Fact]
    public async Task GenerateForecastConeAsync_ShouldGenerateLine_WhenConeFactorZero()
    {
        // Arrange - test case from ar2.test.js "should plot a line if coneFactor is 0"
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var before = now - (5 * 60 * 1000);

        var ddata = new DData
        {
            Sgvs = new List<Entry>
            {
                new() { Mills = before, Mgdl = 100 },
                new() { Mills = now, Mgdl = 105 },
            },
        };

        var bgNowProperties = new Dictionary<string, object> { ["mean"] = 105.0, ["mills"] = now };

        var deltaProperties = new Dictionary<string, object> { ["mean5MinsAgo"] = 100.0 };

        // Act - coneFactor of 0 should generate a line (only positive points)
        var result = await _ar2Service.GenerateForecastConeAsync(
            ddata,
            bgNowProperties,
            deltaProperties,
            0.0,
            CancellationToken.None
        );

        // Assert
        Assert.NotNull(result);
        Assert.Equal(13, result.Count); // Only 13 points (no negative cone points)
    }
}
