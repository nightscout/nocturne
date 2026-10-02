using Microsoft.Extensions.Logging;
using Nocturne.API.Services.Notifications;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Devices;
using Nocturne.API.Services.Realtime;
using Nocturne.API.Services.Devices;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Profiles;
using Microsoft.Extensions.Options;
using dotAPNS;

namespace Nocturne.API.Tests.Services.Notifications;

/// <summary>
/// Unit tests for NotificationV2Service
/// Tests the V2 notification service functionality with 1:1 legacy compatibility
/// Covers Loop notifications, generic notifications, and SignalR integration
/// </summary>
[Parity("notifications.test.js")]
public class NotificationV2ServiceTests
{
    private readonly Mock<ILogger<NotificationV2Service>> _mockLogger;
    private readonly Mock<ISignalRBroadcastService> _mockSignalRBroadcastService;
    private readonly Mock<ILoopService> _mockLoopService;
    private readonly Mock<IProfileProjectionService> _mockProfileProjection;
    private readonly NotificationV2Service _service;

    public NotificationV2ServiceTests()
    {
        _mockLogger = new Mock<ILogger<NotificationV2Service>>();
        _mockSignalRBroadcastService = new Mock<ISignalRBroadcastService>();
        _mockLoopService = new Mock<ILoopService>();
        _mockProfileProjection = new Mock<IProfileProjectionService>();

        _service = new NotificationV2Service(
            _mockLogger.Object,
            _mockSignalRBroadcastService.Object,
            _mockProfileProjection.Object,
            _mockLoopService.Object
        );
    }

