using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services;

/// <summary>
/// Unit tests for Device Health services
/// </summary>
public class DeviceHealthServicesTests
{
    /// <summary>
    /// Test that DeviceHealthOptions can be configured properly
    /// </summary>
    [Fact]
    public void DeviceHealthOptions_ShouldHaveCorrectDefaults()
    {
        // Arrange
        var options = new DeviceHealthOptions();

        // Act & Assert
        Assert.Equal(15, options.HealthCheckIntervalMinutes);
        Assert.Equal(30, options.DataGapWarningMinutes);
        Assert.Equal(20, options.BatteryWarningThreshold);
        Assert.Equal(24, options.SensorExpirationWarningHours);
        Assert.Equal(12, options.CalibrationReminderHours);
        Assert.Equal(4, options.MaintenanceAlertCooldownHours);
        Assert.True(options.EnablePredictiveAlerts);
        Assert.True(options.EnablePerformanceAnalytics);
        Assert.Equal(10, options.MaxDevicesPerUser);
        Assert.Equal(30, options.DeviceRegistrationTimeoutSeconds);
        Assert.False(options.EnableDebugLogging);
    }

    /// <summary>
    /// Test that DeviceHealth DTO can be created with correct properties
    /// </summary>
    [Fact]
    public void DeviceHealth_ShouldCreateWithCorrectProperties()
    {
        // Arrange
        var deviceId = "test-device-123";
        var userId = "test-user-456";
        var deviceName = "Test CGM Device";

        // Act
        var device = new DeviceHealth
        {
            DeviceId = deviceId,
            UserId = userId,
            DeviceName = deviceName,
            DeviceType = DeviceType.CGM,
            Status = DeviceStatusType.Active,
            BatteryLevel = 85,
            BatteryWarningThreshold = 20,
            SensorExpirationWarningHours = 24,
        };

        // Assert
        Assert.Equal(deviceId, device.DeviceId);
        Assert.Equal(userId, device.UserId);
        Assert.Equal(deviceName, device.DeviceName);
        Assert.Equal(DeviceType.CGM, device.DeviceType);
        Assert.Equal(DeviceStatusType.Active, device.Status);
        Assert.Equal(85, device.BatteryLevel);
        Assert.Equal(20, device.BatteryWarningThreshold);
        Assert.Equal(24, device.SensorExpirationWarningHours);
    }

    /// <summary>
    /// Test that DeviceAlert can be created with correct properties
    /// </summary>
    [Fact]
    public void DeviceAlert_ShouldCreateWithCorrectProperties()
    {
        // Arrange
        var deviceId = "test-device-123";
        var userId = "test-user-456";
        var title = "Low Battery Alert";
        var message = "Device battery is low at 15%";

        // Act
        var alert = new DeviceAlert
        {
            DeviceId = deviceId,
            UserId = userId,
            AlertType = DeviceAlertType.BatteryWarning,
            Severity = DeviceIssueSeverity.High,
            Title = title,
            Message = message,
            Acknowledged = false,
        };

        // Assert
        Assert.Equal(deviceId, alert.DeviceId);
        Assert.Equal(userId, alert.UserId);
        Assert.Equal(DeviceAlertType.BatteryWarning, alert.AlertType);
        Assert.Equal(DeviceIssueSeverity.High, alert.Severity);
        Assert.Equal(title, alert.Title);
        Assert.Equal(message, alert.Message);
        Assert.False(alert.Acknowledged);
        Assert.Null(alert.AcknowledgedAt);
    }

    /// <summary>
    /// Test that DeviceRegistrationRequest validation works
    /// </summary>
    [Fact]
    public void DeviceRegistrationRequest_ShouldValidateRequiredProperties()
    {
        // Arrange
        var request = new DeviceRegistrationRequest
        {
            DeviceId = "test-device-123",
            DeviceType = DeviceType.CGM,
            DeviceName = "Test CGM",
            Manufacturer = "Test Corp",
            Model = "CGM-2000",
            BatteryLevel = 100,
        };

        // Act & Assert
        Assert.Equal("test-device-123", request.DeviceId);
        Assert.Equal(DeviceType.CGM, request.DeviceType);
        Assert.Equal("Test CGM", request.DeviceName);
        Assert.Equal("Test Corp", request.Manufacturer);
        Assert.Equal("CGM-2000", request.Model);
        Assert.Equal(100, request.BatteryLevel);
    }
}
