using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services;

/// <summary>
/// Tests for PumpAlertService with 1:1 legacy compatibility
/// Tests pump status monitoring and alert functionality from legacy pump.js behavior
/// </summary>
[Parity("pump.test.js")]
public class PumpAlertServiceTests
{
    private readonly Mock<IOpenApsService> _mockOpenApsService;
    private readonly Mock<IProfileService> _mockProfileService;
    private readonly Mock<ILogger<PumpAlertService>> _mockLogger;
    private readonly PumpAlertService _pumpAlertService;

    // Test data matching legacy pump.test.js
    private static readonly long TestTime = DateTimeOffset.Parse("2015-12-05T19:05:00.000Z").ToUnixTimeMilliseconds();

    public PumpAlertServiceTests()
    {
        _mockOpenApsService = new Mock<IOpenApsService>();
        _mockProfileService = new Mock<IProfileService>();
        _mockLogger = new Mock<ILogger<PumpAlertService>>();
        _pumpAlertService = new PumpAlertService(_mockOpenApsService.Object, _mockLogger.Object);
    }

    private static List<DeviceStatus> CreateTestStatuses(double? reservoir = 86.4, double? voltage = 1.52)
    {
        return
        [
            new DeviceStatus
            {
                Device = "openaps://farawaypi",
                CreatedAt = "2015-12-05T17:35:00.000Z",
                Mills = DateTimeOffset.Parse("2015-12-05T17:35:00.000Z").ToUnixTimeMilliseconds(),
                Pump = new PumpStatus
                {
                    Battery = new PumpBattery { Voltage = voltage },
                    Status = new PumpStatusDetails { Status = "normal", Bolusing = false, Suspended = false },
                    Reservoir = reservoir,
                    Clock = "2015-12-05T17:32:00.000Z"
                }
            },
            new DeviceStatus
            {
                Device = "openaps://abusypi",
                CreatedAt = "2015-12-05T19:05:00.000Z",
                Mills = DateTimeOffset.Parse("2015-12-05T19:05:00.000Z").ToUnixTimeMilliseconds(),
                Pump = new PumpStatus
                {
                    Battery = new PumpBattery { Voltage = voltage },
                    Status = new PumpStatusDetails { Status = "normal", Bolusing = false, Suspended = false },
                    Reservoir = reservoir,
                    Clock = "2015-12-05T19:02:00.000Z"
                }
            }
        ];
    }

    [Parity]
    [Fact]
    public void SetProperties_WithNormalPump_ShouldSetCorrectLevel()
    {
        // Arrange - matches legacy: "set the property and update the pill"
        var statuses = CreateTestStatuses();
        var preferences = new PumpPreferences { EnableAlerts = true };

        // Act
        var result = _pumpAlertService.BuildPumpStatus(statuses, TestTime, preferences, _mockProfileService.Object);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(PumpAlertLevel.None, result.Level);
        Assert.NotNull(result.Battery);
        Assert.Equal(1.52, result.Battery.Value);
        Assert.NotNull(result.Reservoir);
        Assert.Equal(86.4, result.Reservoir.Value);
    }

    [Parity]
    [Fact]
    public void SetProperties_WithReservoirDisplayOverride_ShouldUseOverride()
    {
        // Arrange - matches legacy: "use reservoir_display_override when available"
        var statuses = new List<DeviceStatus>
        {
            new DeviceStatus
            {
                Device = "openaps://abusypi",
                CreatedAt = "2015-12-05T19:05:00.000Z",
                Mills = TestTime,
                Pump = new PumpStatus
                {
                    Battery = new PumpBattery { Voltage = 1.52 },
                    Status = new PumpStatusDetails { Status = "normal" },
                    Reservoir = 86.4,
                    ReservoirDisplayOverride = "50+U",
                    Clock = "2015-12-05T19:02:00.000Z"
                }
            }
        };
        var preferences = new PumpPreferences { EnableAlerts = true };

        // Act
        var result = _pumpAlertService.BuildPumpStatus(statuses, TestTime, preferences, _mockProfileService.Object);

        // Assert
        Assert.NotNull(result.Reservoir);
        Assert.Equal("50+U", result.Reservoir.Display);
    }

