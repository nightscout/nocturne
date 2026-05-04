using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.Monitoring;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Monitoring;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Monitoring;

/// <summary>
/// Tests for PumpAlertService with 1:1 legacy compatibility
/// Tests pump status monitoring and alert functionality from legacy pump.js behavior
/// </summary>
[Parity("pump.test.js")]
public class PumpAlertServiceTests
{
    private readonly Mock<IOpenApsService> _mockOpenApsService;
    private readonly Mock<ITherapySettingsResolver> _mockTherapySettings;
    private readonly Mock<ILogger<PumpAlertService>> _mockLogger;
    private readonly PumpAlertService _pumpAlertService;

    private static readonly long TestTime = DateTimeOffset.Parse("2015-12-05T19:05:00.000Z").ToUnixTimeMilliseconds();

    public PumpAlertServiceTests()
    {
        _mockOpenApsService = new Mock<IOpenApsService>();
        _mockTherapySettings = new Mock<ITherapySettingsResolver>();
        _mockLogger = new Mock<ILogger<PumpAlertService>>();
        _pumpAlertService = new PumpAlertService(_mockOpenApsService.Object, _mockTherapySettings.Object, _mockLogger.Object);
    }

    private static PumpSnapshot CreateTestSnapshot(double? reservoir = 86.4, double? voltage = 1.52)
    {
        return new PumpSnapshot
        {
            Device = "openaps://abusypi",
            Timestamp = DateTime.Parse("2015-12-05T19:05:00.000Z").ToUniversalTime(),
            BatteryVoltage = voltage,
            PumpStatus = "normal",
            Bolusing = false,
            Suspended = false,
            Reservoir = reservoir,
            Clock = "2015-12-05T19:02:00.000Z",
        };
    }

    [Parity]
    [Fact]
    public void SetProperties_WithNormalPump_ShouldSetCorrectLevel()
    {
        var snapshot = CreateTestSnapshot();
        var preferences = new PumpPreferences { EnableAlerts = true };

        var result = _pumpAlertService.BuildPumpStatus(snapshot, TestTime, preferences);

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
        var snapshot = new PumpSnapshot
        {
            Device = "openaps://abusypi",
            Timestamp = DateTime.Parse("2015-12-05T19:05:00.000Z").ToUniversalTime(),
            BatteryVoltage = 1.52,
            PumpStatus = "normal",
            Reservoir = 86.4,
            ReservoirDisplay = "50+U",
            Clock = "2015-12-05T19:02:00.000Z",
        };
        var preferences = new PumpPreferences { EnableAlerts = true };

        var result = _pumpAlertService.BuildPumpStatus(snapshot, TestTime, preferences);

        Assert.NotNull(result.Reservoir);
        Assert.Equal("50+U", result.Reservoir.Display);
    }

    [Parity]
    [Fact]
    public void CheckNotifications_WhenPumpOk_ShouldNotGenerateAlert()
    {
        var snapshot = CreateTestSnapshot();
        var preferences = new PumpPreferences { EnableAlerts = true };

        var status = _pumpAlertService.BuildPumpStatus(snapshot, TestTime, preferences);
        var notification = _pumpAlertService.CheckNotifications(status, preferences, TestTime);

        Assert.Null(notification);
    }

    [Parity]
    [Fact]
    public void CheckNotifications_WhenReservoirLow_ShouldGenerateUrgentAlert()
    {
        var snapshot = CreateTestSnapshot(reservoir: 0.5);
        var preferences = new PumpPreferences { EnableAlerts = true, UrgentRes = 5, WarnRes = 10 };

        var status = _pumpAlertService.BuildPumpStatus(snapshot, TestTime, preferences);
        var notification = _pumpAlertService.CheckNotifications(status, preferences, TestTime);

        Assert.NotNull(notification);
        Assert.Equal((int)PumpAlertLevel.Urgent, notification.Level);
        Assert.Equal("URGENT: Pump Reservoir Low", notification.Title);
    }

    [Parity]
    [Fact]
    public void CheckNotifications_WhenReservoirZero_ShouldGenerateUrgentAlert()
    {
        var snapshot = CreateTestSnapshot(reservoir: 0);
        var preferences = new PumpPreferences { EnableAlerts = true, UrgentRes = 5, WarnRes = 10 };

        var status = _pumpAlertService.BuildPumpStatus(snapshot, TestTime, preferences);
        var notification = _pumpAlertService.CheckNotifications(status, preferences, TestTime);

        Assert.NotNull(notification);
        Assert.Equal((int)PumpAlertLevel.Urgent, notification.Level);
        Assert.Equal("URGENT: Pump Reservoir Low", notification.Title);
    }

    [Parity]
    [Fact]
    public void CheckNotifications_WhenBatteryLow_ShouldGenerateWarnAlert()
    {
        var snapshot = CreateTestSnapshot(voltage: 1.33);
        var preferences = new PumpPreferences { EnableAlerts = true, WarnBattV = 1.35, UrgentBattV = 1.3 };

        var status = _pumpAlertService.BuildPumpStatus(snapshot, TestTime, preferences);
        var notification = _pumpAlertService.CheckNotifications(status, preferences, TestTime);

        Assert.NotNull(notification);
        Assert.Equal((int)PumpAlertLevel.Warn, notification.Level);
        Assert.Equal("Warning, Pump Battery Low", notification.Title);
    }

    [Parity]
    [Fact]
    public void CheckNotifications_WhenBatteryCritical_ShouldGenerateUrgentAlert()
    {
        var snapshot = CreateTestSnapshot(voltage: 1.00);
        var preferences = new PumpPreferences { EnableAlerts = true, WarnBattV = 1.35, UrgentBattV = 1.3 };

        var status = _pumpAlertService.BuildPumpStatus(snapshot, TestTime, preferences);
        var notification = _pumpAlertService.CheckNotifications(status, preferences, TestTime);

        Assert.NotNull(notification);
        Assert.Equal((int)PumpAlertLevel.Urgent, notification.Level);
        Assert.Equal("URGENT: Pump Battery Low", notification.Title);
    }

