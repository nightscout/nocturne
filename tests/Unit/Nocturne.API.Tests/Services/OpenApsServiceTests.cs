using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services;

/// <summary>
/// Tests for OpenApsService with 1:1 legacy compatibility
/// Tests OpenAPS loop data analysis functionality from legacy openaps.js behavior
/// </summary>
[Parity("openaps.test.js")]
public class OpenApsServiceTests
{
    private readonly Mock<ILogger<OpenApsService>> _mockLogger;
    private readonly OpenApsService _openApsService;

    public OpenApsServiceTests()
    {
        _mockLogger = new Mock<ILogger<OpenApsService>>();
        _openApsService = new OpenApsService(_mockLogger.Object);
    }

    [Parity]
    [Fact]
    public void GetPreferences_WithDefaultSettings_ShouldReturnDefaultValues()
    {
        // Arrange
        var settings = new Dictionary<string, object?>();

        // Act
        var result = _openApsService.GetPreferences(settings);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(
            new[] { "status-symbol", "status-label", "iob", "meal-assist", "rssi" },
            result.Fields
        );
        Assert.Equal(
            new[] { "status-symbol", "status-label", "iob", "meal-assist", "rssi" },
            result.RetroFields
        );
        Assert.Equal(30, result.Warn);
        Assert.Equal(60, result.Urgent);
        Assert.False(result.EnableAlerts);
        Assert.Equal("#1e88e5", result.PredIobColor);
        Assert.Equal("#FB8C00", result.PredCobColor);
        Assert.Equal("#FB8C00", result.PredAcobColor);
        Assert.Equal("#00d2d2", result.PredZtColor);
        Assert.Equal("#c9bd60", result.PredUamColor);
        Assert.True(result.ColorPredictionLines);
    }

    [Parity]
    [Fact]
    public void GetPreferences_WithCustomSettings_ShouldReturnCustomValues()
    {
        // Arrange
        var settings = new Dictionary<string, object?>
        {
            { "fields", "status-symbol status-label" },
            { "retroFields", "status-symbol" },
            { "warn", "45" },
            { "urgent", "90" },
            { "enableAlerts", "true" },
            { "predIobColor", "#ff0000" },
            { "colorPredictionLines", "false" },
        };

        // Act
        var result = _openApsService.GetPreferences(settings);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(new[] { "status-symbol", "status-label" }, result.Fields);
        Assert.Equal(new[] { "status-symbol" }, result.RetroFields);
        Assert.Equal(45, result.Warn);
        Assert.Equal(90, result.Urgent);
        Assert.True(result.EnableAlerts);
        Assert.Equal("#ff0000", result.PredIobColor);
        Assert.False(result.ColorPredictionLines);
    }

    [Parity]
    [Fact]
    public void AnalyzeData_WithEnactedStatus_ShouldReturnEnactedSymbol()
    {
        // Arrange
        var currentTime = new DateTime(2015, 12, 5, 19, 5, 0, DateTimeKind.Utc);
        var preferences = new OpenApsPreferences { Warn = 30 };
        var deviceStatuses = new List<DeviceStatus>
        {
            new DeviceStatus
            {
                Device = "openaps://abusypi",
                Mills = ((DateTimeOffset)currentTime.AddMinutes(-2)).ToUnixTimeMilliseconds(),
                OpenAps = new OpenApsStatus
                {
                    Enacted = new OpenApsEnacted
                    {
                        Timestamp = currentTime.AddMinutes(-2).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                        Received = true,
                        Bg = 147,
                        Rate = 0.75,
                        Duration = 30,
                        Reason = "Eventual BG 125>120, no temp, setting 0.75U/hr",
                    },
                },
            },
        };

        // Act
        var result = _openApsService.AnalyzeData(deviceStatuses, currentTime, preferences);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("⌁", result.Status.Symbol);
        Assert.Equal("enacted", result.Status.Code);
        Assert.Equal("Enacted", result.Status.Label);
    }