    [Parity]
    [Fact]
    public void CheckNotifications_WhenPumpOk_ShouldNotGenerateAlert()
    {
        // Arrange - matches legacy: "not generate an alert when pump is ok"
        var statuses = CreateTestStatuses();
        var preferences = new PumpPreferences { EnableAlerts = true };

        var status = _pumpAlertService.BuildPumpStatus(statuses, TestTime, preferences, _mockProfileService.Object);

        // Act
        var notification = _pumpAlertService.CheckNotifications(status, preferences, TestTime, _mockProfileService.Object);

        // Assert
        Assert.Null(notification);
    }

    [Parity]
    [Fact]
    public void CheckNotifications_WhenReservoirLow_ShouldGenerateUrgentAlert()
    {
        // Arrange - matches legacy: "generate an alert when reservoir is low" (0.5U)
        var statuses = CreateTestStatuses(reservoir: 0.5);
        var preferences = new PumpPreferences { EnableAlerts = true, UrgentRes = 5, WarnRes = 10 };

        var status = _pumpAlertService.BuildPumpStatus(statuses, TestTime, preferences, _mockProfileService.Object);

        // Act
        var notification = _pumpAlertService.CheckNotifications(status, preferences, TestTime, _mockProfileService.Object);

        // Assert
        Assert.NotNull(notification);
        Assert.Equal((int)PumpAlertLevel.Urgent, notification.Level);
        Assert.Equal("URGENT: Pump Reservoir Low", notification.Title);
    }

    [Parity]
    [Fact]
    public void CheckNotifications_WhenReservoirZero_ShouldGenerateUrgentAlert()
    {
        // Arrange - matches legacy: "generate an alert when reservoir is 0"
        var statuses = CreateTestStatuses(reservoir: 0);
        var preferences = new PumpPreferences { EnableAlerts = true, UrgentRes = 5, WarnRes = 10 };

        var status = _pumpAlertService.BuildPumpStatus(statuses, TestTime, preferences, _mockProfileService.Object);

        // Act
        var notification = _pumpAlertService.CheckNotifications(status, preferences, TestTime, _mockProfileService.Object);

        // Assert
        Assert.NotNull(notification);
        Assert.Equal((int)PumpAlertLevel.Urgent, notification.Level);
        Assert.Equal("URGENT: Pump Reservoir Low", notification.Title);
    }

    [Parity]
    [Fact]
    public void CheckNotifications_WhenBatteryLow_ShouldGenerateWarnAlert()
    {
        // Arrange - matches legacy: "generate an alert when battery is low" (1.33V)
        var statuses = CreateTestStatuses(voltage: 1.33);
        var preferences = new PumpPreferences { EnableAlerts = true, WarnBattV = 1.35, UrgentBattV = 1.3 };

        var status = _pumpAlertService.BuildPumpStatus(statuses, TestTime, preferences, _mockProfileService.Object);

        // Act
        var notification = _pumpAlertService.CheckNotifications(status, preferences, TestTime, _mockProfileService.Object);

        // Assert
        Assert.NotNull(notification);
        Assert.Equal((int)PumpAlertLevel.Warn, notification.Level);
        Assert.Equal("Warning, Pump Battery Low", notification.Title);
    }

    [Parity]
    [Fact]
    public void CheckNotifications_WhenBatteryCritical_ShouldGenerateUrgentAlert()
    {
        // Arrange - matches legacy: "generate an urgent alarm when battery is really low" (1.00V)
        var statuses = CreateTestStatuses(voltage: 1.00);
        var preferences = new PumpPreferences { EnableAlerts = true, WarnBattV = 1.35, UrgentBattV = 1.3 };

        var status = _pumpAlertService.BuildPumpStatus(statuses, TestTime, preferences, _mockProfileService.Object);

        // Act
        var notification = _pumpAlertService.CheckNotifications(status, preferences, TestTime, _mockProfileService.Object);

        // Assert
        Assert.NotNull(notification);
        Assert.Equal((int)PumpAlertLevel.Urgent, notification.Level);
        Assert.Equal("URGENT: Pump Battery Low", notification.Title);
    }

