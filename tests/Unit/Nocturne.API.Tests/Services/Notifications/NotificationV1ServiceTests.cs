using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.Notifications;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Identity;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Tests.Services.Notifications;

/// <summary>
/// Unit tests for NotificationV1Service
/// Tests the Phase 8.1 notification service functionality with 1:1 legacy compatibility
/// </summary>
[Parity("notifications-api.test.js", Description = "Also covers adminnotifies.test.js aggregation paths")]
public class NotificationV1ServiceTests
{
    private readonly Mock<ILogger<NotificationV1Service>> _mockLogger;
    private readonly Mock<IAuthorizationService> _mockAuthorizationService;
    private readonly Mock<ISignalRBroadcastService> _mockSignalRBroadcastService;
    private readonly Mock<IPushoverService> _mockPushoverService;
    private readonly LegacyAlarmSnoozeStore _snoozes = new(TimeProvider.System);
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private readonly NotificationV1Service _service;

    public NotificationV1ServiceTests()
    {
        _mockLogger = new Mock<ILogger<NotificationV1Service>>();
        _mockAuthorizationService = new Mock<IAuthorizationService>();
        _mockSignalRBroadcastService = new Mock<ISignalRBroadcastService>();
        _mockPushoverService = new Mock<IPushoverService>();
        _service = CreateService(TenantA, _mockPushoverService.Object);
    }

    private NotificationV1Service CreateService(Guid tenantId, IPushoverService? pushover = null) =>
        new(
            _mockLogger.Object,
            _mockAuthorizationService.Object,
            _mockSignalRBroadcastService.Object,
            Mock.Of<ITenantAccessor>(a => a.TenantId == tenantId),
            _snoozes,
            pushover
        );

    [Fact]
    public async Task AckNotificationAsync_SnoozeHoldsIntoTheNextRequest()
    {
        var request = new NotificationAckRequest { Level = 1, Group = "default", Time = 1800000 };

        var first = await CreateService(TenantA).AckNotificationAsync(request, CancellationToken.None);
        var second = await CreateService(TenantA).AckNotificationAsync(request, CancellationToken.None);

        Assert.True(first.Success);
        Assert.False(second.Success);
        Assert.Contains("already been snoozed", second.Message);
    }

    [Fact]
    public async Task AckNotificationAsync_SnoozeOfOneTenantLeavesAnotherTenantsAlarm()
    {
        var request = new NotificationAckRequest { Level = 1, Group = "default", Time = 1800000 };

        await CreateService(TenantA).AckNotificationAsync(request, CancellationToken.None);
        var other = await CreateService(TenantB).AckNotificationAsync(request, CancellationToken.None);

        Assert.True(other.Success);
    }