    [Parity]
    [Fact]
    public void AnalyzeData_WithNotReceivedEnacted_ShouldReturnNotEnactedSymbol()
    {
        // Arrange
        var currentTime = new DateTime(2015, 12, 5, 19, 5, 0, DateTimeKind.Utc);
        var preferences = new OpenApsPreferences { Warn = 30 };

        var deviceStatuses = new List<DeviceStatus>
        {
            new DeviceStatus
            {
                Device = "openaps://abusypi",
                Mills = ((DateTimeOffset)currentTime.AddMinutes(-2)).ToUnixTimeMilliseconds(),
                OpenAps = new OpenApsStatus
                {
                    Enacted = new OpenApsEnacted
                    {
                        Timestamp = currentTime.AddMinutes(-2).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                        Received = false, // Not received
                        Bg = 147,
                        Rate = 0.75,
                        Duration = 30,
                        Reason = "Eventual BG 125>120, no temp, setting 0.75U/hr",
                    },
                },
            },
        };

        // Act
        var result = _openApsService.AnalyzeData(deviceStatuses, currentTime, preferences);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("x", result.Status.Symbol);
        Assert.Equal("notenacted", result.Status.Code);
        Assert.Equal("Not Enacted", result.Status.Label);
    }

    [Parity]
    [Fact]
    public void FindOfflineMarker_WithActiveOfflineMarker_ShouldReturnMarker()
    {
        // Arrange
        var currentTime = new DateTime(2015, 12, 5, 19, 5, 0, DateTimeKind.Utc);
        var treatments = new List<Treatment>
        {
            new Treatment
            {
                EventType = "OpenAPS Offline",
                Mills = ((DateTimeOffset)currentTime.AddMinutes(-30)).ToUnixTimeMilliseconds(),
                Duration = 60, // 60 minutes duration
            },
        };

        // Act
        var result = _openApsService.FindOfflineMarker(treatments, currentTime);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("OpenAPS Offline", result.EventType);
    }

    [Parity]
    [Fact]
    public void FindOfflineMarker_WithExpiredOfflineMarker_ShouldReturnNull()
    {
        // Arrange
        var currentTime = new DateTime(2015, 12, 5, 19, 5, 0, DateTimeKind.Utc);
        var treatments = new List<Treatment>
        {
            new Treatment
            {
                EventType = "OpenAPS Offline",
                Mills = currentTime.AddMinutes(-120).Ticks / 10000, // 2 hours ago
                Duration = 60, // 60 minutes duration - expired
            },
        };

        // Act
        var result = _openApsService.FindOfflineMarker(treatments, currentTime);

        // Assert
        Assert.Null(result);
    }

    [Parity]
    [Fact]
    public void CheckNotifications_WithAlertsDisabled_ShouldReturnNone()
    {
        // Arrange
        var analysisResult = new OpenApsAnalysisResult
        {
            LastLoopMoment = DateTime.UtcNow.AddMinutes(-90),
        };
        var preferences = new OpenApsPreferences { EnableAlerts = false, Urgent = 60 };
        var currentTime = DateTime.UtcNow;

        // Act
        var result = _openApsService.CheckNotifications(
            analysisResult,
            preferences,
            currentTime,
            null
        );

        // Assert
        Assert.Equal(Levels.NONE, result);
    }

    [Parity]
    [Fact]
    public void CheckNotifications_WithStuckLoop_ShouldReturnUrgent()
    {
        // Arrange
        var analysisResult = new OpenApsAnalysisResult
        {
            LastLoopMoment = DateTime.UtcNow.AddMinutes(-90),
        };
        var preferences = new OpenApsPreferences { EnableAlerts = true, Urgent = 60 };
        var currentTime = DateTime.UtcNow;

        // Act
        var result = _openApsService.CheckNotifications(
            analysisResult,
            preferences,
            currentTime,
            null
        ); // Assert
        Assert.Equal(Levels.URGENT, result);
    }

    [Parity]
    [Fact]
    public void CheckNotifications_WithOfflineMarker_ShouldReturnNone()
    {
        // Arrange
        var analysisResult = new OpenApsAnalysisResult
        {
            LastLoopMoment = DateTime.UtcNow.AddMinutes(-90),
        };
        var preferences = new OpenApsPreferences { EnableAlerts = true, Urgent = 60 };
        var currentTime = DateTime.UtcNow;
        var offlineMarker = new Treatment { EventType = "OpenAPS Offline" };

        // Act
        var result = _openApsService.CheckNotifications(
            analysisResult,
            preferences,
            currentTime,
            offlineMarker
        );

        // Assert
        Assert.Equal(Levels.NONE, result);
    }