    [Parity]
    [Fact]
    public void CheckNotifications_QuietNight_ShouldSuppressBatteryAlert()
    {
        // Arrange - matches legacy: "not generate a battery alarm during night when PUMP_WARN_BATT_QUIET_NIGHT is true"
        var statuses = CreateTestStatuses(voltage: 1.00);

        // Set up quiet night mode - time is during night (19:05, dayEnd is 21.0, dayStart is 24.0 in test)
        var preferences = new PumpPreferences
        {
            EnableAlerts = true,
            WarnBattV = 1.35,
            UrgentBattV = 1.3,
            WarnBattQuietNight = true,
            DayStart = 24.0, // Set to 24 so it always evaluates as night
            DayEnd = 21.0
        };

        _mockProfileService.Setup(p => p.GetTimezone()).Returns("UTC");

        var status = _pumpAlertService.BuildPumpStatus(statuses, TestTime, preferences, _mockProfileService.Object);

        // Act
        var notification = _pumpAlertService.CheckNotifications(status, preferences, TestTime, _mockProfileService.Object);

        // Assert - battery alert should be suppressed during quiet night
        // Note: The battery level is evaluated as NONE due to batteryWarn=false during night
        Assert.Equal(PumpAlertLevel.None, status.Battery?.Level ?? PumpAlertLevel.None);
    }

    [Parity]
    [Fact]
    public void CheckNotifications_OfflineMarker_ShouldNotGenerateAlert()
    {
        // Arrange - matches legacy: "not generate an alert for a stale pump data, when there is an offline marker"
        var statuses = CreateTestStatuses();
        // Make data stale by advancing time by 1 hour
        var staleTime = TestTime + (60 * 60 * 1000);
        var preferences = new PumpPreferences { EnableAlerts = true, UrgentClock = 60, WarnClock = 30 };

        var treatments = new List<Treatment>
        {
            new Treatment
            {
                EventType = "OpenAPS Offline",
                Mills = TestTime,
                Duration = 60 // 60 minutes
            }
        };

        // Set up mock to return the offline marker
        _mockOpenApsService
            .Setup(o => o.FindOfflineMarker(It.IsAny<IEnumerable<Treatment>>(), It.IsAny<DateTime>()))
            .Returns(treatments[0]);

        // Act
        var status = _pumpAlertService.BuildPumpStatus(statuses, staleTime, preferences, _mockProfileService.Object, treatments);

        // Assert - no alert should be generated due to offline marker
        Assert.Equal(PumpAlertLevel.None, status.Level);
    }

    [Parity]
    [Fact]
    public void VirtualAssistant_ReservoirHandler_ShouldReturnFormattedResponse()
    {
        // Arrange - matches legacy: "should handle virtAsst requests" for reservoir
        var statuses = CreateTestStatuses();
        var preferences = new PumpPreferences();

        var status = _pumpAlertService.BuildPumpStatus(statuses, TestTime, preferences, _mockProfileService.Object);

        // Act
        var (title, response) = _pumpAlertService.HandleVirtualAssistantReservoir(status);

        // Assert
        Assert.Equal("Insulin Remaining", title);
        Assert.Equal("You have 86.4 units remaining", response);
    }

    [Parity]
    [Fact]
    public void VirtualAssistant_BatteryHandler_ShouldReturnFormattedResponse()
    {
        // Arrange - matches legacy: "should handle virtAsst requests" for battery
        var statuses = CreateTestStatuses();
        var preferences = new PumpPreferences();

        var status = _pumpAlertService.BuildPumpStatus(statuses, TestTime, preferences, _mockProfileService.Object);

        // Act
        var (title, response) = _pumpAlertService.HandleVirtualAssistantBattery(status);

        // Assert
        Assert.Equal("Pump Battery", title);
        Assert.Equal("Your pump battery is at 1.52 volts", response);
    }