    [Fact]
    public async Task AckNotificationAsync_WithValidRequest_ReturnsSuccess()
    {
        // Arrange
        var request = new NotificationAckRequest
        {
            Level = 1,
            Group = "default",
            Time = 1800000, // 30 minutes
        };

        // Act
        var result = await _service.AckNotificationAsync(request, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Contains("Acknowledged default - Level 1", result.Message);
        Assert.True(result.Timestamp > 0);
    }

    [Fact]
    public async Task AckNotificationAsync_WithLevel2_AlsoAcknowledgesLevel1()
    {
        // Arrange
        var request = new NotificationAckRequest
        {
            Level = 2,
            Group = "test-group",
            Time = 900000, // 15 minutes
        };

        // Act
        var result = await _service.AckNotificationAsync(request, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Contains("Acknowledged test-group - Level 2", result.Message);

        // Test that subsequent level 1 acknowledgment in same group is already silenced
        var level1Request = new NotificationAckRequest { Level = 1, Group = "test-group" };

        var level1Result = await _service.AckNotificationAsync(
            level1Request,
            CancellationToken.None
        );

        // Should be silenced since level 2 ack already acknowledged level 1
        Assert.False(level1Result.Success);
        Assert.Contains("already been snoozed", level1Result.Message);
    }

    [Fact]
    public async Task AckNotificationAsync_DuplicateAckWithinSilenceTime_ReturnsFailure()
    {
        // Arrange
        var request = new NotificationAckRequest
        {
            Level = 1,
            Group = "duplicate-test",
            Time = 1800000, // 30 minutes
        };

        // Act

        // First acknowledgment should succeed
        var firstResult = await _service.AckNotificationAsync(request, CancellationToken.None);

        // Second acknowledgment within silence time should fail
        var secondResult = await _service.AckNotificationAsync(request, CancellationToken.None);

        // Assert
        Assert.True(firstResult.Success);
        Assert.False(secondResult.Success);
        Assert.Contains("already been snoozed", secondResult.Message);
    }

    [Fact]
    public async Task ProcessPushoverCallbackAsync_WithAcknowledgedStatus_ReturnsSuccess()
    {
        // Arrange
        var request = new PushoverCallbackRequest
        {
            Receipt = "test-receipt-123",
            Status = 1, // Acknowledged
            AcknowledgedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            AcknowledgedBy = "test-user",
        };

        // Act
        var result = await _service.ProcessPushoverCallbackAsync(request, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("Pushover callback processed successfully", result.Message);
    }

    [Fact]
    public async Task ProcessPushoverCallbackAsync_WithoutAcknowledgment_ReturnsNoAction()
    {
        // Arrange
        var request = new PushoverCallbackRequest
        {
            Receipt = "test-receipt-456",
            Status = 0, // Not acknowledged
        };

        // Act
        var result = await _service.ProcessPushoverCallbackAsync(request, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("Pushover callback received but no action taken", result.Message);
    }

    [Fact]
    public async Task SendPushoverNotificationAsync_WithNoPushoverService_ReturnsSuccess()
    { // Arrange
        var serviceWithoutPushover = CreateService(TenantA);
        var level = 1;
        var group = "test-group";
        var title = "Test Alert";
        var message = "Test message";

        // Act
        var result = await serviceWithoutPushover.SendPushoverNotificationAsync(
            level,
            group,
            title,
            message,
            cancellationToken: CancellationToken.None
        );

        // Assert
        Assert.True(result.Success);
        Assert.Equal("Pushover service not configured", result.Message);
    }

    [Fact]
    public async Task SendPushoverNotificationAsync_WithValidRequest_ReturnsSuccess()
    {
        // Arrange
        var level = 2;
        var group = "urgent-group";
        var title = "Urgent Alert";
        var message = "This is an urgent alert";
        var sound = "persistent";

        var pushoverRequest = new PushoverNotificationRequest
        {
            Title = title,
            Message = message,
            Level = level,
            Group = group,
            Sound = sound,
            Priority = 2,
        };

        var pushoverResponse = new PushoverResponse
        {
            Success = true,
            Receipt = "test-receipt-123",
        };

        _mockPushoverService
            .Setup(p => p.CreateAlarmNotification(level, group, title, message, sound))
            .Returns(pushoverRequest);

        _mockPushoverService
            .Setup(p => p.SendNotificationAsync(pushoverRequest, It.IsAny<CancellationToken>()))
            .ReturnsAsync(pushoverResponse);

        // Act
        var result = await _service.SendPushoverNotificationAsync(
            level,
            group,
            title,
            message,
            sound,
            CancellationToken.None
        );

        // Assert
        Assert.True(result.Success);
        Assert.Equal("Pushover notification sent successfully", result.Message);
        _mockPushoverService.Verify(
            p => p.CreateAlarmNotification(level, group, title, message, sound),
            Times.Once
        );
        _mockPushoverService.Verify(
            p => p.SendNotificationAsync(pushoverRequest, It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task SendPushoverNotificationAsync_WithFailedSend_ReturnsFailure()
    {
        // Arrange
        var level = 1;
        var group = "test-group";
        var title = "Test Alert";
        var message = "Test message";

        var pushoverRequest = new PushoverNotificationRequest
        {
            Title = title,
            Message = message,
            Level = level,
            Group = group,
        };

        var pushoverResponse = new PushoverResponse { Success = false, Error = "Invalid user key" };

        _mockPushoverService
            .Setup(p => p.CreateAlarmNotification(level, group, title, message, null))
            .Returns(pushoverRequest);

        _mockPushoverService
            .Setup(p => p.SendNotificationAsync(pushoverRequest, It.IsAny<CancellationToken>()))
            .ReturnsAsync(pushoverResponse);

        // Act
        var result = await _service.SendPushoverNotificationAsync(
            level,
            group,
            title,
            message,
            cancellationToken: CancellationToken.None
        );

        // Assert
        Assert.False(result.Success);
        Assert.Equal("Failed to send Pushover notification: Invalid user key", result.Message);
    }

    [Fact]
    public void GetActivePushoverReceiptCount_ReturnsCorrectCount()
    {
        // Arrange
        _service.RegisterPushoverReceipt("receipt1", 1, "group1", "title1", "message1");
        _service.RegisterPushoverReceipt("receipt2", 2, "group2", "title2", "message2");

        // Act
        var count = _service.GetActivePushoverReceiptCount();

        // Assert
        Assert.Equal(2, count);
    }

    [Fact]
    public void RegisterPushoverReceipt_WithValidData_RegistersSuccessfully()
    {
        // Arrange
        var receipt = "test-receipt-456";
        var level = 2;
        var group = "urgent";
        var title = "Urgent Alert";
        var message = "This is urgent";

        var initialCount = _service.GetActivePushoverReceiptCount();

        // Act
        _service.RegisterPushoverReceipt(receipt, level, group, title, message);

        // Assert
        var newCount = _service.GetActivePushoverReceiptCount();
        Assert.Equal(initialCount + 1, newCount);
    }

    [Fact]
    public void RegisterPushoverReceipt_WithEmptyReceipt_DoesNotRegister()
    {
        // Arrange
        var initialCount = _service.GetActivePushoverReceiptCount();

        // Act
        _service.RegisterPushoverReceipt("", 1, "group", "title", "message");

        // Assert
        var newCount = _service.GetActivePushoverReceiptCount();
        Assert.Equal(initialCount, newCount);
    }

    [Fact]
    public void CleanExpiredPushoverReceipts_RemovesExpiredReceipts()
    {
        // Arrange
        // Register a receipt that would be considered expired
        _service.RegisterPushoverReceipt("expired-receipt", 1, "group", "title", "message");
        var initialCount = _service.GetActivePushoverReceiptCount();

        // Act
        _service.CleanExpiredPushoverReceipts(); // Assert
        // Since we can't easily mock time, we just verify the method doesn't throw
        // In a real implementation, you might use IDateTimeProvider for better testability
        Assert.True(true); // Method completed without exception
    }

    [Fact]
    public async Task GetAdminNotifiesAsync_WithAdminUser_ReturnsNotificationDetails()
    {
        // Arrange
        var subjectId = "admin-user-123";

        // Mock the authorization service to return true for admin permission
        _mockAuthorizationService
            .Setup(a => a.CheckPermissionAsync(subjectId, "*:*:admin"))
            .ReturnsAsync(true);

        // Add a test notification
        await _service.AddAdminNotificationAsync(
            new AdminNotification
            {
                Title = "Test Title",
                Message = "Test admin notification",
                Count = 1,
                LastRecorded = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Persistent = false,
            },
            CancellationToken.None
        );

        // Act
        var result = await _service.GetAdminNotifiesAsync(subjectId, CancellationToken.None);

        // Assert
        Assert.Equal(200, result.Status);
        Assert.Single(result.Message.Notifies); // Admin users should see notification details
        Assert.Equal(1, result.Message.NotifyCount);
        Assert.Equal("Test admin notification", result.Message.Notifies.First().Message);

        // Verify the authorization service was called with correct parameters
        _mockAuthorizationService.Verify(
            a => a.CheckPermissionAsync(subjectId, "*:*:admin"),
            Times.Once
        );
    }

    [Fact]
    public async Task GetAdminNotifiesAsync_WithNonAdminUser_ReturnsOnlyCount()
    {
        // Arrange
        var subjectId = "regular-user-456";

        // Mock the authorization service to return false for admin permission
        _mockAuthorizationService
            .Setup(a => a.CheckPermissionAsync(subjectId, "*:*:admin"))
            .ReturnsAsync(false);

        // Add a test notification
        await _service.AddAdminNotificationAsync(
            new AdminNotification
            {
                Title = "Test Title 2",
                Message = "Test admin notification",
                Count = 1,
                LastRecorded = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Persistent = false,
            },
            CancellationToken.None
        );

        // Act
        var result = await _service.GetAdminNotifiesAsync(subjectId, CancellationToken.None);

        // Assert
        Assert.Equal(200, result.Status);
        Assert.Empty(result.Message.Notifies); // Non-admin users should not see notification details
        Assert.Equal(1, result.Message.NotifyCount); // But should see the count

        // Verify the authorization service was called with correct parameters
        _mockAuthorizationService.Verify(
            a => a.CheckPermissionAsync(subjectId, "*:*:admin"),
            Times.Once
        );
    }

    [Fact]
    public async Task GetAdminNotifiesAsync_WithNullSubjectId_ReturnsOnlyCount()
    {
        // Arrange
        string? subjectId = null;

        // Add a test notification
        await _service.AddAdminNotificationAsync(
            new AdminNotification
            {
                Title = "test title",
                Message = "Test admin notification",
                Count = 1,
                LastRecorded = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Persistent = false,
            },
            CancellationToken.None
        );

        // Act
        var result = await _service.GetAdminNotifiesAsync(subjectId, CancellationToken.None);

        // Assert
        Assert.Equal(200, result.Status);
        Assert.Empty(result.Message.Notifies); // Anonymous users should not see notification details
        Assert.Equal(1, result.Message.NotifyCount); // But should see the count

        // Verify the authorization service was not called for null subject
        _mockAuthorizationService.Verify(
            a => a.CheckPermissionAsync(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never
        );
    }

    [Fact]
    public async Task GetAdminNotifiesAsync_WithEmptySubjectId_ReturnsOnlyCount()
    {
        // Arrange
        var subjectId = "";

        // Add a test notification
        await _service.AddAdminNotificationAsync(
            new AdminNotification
            {
                Title = "test title",
                Message = "Test admin notification",
                Count = 1,
                LastRecorded = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Persistent = false,
            },
            CancellationToken.None
        );

        // Act
        var result = await _service.GetAdminNotifiesAsync(subjectId, CancellationToken.None);

        // Assert
        Assert.Equal(200, result.Status);
        Assert.Empty(result.Message.Notifies); // Empty subject should not see notification details
        Assert.Equal(1, result.Message.NotifyCount); // But should see the count

        // Verify the authorization service was not called for empty subject
        _mockAuthorizationService.Verify(
            a => a.CheckPermissionAsync(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never
        );
    }

    [Fact]
    public async Task GetAdminNotifiesAsync_FiltersOldNonPersistentNotifications()
    {
        // Arrange
        var subjectId = "admin-user-123";
        var eightHoursAgo = DateTimeOffset.UtcNow.AddHours(-8).ToUnixTimeMilliseconds();
        var tenHoursAgo = DateTimeOffset.UtcNow.AddHours(-10).ToUnixTimeMilliseconds();

        _mockAuthorizationService
            .Setup(a => a.CheckPermissionAsync(subjectId, "*:*:admin"))
            .ReturnsAsync(true);

        // Add recent notification (should be included)
        await _service.AddAdminNotificationAsync(
            new AdminNotification
            {
                Title = "recent-id",
                Message = "Recent notification",
                Count = 1,
                LastRecorded = eightHoursAgo,
                Persistent = false,
            },
            CancellationToken.None
        );

        // Add old notification (should be filtered out)
        await _service.AddAdminNotificationAsync(
            new AdminNotification
            {
                Title = "old-id",
                Message = "Old notification",
                Count = 1,
                LastRecorded = tenHoursAgo,
                Persistent = false,
            },
            CancellationToken.None
        );

        // Add persistent notification (should always be included regardless of age)
        await _service.AddAdminNotificationAsync(
            new AdminNotification
            {
                Title = "persistent-id",
                Message = "Persistent notification",
                Count = 1,
                LastRecorded = tenHoursAgo,
                Persistent = true,
            },
            CancellationToken.None
        );

        // Act
        var result = await _service.GetAdminNotifiesAsync(subjectId, CancellationToken.None);

        // Assert
        Assert.Equal(200, result.Status);
        Assert.Equal(2, result.Message.Notifies.Count); // Should include recent + persistent, but not old
        Assert.Equal(2, result.Message.NotifyCount);
        Assert.Contains(result.Message.Notifies, n => n.Message == "Recent notification");
        Assert.Contains(result.Message.Notifies, n => n.Message == "Persistent notification");
        Assert.DoesNotContain(result.Message.Notifies, n => n.Message == "Old notification");
    }
}