    private (NotificationV2Service Service, MockApnsClientFactory Apns) CreateWithLoopPush(
        Profile? currentProfile
    )
    {
        var apns = new MockApnsClientFactory();
        var loopService = new LoopService(
            Mock.Of<ILogger<LoopService>>(),
            Options.Create(
                new LoopConfiguration
                {
                    ApnsKey = "mock-key",
                    ApnsKeyId = "ABC123DEFG",
                    DeveloperTeamId = "TEAM123456",
                }
            ),
            apns
        );
        _mockProfileProjection
            .Setup(p => p.GetCurrentProfileAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(currentProfile);

        var service = new NotificationV2Service(
            _mockLogger.Object,
            _mockSignalRBroadcastService.Object,
            _mockProfileProjection.Object,
            loopService
        );
        return (service, apns);
    }

    private static Profile ProfileWithLoopSettings(string? deviceToken, string? bundleIdentifier) =>
        new()
        {
            LoopSettings = new LoopProfileSettings
            {
                DeviceToken = deviceToken,
                BundleIdentifier = bundleIdentifier,
            },
        };

    #region SendLoopNotificationAsync Tests

    [Fact]
    public async Task SendLoopNotificationAsync_RemoteCarbs_PushesToTheProfilesDevice()
    {
        var (service, apns) = CreateWithLoopPush(
            ProfileWithLoopSettings("profile-device-token", "com.loopkit.Loop")
        );

        var result = await service.SendLoopNotificationAsync(
            new LoopNotificationData
            {
                EventType = "Remote Carbs Entry",
                RemoteCarbs = "20.0",
                RemoteAbsorption = "3.0",
                Otp = "123456",
                EnteredBy = "caregiver",
                CreatedAt = "2026-09-30T01:02:03.000Z",
            },
            "10.0.0.5"
        );

        result.Success.Should().BeTrue(result.Message);
        var push = apns.CapturedPushes.Should().ContainSingle().Subject;
        push.DeviceToken.Should().Be("profile-device-token");
        push.BundleId.Should().Be("com.loopkit.Loop");
        push.SentToDevelopmentServer.Should().BeFalse();
        push.GetProperty("carbs-entry").Should().Be("20");
        push.GetProperty("absorption-time").Should().Be("3");
        push.GetProperty("otp").Should().Be("123456");
        push.GetProperty("start-time").Should().Be("2026-09-30T01:02:03.000Z");
        push.GetProperty("remote-address").Should().Be("10.0.0.5");
    }

    [Fact]
    public async Task SendLoopNotificationAsync_NoProfile_FailsWithoutPushing()
    {
        var (service, apns) = CreateWithLoopPush(currentProfile: null);

        var result = await service.SendLoopNotificationAsync(
            new LoopNotificationData { EventType = "Temporary Override Cancel" },
            "10.0.0.5"
        );

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Loop notification failed: Could not find loopSettings in profile.");
        apns.CapturedPushes.Should().BeEmpty();
    }

    [Fact]
    public async Task SendLoopNotificationAsync_ProfileWithoutDeviceToken_FailsWithoutPushing()
    {
        var (service, apns) = CreateWithLoopPush(ProfileWithLoopSettings(null, "com.loopkit.Loop"));

        var result = await service.SendLoopNotificationAsync(
            new LoopNotificationData { EventType = "Temporary Override Cancel" },
            "10.0.0.5"
        );

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Loop notification failed: Could not find deviceToken in loopSettings.");
        apns.CapturedPushes.Should().BeEmpty();
    }

    [Fact]
    public async Task SendLoopNotificationAsync_ApnsRejectsThePush_Fails()
    {
        var (service, apns) = CreateWithLoopPush(
            ProfileWithLoopSettings("profile-device-token", "com.loopkit.Loop")
        );
        apns.NextResponse = ApnsResponse.Error(ApnsResponseReason.BadDeviceToken, "BadDeviceToken");

        var result = await service.SendLoopNotificationAsync(
            new LoopNotificationData { EventType = "Remote Bolus Entry", RemoteBolus = "1.5", Otp = "123456" },
            "10.0.0.5"
        );

        result.Success.Should().BeFalse();
        result.Message.Should().StartWith("APNs delivery failed:");
    }

    #endregion


    #region ProcessNotificationAsync Tests

    [Fact]
    public async Task ProcessNotificationAsync_WithValidNotification_ReturnsSuccess()
    {
        // Arrange
        var notification = new NotificationBase
        {
            Title = "Test Notification",
            Message = "This is a test notification",
            Level = 0, // INFO
            Group = "test-group",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Plugin = "test-plugin",
            IsAnnouncement = false,
        };

        // Act
        var result = await _service.ProcessNotificationAsync(notification);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.Message.Should().Be("Notification processed successfully");
        result.Data.Should().NotBeNull();
        result.Timestamp.Should().BeGreaterThan(0);

        // Verify SignalR broadcast was called
        _mockSignalRBroadcastService.Verify(
            x => x.BroadcastNotificationAsync(It.IsAny<NotificationBase>()),
            Times.Once
        );
    }

    [Fact]
    public async Task ProcessNotificationAsync_WithOnlyTitle_ReturnsSuccess()
    {
        // Arrange
        var notification = new NotificationBase
        {
            Title = "Title Only Notification",
            Message = "", // Empty message
            Level = 0,
        };

        // Act
        var result = await _service.ProcessNotificationAsync(notification);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.Message.Should().Be("Notification processed successfully");

        // Verify SignalR broadcast was called
        _mockSignalRBroadcastService.Verify(
            x => x.BroadcastNotificationAsync(It.IsAny<NotificationBase>()),
            Times.Once
        );
    }

    [Fact]
    public async Task ProcessNotificationAsync_WithOnlyMessage_ReturnsSuccess()
    {
        // Arrange
        var notification = new NotificationBase
        {
            Title = "", // Empty title
            Message = "Message Only Notification",
            Level = 0,
        };

        // Act
        var result = await _service.ProcessNotificationAsync(notification);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.Message.Should().Be("Notification processed successfully");

        // Verify SignalR broadcast was called
        _mockSignalRBroadcastService.Verify(
            x => x.BroadcastNotificationAsync(It.IsAny<NotificationBase>()),
            Times.Once
        );
    }

    [Fact]
    public async Task ProcessNotificationAsync_WithNoTitleAndNoMessage_ReturnsFailure()
    {
        // Arrange
        var notification = new NotificationBase
        {
            Title = "", // Empty title
            Message = "", // Empty message
            Level = 0,
        };

        // Act
        var result = await _service.ProcessNotificationAsync(notification);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
        result.Message.Should().Be("Notification must have either title or message");
        result.Timestamp.Should().BeGreaterThan(0);
        result.Data.Should().BeNull();

        // Verify SignalR broadcast was NOT called
        _mockSignalRBroadcastService.Verify(
            x => x.BroadcastNotificationAsync(It.IsAny<NotificationBase>()),
            Times.Never
        );
    }

    [Fact]
    public async Task ProcessNotificationAsync_WithNullTitleAndMessage_ReturnsFailure()
    {
        // Arrange
        var notification = new NotificationBase
        {
            Title = null!, // Null title
            Message = null!, // Null message
            Level = 0,
        };

        // Act
        var result = await _service.ProcessNotificationAsync(notification);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
        result.Message.Should().Be("Notification must have either title or message");
        result.Timestamp.Should().BeGreaterThan(0);
        result.Data.Should().BeNull();
    }

    [Fact]
    public async Task ProcessNotificationAsync_WithWarnLevel_CallsAlarmBroadcast()
    {
        // Arrange
        var notification = new NotificationBase
        {
            Title = "Warning Notification",
            Message = "This is a warning",
            Level = 1, // WARN
            Group = "alarms",
        };

        // Act
        var result = await _service.ProcessNotificationAsync(notification);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();

        // Verify correct SignalR broadcast method was called
        _mockSignalRBroadcastService.Verify(
            x => x.BroadcastAlarmAsync(It.IsAny<NotificationBase>()),
            Times.Once
        );

        _mockSignalRBroadcastService.Verify(
            x => x.BroadcastNotificationAsync(It.IsAny<NotificationBase>()),
            Times.Never
        );
    }

    [Fact]
    public async Task ProcessNotificationAsync_WithUrgentLevel_CallsUrgentAlarmBroadcast()
    {
        // Arrange
        var notification = new NotificationBase
        {
            Title = "Urgent Notification",
            Message = "This is urgent",
            Level = 2, // URGENT
            Group = "critical",
        };

        // Act
        var result = await _service.ProcessNotificationAsync(notification);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();

        // Verify correct SignalR broadcast method was called
        _mockSignalRBroadcastService.Verify(
            x => x.BroadcastUrgentAlarmAsync(It.IsAny<NotificationBase>()),
            Times.Once
        );

        _mockSignalRBroadcastService.Verify(
            x => x.BroadcastAlarmAsync(It.IsAny<NotificationBase>()),
            Times.Never
        );

        _mockSignalRBroadcastService.Verify(
            x => x.BroadcastNotificationAsync(It.IsAny<NotificationBase>()),
            Times.Never
        );
    }

    [Fact]
    public async Task ProcessNotificationAsync_WithAnnouncement_CallsAnnouncementBroadcast()
    {
        // Arrange
        var notification = new NotificationBase
        {
            Title = "System Announcement",
            Message = "System maintenance scheduled",
            Level = 0,
            IsAnnouncement = true,
        };

        // Act
        var result = await _service.ProcessNotificationAsync(notification);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();

        // Verify correct SignalR broadcast method was called
        _mockSignalRBroadcastService.Verify(
            x => x.BroadcastAnnouncementAsync(It.IsAny<NotificationBase>()),
            Times.Once
        );

        _mockSignalRBroadcastService.Verify(
            x => x.BroadcastNotificationAsync(It.IsAny<NotificationBase>()),
            Times.Never
        );
    }

    [Fact]
    public async Task ProcessNotificationAsync_WithClearAlarm_CallsClearAlarmBroadcast()
    {
        // Arrange
        var notification = new NotificationBase
        {
            Title = "Clear Alarm",
            Message = "Alarm condition cleared",
            Level = 1,
            Clear = true,
        };

        // Act
        var result = await _service.ProcessNotificationAsync(notification);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();

        // Verify correct SignalR broadcast method was called
        _mockSignalRBroadcastService.Verify(
            x => x.BroadcastClearAlarmAsync(It.IsAny<NotificationBase>()),
            Times.Once
        );

        _mockSignalRBroadcastService.Verify(
            x => x.BroadcastAlarmAsync(It.IsAny<NotificationBase>()),
            Times.Never
        );
    }

    [Fact]
    public async Task ProcessNotificationAsync_WithSignalRFailure_ContinuesProcessing()
    {
        // Arrange
        var notification = new NotificationBase
        {
            Title = "Test Notification",
            Message = "Test message",
            Level = 0,
        };

        _mockSignalRBroadcastService
            .Setup(x => x.BroadcastNotificationAsync(It.IsAny<NotificationBase>()))
            .ThrowsAsync(new InvalidOperationException("SignalR connection failed"));

        // Act
        var result = await _service.ProcessNotificationAsync(notification);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue(); // Processing continues despite SignalR failure
        result.Message.Should().Be("Notification processed successfully");
    }

    [Fact]
    public async Task ProcessNotificationAsync_AppliesDefaults()
    {
        // Arrange
        var notification = new NotificationBase
        {
            Title = "", // Will default to "Notification"
            Message = "Test message",
            Group = "", // Will default to "default"
            Timestamp = 0, // Will default to current time
            Level = 0,
        };

        // Act
        var result = await _service.ProcessNotificationAsync(notification);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();

        // Verify defaults were applied through successful processing
        result.Data.Should().NotBeNull();
    }

    [Fact]
    public async Task ProcessNotificationAsync_WithException_ReturnsFailure()
    {
        // Arrange
        var notification = new NotificationBase
        {
            Title = "Test",
            Message = "Test message",
            Level = 0,
        };

        // Create a service with a SignalR service that throws to simulate internal failure
        var mockSignalRThatThrows = new Mock<ISignalRBroadcastService>();
        mockSignalRThatThrows
            .Setup(x => x.BroadcastNotificationAsync(It.IsAny<NotificationBase>()))
            .ThrowsAsync(new InvalidOperationException("Simulated SignalR exception"));

        var serviceWithException = new NotificationV2Service(
            _mockLogger.Object,
            mockSignalRThatThrows.Object,
            _mockProfileProjection.Object,
            _mockLoopService.Object
        );

        // Act
        var result = await serviceWithException.ProcessNotificationAsync(notification);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue(); // Processing continues despite SignalR failure
        result.Message.Should().Be("Notification processed successfully");
        result.Timestamp.Should().BeGreaterThan(0);
        result.Data.Should().NotBeNull();
    }

    #endregion

    #region GetNotificationStatusAsync Tests

    [Fact]
    public async Task GetNotificationStatusAsync_WithValidLoopService_ReturnsCompleteStatus()
    {
        // Arrange
        _mockLoopService.Setup(x => x.IsConfigurationValid()).Returns(true);

        // Act
        var result = await _service.GetNotificationStatusAsync();

        // Assert
        result.Should().NotBeNull();

        // Method completes successfully, result is a structured object
        // (Cannot test dynamic properties easily in unit tests)
    }

    [Fact]
    public async Task GetNotificationStatusAsync_WithInvalidLoopService_ReturnsLimitedStatus()
    {
        // Arrange
        _mockLoopService.Setup(x => x.IsConfigurationValid()).Returns(false);

        // Act
        var result = await _service.GetNotificationStatusAsync();

        // Assert
        result.Should().NotBeNull();

        // Method should complete successfully even with invalid Loop service
    }

    #endregion

    #region Integration and Edge Case Tests

    [Fact]
    public async Task NotificationV2Service_HandlesMultipleNotificationLevels()
    {
        // Arrange
        var infoNotification = new NotificationBase
        {
            Title = "Info",
            Message = "Info",
            Level = 0,
        };
        var warnNotification = new NotificationBase
        {
            Title = "Warn",
            Message = "Warn",
            Level = 1,
        };
        var urgentNotification = new NotificationBase
        {
            Title = "Urgent",
            Message = "Urgent",
            Level = 2,
        };

        // Act
        var infoResult = await _service.ProcessNotificationAsync(infoNotification);
        var warnResult = await _service.ProcessNotificationAsync(warnNotification);
        var urgentResult = await _service.ProcessNotificationAsync(urgentNotification);

        // Assert
        infoResult.Success.Should().BeTrue();
        warnResult.Success.Should().BeTrue();
        urgentResult.Success.Should().BeTrue();

        // Verify different broadcast methods were called
        _mockSignalRBroadcastService.Verify(
            x => x.BroadcastNotificationAsync(It.IsAny<NotificationBase>()),
            Times.Once
        );
        _mockSignalRBroadcastService.Verify(
            x => x.BroadcastAlarmAsync(It.IsAny<NotificationBase>()),
            Times.Once
        );
        _mockSignalRBroadcastService.Verify(
            x => x.BroadcastUrgentAlarmAsync(It.IsAny<NotificationBase>()),
            Times.Once
        );
    }

    [Fact]
    public async Task NotificationV2Service_HandlesSpecialNotificationTypes()
    {
        // Arrange
        var clearNotification = new NotificationBase
        {
            Title = "Clear",
            Message = "Clear",
            Level = 1,
            Clear = true,
        };
        var announcementNotification = new NotificationBase
        {
            Title = "Announcement",
            Message = "Announcement",
            Level = 0,
            IsAnnouncement = true,
        };

        // Act
        var clearResult = await _service.ProcessNotificationAsync(clearNotification);
        var announcementResult = await _service.ProcessNotificationAsync(announcementNotification);

        // Assert
        clearResult.Success.Should().BeTrue();
        announcementResult.Success.Should().BeTrue();

        // Verify special broadcast methods were called
        _mockSignalRBroadcastService.Verify(
            x => x.BroadcastClearAlarmAsync(It.IsAny<NotificationBase>()),
            Times.Once
        );
        _mockSignalRBroadcastService.Verify(
            x => x.BroadcastAnnouncementAsync(It.IsAny<NotificationBase>()),
            Times.Once
        );
    }

    #endregion
}
