using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Services;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// Integration tests for Loop notification parity with legacy Nightscout
/// These tests verify that APNS payloads match the legacy loop.js implementation exactly
/// </summary>
[Trait("Category", "Integration")]
public class LoopServiceParityTests
{
    private (LoopService service, MockApnsClientFactory factory) CreateLoopService()
    {
        var logger = new Mock<ILogger<LoopService>>();

        // Create factory that captures push data
        var mockFactory = new MockApnsClientFactory();

        // Configure valid APNS credentials for testing
        var configuration = new LoopConfiguration
        {
            ApnsKey = "mock-key",
            ApnsKeyId = "ABC123DEFG",
            DeveloperTeamId = "TEAM123456",
            PushServerEnvironment = "development",
        };
        var options = Options.Create(configuration);

        return (new LoopService(logger.Object, options, mockFactory), mockFactory);
    }

    private static LoopSettings CreateValidLoopSettings() =>
        new()
        {
            DeviceToken = "test-device-token-1234567890abcdef",
            BundleIdentifier = "com.loopkit.Loop",
        };

    #region Temporary Override Cancel Tests

    /// <summary>
    /// Verifies that Temporary Override Cancel payloads match legacy format
    /// Legacy: payload["cancel-temporary-override"] = "true"
    /// </summary>
    [Parity]
    [Fact]
    public async Task SendNotificationAsync_TemporaryOverrideCancel_MatchesLegacyPayload()
    {
        // Arrange
        var (service, factory) = CreateLoopService();
        var data = new LoopNotificationData
        {
            EventType = "Temporary Override Cancel",
            Notes = "Test cancel note",
            EnteredBy = "TestUser",
        };
        var loopSettings = CreateValidLoopSettings();

        // Act
        var result = await service.SendNotificationAsync(
            data,
            loopSettings,
            "127.0.0.1",
            CancellationToken.None
        );

        // Assert
        Assert.True(result.Success, result.Message);
        var push = factory.LastPush;
        Assert.NotNull(push);

        // Verify device token
        Assert.Equal(loopSettings.DeviceToken, push.DeviceToken);

        // Verify payload matches legacy format
        Assert.Equal("true", push.GetProperty("cancel-temporary-override"));
        Assert.Equal("127.0.0.1", push.GetProperty("remote-address"));
        Assert.Equal("Test cancel note", push.GetProperty("notes"));
        Assert.Equal("TestUser", push.GetProperty("entered-by"));

        // Verify timestamp fields are present (ISO 8601 format)
        Assert.NotNull(push.GetProperty("sent-at"));
        Assert.NotNull(push.GetProperty("expiration"));

        // Verify alert message
        Assert.Contains("Cancel Temporary Override", push.Alert);

        service.Dispose();
    }

    #endregion

    #region Temporary Override Tests

    /// <summary>
    /// Verifies that Temporary Override payloads match legacy format
    /// Legacy: payload["override-name"] = data.reason, payload["override-duration-minutes"] = parseInt(data.duration)
    /// </summary>
    [Parity]
    [Fact]
    public async Task SendNotificationAsync_TemporaryOverride_MatchesLegacyPayload()
    {
        // Arrange
        var (service, factory) = CreateLoopService();
        var data = new LoopNotificationData
        {
            EventType = "Temporary Override",
            Reason = "exercise",
            ReasonDisplay = "Exercise",
            Duration = "60",
            Notes = "Going for a run",
            EnteredBy = "TestUser",
        };
        var loopSettings = CreateValidLoopSettings();

        // Act
        var result = await service.SendNotificationAsync(
            data,
            loopSettings,
            "192.168.1.100",
            CancellationToken.None
        );

        // Assert
        Assert.True(result.Success, result.Message);
        var push = factory.LastPush;
        Assert.NotNull(push);

        // Verify payload matches legacy format
        Assert.Equal("exercise", push.GetProperty("override-name"));
        Assert.Equal("60", push.GetProperty("override-duration-minutes"));
        Assert.Equal("192.168.1.100", push.GetProperty("remote-address"));
        Assert.Equal("Going for a run", push.GetProperty("notes"));
        Assert.Equal("TestUser", push.GetProperty("entered-by"));

        // Verify timestamp fields
        Assert.NotNull(push.GetProperty("sent-at"));
        Assert.NotNull(push.GetProperty("expiration"));

        // Verify alert message includes reasonDisplay
        Assert.Contains("Exercise Temporary Override", push.Alert);

        service.Dispose();
    }