    [Fact]
    public void GetPreferences_WithDefaultSettings_ShouldReturnDefaults()
    {
        // Arrange
        var settings = new Dictionary<string, object?>();

        // Act
        var result = _pumpAlertService.GetPreferences(settings);

        // Assert
        Assert.Equal(30, result.WarnClock);
        Assert.Equal(60, result.UrgentClock);
        Assert.Equal(10, result.WarnRes);
        Assert.Equal(5, result.UrgentRes);
        Assert.Equal(1.35, result.WarnBattV);
        Assert.Equal(1.3, result.UrgentBattV);
        Assert.Equal(30, result.WarnBattP);
        Assert.Equal(20, result.UrgentBattP);
        Assert.False(result.EnableAlerts);
        Assert.False(result.WarnBattQuietNight);
    }

    [Fact]
    public void GetPreferences_WithCustomSettings_ShouldReturnCustomValues()
    {
        // Arrange
        var settings = new Dictionary<string, object?>
        {
            { "warnClock", 45 },
            { "urgentClock", 90 },
            { "warnRes", 15 },
            { "urgentRes", 8 },
            { "enableAlerts", true },
            { "warnBattQuietNight", true }
        };

        // Act
        var result = _pumpAlertService.GetPreferences(settings, dayStart: 7.0, dayEnd: 22.0);

        // Assert
        Assert.Equal(45, result.WarnClock);
        Assert.Equal(90, result.UrgentClock);
        Assert.Equal(15, result.WarnRes);
        Assert.Equal(8, result.UrgentRes);
        Assert.True(result.EnableAlerts);
        Assert.True(result.WarnBattQuietNight);
        Assert.Equal(7.0, result.DayStart);
        Assert.Equal(22.0, result.DayEnd);
    }

    [Fact]
    public void GenerateVisualizationData_ShouldReturnCorrectFormat()
    {
        // Arrange
        var statuses = CreateTestStatuses();
        var preferences = new PumpPreferences { Fields = ["reservoir"], RetroFields = ["reservoir", "battery"] };

        var status = _pumpAlertService.BuildPumpStatus(statuses, TestTime, preferences, _mockProfileService.Object);

        // Act
        var result = _pumpAlertService.GenerateVisualizationData(status, preferences, false, TestTime, _mockProfileService.Object);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Pump", result.Label);
        Assert.Equal("current", result.PillClass);
        Assert.Contains("86.4U", result.Value);
    }

    [Fact]
    public void BuildPumpStatus_WithInsuletManufacturer_ShouldDefaultToFiftyPlusUnits()
    {
        // Arrange - Omnipod doesn't report exact reservoir levels
        var statuses = new List<DeviceStatus>
        {
            new DeviceStatus
            {
                Device = "loop://omnipod",
                Mills = TestTime,
                Pump = new PumpStatus
                {
                    Manufacturer = "Insulet",
                    Battery = new PumpBattery { Percent = 80 },
                    Clock = "2015-12-05T19:02:00.000Z"
                    // No reservoir specified
                }
            }
        };
        var preferences = new PumpPreferences();

        // Act
        var result = _pumpAlertService.BuildPumpStatus(statuses, TestTime, preferences, _mockProfileService.Object);

        // Assert
        Assert.NotNull(result.Reservoir);
        Assert.Equal("50+ U", result.Reservoir.Display);
    }

    [Fact]
    public void BuildPumpStatus_WithBatteryPercent_ShouldUsePercentThresholds()
    {
        // Arrange - test battery percentage (not voltage)
        var statuses = new List<DeviceStatus>
        {
            new DeviceStatus
            {
                Device = "loop://omnipod",
                Mills = TestTime,
                Pump = new PumpStatus
                {
                    Battery = new PumpBattery { Percent = 15 }, // Below urgentBattP (20)
                    Reservoir = 50,
                    Clock = "2015-12-05T19:02:00.000Z"
                }
            }
        };
        var preferences = new PumpPreferences { EnableAlerts = true, WarnBattP = 30, UrgentBattP = 20 };

        // Act
        var result = _pumpAlertService.BuildPumpStatus(statuses, TestTime, preferences, _mockProfileService.Object);

        // Assert
        Assert.NotNull(result.Battery);
        Assert.Equal(PumpAlertLevel.Urgent, result.Battery.Level);
        Assert.Equal("URGENT: Pump Battery Low", result.Battery.Message);
    }
}