    [Parity]
    [Fact]
    public void CheckNotifications_QuietNight_ShouldSuppressBatteryAlert()
    {
        var snapshot = CreateTestSnapshot(voltage: 1.00);
        var preferences = new PumpPreferences
        {
            EnableAlerts = true, WarnBattV = 1.35, UrgentBattV = 1.3,
            WarnBattQuietNight = true, DayStart = 24.0, DayEnd = 21.0
        };

        _mockTherapySettings
            .Setup(t => t.GetTimezoneAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("UTC");

        var status = _pumpAlertService.BuildPumpStatus(snapshot, TestTime, preferences);

        Assert.Equal(PumpAlertLevel.None, status.Battery?.Level ?? PumpAlertLevel.None);
    }

    [Parity]
    [Fact]
    public void CheckNotifications_OfflineMarker_ShouldNotGenerateAlert()
    {
        var snapshot = CreateTestSnapshot();
        var staleTime = TestTime + (60 * 60 * 1000);
        var preferences = new PumpPreferences { EnableAlerts = true, UrgentClock = 60, WarnClock = 30 };

        var treatments = new List<Treatment>
        {
            new Treatment { EventType = "OpenAPS Offline", Mills = TestTime, Duration = 60 }
        };

        _mockOpenApsService
            .Setup(o => o.FindOfflineMarker(It.IsAny<IEnumerable<Treatment>>(), It.IsAny<DateTime>()))
            .Returns(treatments[0]);

        var status = _pumpAlertService.BuildPumpStatus(snapshot, staleTime, preferences, treatments);

        Assert.Equal(PumpAlertLevel.None, status.Level);
    }

    [Parity]
    [Fact]
    public void VirtualAssistant_ReservoirHandler_ShouldReturnFormattedResponse()
    {
        var snapshot = CreateTestSnapshot();
        var preferences = new PumpPreferences();

        var status = _pumpAlertService.BuildPumpStatus(snapshot, TestTime, preferences);
        var (title, response) = _pumpAlertService.HandleVirtualAssistantReservoir(status);

        Assert.Equal("Insulin Remaining", title);
        Assert.Equal("You have 86.4 units remaining", response);
    }

    [Parity]
    [Fact]
    public void VirtualAssistant_BatteryHandler_ShouldReturnFormattedResponse()
    {
        var snapshot = CreateTestSnapshot();
        var preferences = new PumpPreferences();

        var status = _pumpAlertService.BuildPumpStatus(snapshot, TestTime, preferences);
        var (title, response) = _pumpAlertService.HandleVirtualAssistantBattery(status);

        Assert.Equal("Pump Battery", title);
        Assert.Equal("Your pump battery is at 1.52 volts", response);
    }

    [Fact]
    public void GetPreferences_WithDefaultSettings_ShouldReturnDefaults()
    {
        var settings = new Dictionary<string, object?>();

        var result = _pumpAlertService.GetPreferences(settings);

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
        var settings = new Dictionary<string, object?>
        {
            { "warnClock", 45 }, { "urgentClock", 90 }, { "warnRes", 15 }, { "urgentRes", 8 },
            { "enableAlerts", true }, { "warnBattQuietNight", true }
        };

        var result = _pumpAlertService.GetPreferences(settings, dayStart: 7.0, dayEnd: 22.0);

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
        var snapshot = CreateTestSnapshot();
        var preferences = new PumpPreferences { Fields = ["reservoir"], RetroFields = ["reservoir", "battery"] };

        var status = _pumpAlertService.BuildPumpStatus(snapshot, TestTime, preferences);
        var result = _pumpAlertService.GenerateVisualizationData(status, preferences, false, TestTime);

        Assert.NotNull(result);
        Assert.Equal("Pump", result.Label);
        Assert.Equal("current", result.PillClass);
        Assert.Contains("86.4U", result.Value);
    }

    [Fact]
    public void BuildPumpStatus_WithInsuletManufacturer_ShouldDefaultToFiftyPlusUnits()
    {
        var snapshot = new PumpSnapshot
        {
            Device = "loop://omnipod",
            Timestamp = DateTime.Parse("2015-12-05T19:05:00.000Z").ToUniversalTime(),
            Manufacturer = "Insulet",
            BatteryPercent = 80,
            Clock = "2015-12-05T19:02:00.000Z",
        };
        var preferences = new PumpPreferences();

        var result = _pumpAlertService.BuildPumpStatus(snapshot, TestTime, preferences);

        Assert.NotNull(result.Reservoir);
        Assert.Equal("50+ U", result.Reservoir.Display);
    }

    [Fact]
    public void BuildPumpStatus_WithBatteryPercent_ShouldUsePercentThresholds()
    {
        var snapshot = new PumpSnapshot
        {
            Device = "loop://omnipod",
            Timestamp = DateTime.Parse("2015-12-05T19:05:00.000Z").ToUniversalTime(),
            BatteryPercent = 15,
            Reservoir = 50,
            Clock = "2015-12-05T19:02:00.000Z",
        };
        var preferences = new PumpPreferences { EnableAlerts = true, WarnBattP = 30, UrgentBattP = 20 };

        var result = _pumpAlertService.BuildPumpStatus(snapshot, TestTime, preferences);

        Assert.NotNull(result.Battery);
        Assert.Equal(PumpAlertLevel.Urgent, result.Battery.Level);
        Assert.Equal("URGENT: Pump Battery Low", result.Battery.Message);
    }
}