    /// <summary>
    /// Verifies that Temporary Override without duration omits the duration field
    /// Legacy: only adds duration if parseInt(data.duration) > 0
    /// </summary>
    [Parity]
    [Fact]
    public async Task SendNotificationAsync_TemporaryOverrideNoDuration_OmitsDurationField()
    {
        // Arrange
        var (service, factory) = CreateLoopService();
        var data = new LoopNotificationData
        {
            EventType = "Temporary Override",
            Reason = "sleep",
            ReasonDisplay = "Sleep",
            Duration = null, // No duration
            EnteredBy = "TestUser",
        };
        var loopSettings = CreateValidLoopSettings();

        // Act
        await service.SendNotificationAsync(
            data,
            loopSettings,
            "127.0.0.1",
            CancellationToken.None
        );

        // Assert
        var push = factory.LastPush;
        Assert.NotNull(push);

        // Duration should not be present when null
        Assert.Null(push.GetProperty("override-duration-minutes"));

        service.Dispose();
    }

    #endregion

    #region Remote Carbs Entry Tests

    /// <summary>
    /// Verifies that Remote Carbs Entry payloads match legacy format
    /// Legacy: payload["carbs-entry"] = parseFloat(data.remoteCarbs)
    /// </summary>
    [Parity]
    [Fact]
    public async Task SendNotificationAsync_RemoteCarbsEntry_MatchesLegacyPayload()
    {
        // Arrange
        var (service, factory) = CreateLoopService();
        var data = new LoopNotificationData
        {
            EventType = "Remote Carbs Entry",
            RemoteCarbs = "45.5",
            RemoteAbsorption = "4.0",
            Otp = "123456",
            CreatedAt = "2026-01-06T12:00:00Z",
            Notes = "Lunch",
            EnteredBy = "TestUser",
        };
        var loopSettings = CreateValidLoopSettings();

        // Act
        await service.SendNotificationAsync(
            data,
            loopSettings,
            "10.0.0.1",
            CancellationToken.None
        );

        // Assert
        var push = factory.LastPush;
        Assert.NotNull(push);

        // Verify payload matches legacy format
        Assert.Equal("45.5", push.GetProperty("carbs-entry"));
        Assert.Equal("4", push.GetProperty("absorption-time")); // 4.0 hours
        Assert.Equal("123456", push.GetProperty("otp"));
        Assert.Equal("2026-01-06T12:00:00Z", push.GetProperty("start-time"));
        Assert.Equal("10.0.0.1", push.GetProperty("remote-address"));
        Assert.Equal("Lunch", push.GetProperty("notes"));
        Assert.Equal("TestUser", push.GetProperty("entered-by"));

        // Verify alert message
        Assert.Contains("Remote Carbs Entry", push.Alert);
        Assert.Contains("45.5", push.Alert);
        Assert.Contains("grams", push.Alert);

        service.Dispose();
    }

    /// <summary>
    /// Verifies that Remote Carbs Entry uses default absorption time of 3 hours
    /// Legacy: payload["absorption-time"] = 3.0 (default)
    /// </summary>
    [Parity]
    [Fact]
    public async Task SendNotificationAsync_RemoteCarbsEntry_UsesDefaultAbsorption()
    {
        // Arrange
        var (service, factory) = CreateLoopService();
        var data = new LoopNotificationData
        {
            EventType = "Remote Carbs Entry",
            RemoteCarbs = "30",
            RemoteAbsorption = null, // No absorption specified
        };
        var loopSettings = CreateValidLoopSettings();

        // Act
        await service.SendNotificationAsync(
            data,
            loopSettings,
            "127.0.0.1",
            CancellationToken.None
        );

        // Assert
        var push = factory.LastPush;
        Assert.NotNull(push);

        // Should use default 3 hour absorption
        Assert.Equal("3", push.GetProperty("absorption-time"));

        service.Dispose();
    }

    /// <summary>
    /// Verifies that Remote Carbs Entry with zero/invalid carbs returns error
    /// Legacy: completion("Loop remote carbs failed...")
    /// </summary>
    [Parity]
    [Fact]
    public async Task SendNotificationAsync_RemoteCarbsEntryZeroCarbs_ReturnsError()
    {
        // Arrange
        var (service, factory) = CreateLoopService();
        var data = new LoopNotificationData
        {
            EventType = "Remote Carbs Entry",
            RemoteCarbs = "0", // Invalid: must be > 0
        };
        var loopSettings = CreateValidLoopSettings();

        // Act
        var result = await service.SendNotificationAsync(
            data,
            loopSettings,
            "127.0.0.1",
            CancellationToken.None
        );

        // Assert
        Assert.False(result.Success);
        Assert.Contains("carbs", result.Message.ToLowerInvariant());

        service.Dispose();
    }

    #endregion

    #region Remote Bolus Entry Tests