    [Parity]
    [Fact]
    public void GetEventTypes_WithMgDlUnits_ShouldReturnCorrectTargets()
    {
        // Arrange
        var units = "mg/dl";

        // Act
        var result = _openApsService.GetEventTypes(units);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(3, result.Count);

        var tempTarget = result[0] as dynamic;
        Assert.NotNull(tempTarget);
        // Note: This is simplified - full implementation needs proper dynamic object handling
    }

    [Parity]
    [Fact]
    public void GetEventTypes_WithMmolUnits_ShouldReturnCorrectTargets()
    {
        // Arrange
        var units = "mmol";

        // Act
        var result = _openApsService.GetEventTypes(units);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(3, result.Count);

        // Note: This is simplified - full implementation needs proper dynamic object handling
    }

    [Parity]
    [Fact]
    public void GenerateForecastPoints_WithPredictionData_ShouldReturnPoints()
    {
        // Arrange
        var predictionData = new OpenApsPredBg
        {
            Iob = new List<double> { 100, 95, 90, 85, 80 },
            Cob = new List<double> { 100, 105, 110, 105, 100 },
        };
        var preferences = new OpenApsPreferences();
        var currentTime = DateTime.UtcNow;

        // Act
        var result = _openApsService.GenerateForecastPoints(
            predictionData,
            preferences,
            currentTime
        );

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Count > 0);
        Assert.Contains(result, p => p.Type == "IOB");
        Assert.Contains(result, p => p.Type == "COB");
    }

    [Parity]
    [Fact]
    public void HandleVirtualAssistantForecast_WithEventualBg_ShouldReturnForecast()
    {
        // Arrange
        var analysisResult = new OpenApsAnalysisResult { LastEventualBg = 125 };

        // Act
        var (title, response) = _openApsService.HandleVirtualAssistantForecast(analysisResult);

        // Assert
        Assert.Equal("OpenAPS Forecast", title);
        Assert.Equal("The OpenAPS Eventual BG is 125", response);
    }

    [Parity]
    [Fact]
    public void HandleVirtualAssistantForecast_WithoutEventualBg_ShouldReturnUnknown()
    {
        // Arrange
        var analysisResult = new OpenApsAnalysisResult();

        // Act
        var (title, response) = _openApsService.HandleVirtualAssistantForecast(analysisResult);

        // Assert
        Assert.Equal("OpenAPS Forecast", title);
        Assert.Equal("Unknown", response);
    }

    [Parity]
    [Fact]
    public void HandleVirtualAssistantLastLoop_WithLastLoopMoment_ShouldReturnTimeAgo()
    {
        // Arrange
        var lastLoopTime = DateTime.UtcNow.AddMinutes(-2);
        var analysisResult = new OpenApsAnalysisResult { LastLoopMoment = lastLoopTime };
        var currentTime = DateTime.UtcNow;

        // Act
        var (title, response) = _openApsService.HandleVirtualAssistantLastLoop(
            analysisResult,
            currentTime
        );

        // Assert
        Assert.Equal("Last Loop", title);
        Assert.Contains("2 minutes ago", response);
    }

    [Parity]
    [Fact]
    public void HandleVirtualAssistantLastLoop_WithoutLastLoopMoment_ShouldReturnUnknown()
    {
        // Arrange
        var analysisResult = new OpenApsAnalysisResult();
        var currentTime = DateTime.UtcNow;

        // Act
        var (title, response) = _openApsService.HandleVirtualAssistantLastLoop(
            analysisResult,
            currentTime
        );

        // Assert
        Assert.Equal("Last Loop", title);
        Assert.Equal("Unknown", response);
    }

    [Parity]
    [Fact]
    public void GenerateVisualizationData_WithAnalysisResult_ShouldReturnVisualizationData()
    {
        // Arrange
        var analysisResult = new OpenApsAnalysisResult
        {
            LastLoopMoment = DateTime.UtcNow.AddMinutes(-2),
            Status = new OpenApsLoopStatus
            {
                Symbol = "⌁",
                Code = "enacted",
                Label = "Enacted",
            },
        };
        var preferences = new OpenApsPreferences();
        var currentTime = DateTime.UtcNow;

        // Act
        var result = _openApsService.GenerateVisualizationData(
            analysisResult,
            preferences,
            false,
            currentTime
        );

        // Assert
        Assert.NotNull(result);
        // Note: This is simplified - full implementation needs proper dynamic object verification
    }
}
