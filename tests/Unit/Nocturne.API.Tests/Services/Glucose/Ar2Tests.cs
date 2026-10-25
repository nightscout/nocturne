using Nocturne.API.Tests.Infrastructure;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.Profiles;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Profiles.Resolvers;
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

    [Fact]
    public async Task PropertiesForecast_FromOneMinuteReadings_FollowsTheFiveMinuteTrend()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var readings = Enumerable
            .Range(0, 20)
            .Select(m => new Entry { Type = "sgv", Mills = now - m * 60 * 1000, Mgdl = 200 - 2 * m })
            .ToList();

        var ar2 = await GetAr2PropertyAsync(readings);

        // Buckets {200,198,196} and {194..186} give Curr = ln(198/140) and Prev = ln(190/140).
        var predicted = ar2
            .GetProperty("forecast")
            .GetProperty("predicted")
            .EnumerateArray()
            .Select(p => p.GetProperty("mgdl").GetInt32());
        Assert.Equal(new[] { 203, 207, 209, 210, 210, 209 }, predicted);
        Assert.Equal("BG 15m: 209 mg/dl", ar2.GetProperty("displayLine").GetString());
    }

    private Task<JsonElement> GetAr2PropertyAsync(double mgdlFiveMinutesAgo, double mgdlNow)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return GetAr2PropertyAsync(
            new List<Entry>
            {
                new() { Type = "sgv", Mills = now - 5 * 60 * 1000, Mgdl = mgdlFiveMinutesAgo },
                new() { Type = "sgv", Mills = now, Mgdl = mgdlNow },
            }
        );
    }

    private async Task<JsonElement> GetAr2PropertyAsync(List<Entry> sgvs)
    {
        var ddata = new DData { Sgvs = sgvs };

        var ddataService = new Mock<IDDataService>();
        ddataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ddata);

        var service = new PropertiesService(
            ddataService.Object,
            Mock.Of<ILogger<PropertiesService>>(),
            TestIobCobReader.Empty(),
            _ar2Service,
            Mock.Of<IDeviceAgeService>(),
            Mock.Of<ITherapySettingsResolver>(),
            TimeProvider.System,
            Options.Create(new DbSizeOptions())
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
}