    /// <summary>
    /// Verifies that Remote Bolus Entry payloads match legacy format
    /// Legacy: payload["bolus-entry"] = parseFloat(data.remoteBolus)
    /// </summary>
    [Parity]
    [Fact]
    public async Task SendNotificationAsync_RemoteBolusEntry_MatchesLegacyPayload()
    {
        // Arrange
        var (service, factory) = CreateLoopService();
        var data = new LoopNotificationData
        {
            EventType = "Remote Bolus Entry",
            RemoteBolus = "2.5",
            Otp = "654321",
            Notes = "Correction bolus",
            EnteredBy = "TestUser",
        };
        var loopSettings = CreateValidLoopSettings();

        // Act
        await service.SendNotificationAsync(
            data,
            loopSettings,
            "172.16.0.1",
            CancellationToken.None
        );

        // Assert
        var push = factory.LastPush;
        Assert.NotNull(push);

        // Verify payload matches legacy format
        Assert.Equal("2.5", push.GetProperty("bolus-entry"));
        Assert.Equal("654321", push.GetProperty("otp"));
        Assert.Equal("172.16.0.1", push.GetProperty("remote-address"));
        Assert.Equal("Correction bolus", push.GetProperty("notes"));
        Assert.Equal("TestUser", push.GetProperty("entered-by"));

        // Verify alert message
        Assert.Contains("Remote Bolus Entry", push.Alert);
        Assert.Contains("2.5", push.Alert);
        Assert.Contains("U", push.Alert);

        service.Dispose();
    }

    /// <summary>
    /// Verifies that Remote Bolus Entry with zero/invalid bolus returns error
    /// Legacy: completion("Loop remote bolus failed...")
    /// </summary>
    [Parity]
    [Fact]
    public async Task SendNotificationAsync_RemoteBolusEntryZeroBolus_ReturnsError()
    {
        // Arrange
        var (service, factory) = CreateLoopService();
        var data = new LoopNotificationData
        {
            EventType = "Remote Bolus Entry",
            RemoteBolus = "0", // Invalid: must be > 0
        };
        var loopSettings = CreateValidLoopSettings();

        // Act
        var result = await service.SendNotificationAsync(
            data,
            loopSettings,
            "127.0.0.1",
            CancellationToken.None
        );

        // Assert
        Assert.False(result.Success);
        Assert.Contains("bolus", result.Message.ToLowerInvariant());

        service.Dispose();
    }

    #endregion

    #region Unhandled Event Type Tests

    /// <summary>
    /// Verifies that unhandled event types return error
    /// Legacy: completion("Loop notification failed: Unhandled event type:", data.eventType)
    /// </summary>
    [Parity]
    [Fact]
    public async Task SendNotificationAsync_UnhandledEventType_ReturnsError()
    {
        // Arrange
        var (service, factory) = CreateLoopService();
        var data = new LoopNotificationData { EventType = "Unknown Event Type" };
        var loopSettings = CreateValidLoopSettings();

        // Act
        var result = await service.SendNotificationAsync(
            data,
            loopSettings,
            "127.0.0.1",
            CancellationToken.None
        );

        // Assert
        Assert.False(result.Success);
        Assert.Contains("Unhandled event type", result.Message);

        service.Dispose();
    }

    #endregion

    #region Common Payload Field Tests

    /// <summary>
    /// Verifies that notes and enteredBy are appended to alert message
    /// Legacy: alert += " - " + data.notes, alert += " - " + data.enteredBy
    /// </summary>
    [Parity]
    [Fact]
    public async Task SendNotificationAsync_WithNotesAndEnteredBy_AppendsToAlert()
    {
        // Arrange
        var (service, factory) = CreateLoopService();
        var data = new LoopNotificationData
        {
            EventType = "Temporary Override Cancel",
            Notes = "Important note",
            EnteredBy = "NightscoutWeb",
        };
        var loopSettings = CreateValidLoopSettings();

        // Act
        await service.SendNotificationAsync(
            data,
            loopSettings,
            "127.0.0.1",
            CancellationToken.None
        );

        // Assert
        var push = factory.LastPush;
        Assert.NotNull(push);

        Assert.Contains("Important note", push.Alert);
        Assert.Contains("NightscoutWeb", push.Alert);

        service.Dispose();
    }

    /// <summary>
    /// Verifies that expiration is 5 minutes after sent-at
    /// Legacy: let expiration = new Date(now.getTime() + 5 * 60 * 1000)
    /// </summary>
    [Parity]
    [Fact]
    public async Task SendNotificationAsync_ExpirationIs5MinutesAfterSentAt()
    {
        // Arrange
        var (service, factory) = CreateLoopService();
        var data = new LoopNotificationData { EventType = "Temporary Override Cancel" };
        var loopSettings = CreateValidLoopSettings();

        // Act
        await service.SendNotificationAsync(
            data,
            loopSettings,
            "127.0.0.1",
            CancellationToken.None
        );

        // Assert
        var push = factory.LastPush;
        Assert.NotNull(push);

        var sentAt = DateTimeOffset.Parse(push.GetProperty("sent-at")!);
        var expiration = DateTimeOffset.Parse(push.GetProperty("expiration")!);

        var difference = expiration - sentAt;
        Assert.Equal(5, difference.TotalMinutes, 0.1);

        service.Dispose();
    }

    #endregion
}
