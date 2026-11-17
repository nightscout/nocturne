using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
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
    private readonly LegacyAlarmSnoozeStore _snoozes = new(TimeProvider.System);
    private readonly LegacyNotificationStore _notifications = new(TimeProvider.System);
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private readonly NotificationV1Service _service;

    public NotificationV1ServiceTests()
    {
        _mockLogger = new Mock<ILogger<NotificationV1Service>>();
        _mockAuthorizationService = new Mock<IAuthorizationService>();
        _mockSignalRBroadcastService = new Mock<ISignalRBroadcastService>();
        _service = CreateService(TenantA);
    }

    private NotificationV1Service CreateService(Guid tenantId, TimeProvider? time = null) =>
        new(
            _mockLogger.Object,
            _mockAuthorizationService.Object,
            _mockSignalRBroadcastService.Object,
            Mock.Of<ITenantAccessor>(a => a.TenantId == tenantId),
            _snoozes,
            _notifications,
            time ?? TimeProvider.System
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
    public async Task AckNotificationAsync_StampsTheResponseFromTheClock()
    {
        var now = new DateTimeOffset(2026, 10, 29, 3, 4, 5, TimeSpan.Zero);
        var request = new NotificationAckRequest { Level = 1, Group = "default", Time = 1800000 };

        var result = await CreateService(TenantA, new FakeTimeProvider(now)).AckNotificationAsync(request, CancellationToken.None);

        Assert.Equal(now.ToUnixTimeMilliseconds(), result.Timestamp);
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
    public async Task GetAdminNotifiesAsync_NotificationAddedInAnotherRequest_IsStillThere()
    {
        _mockAuthorizationService
            .Setup(a => a.CheckPermissionAsync("admin", "*:*:admin"))
            .ReturnsAsync(true);
        await CreateService(TenantA).AddAdminNotificationAsync(
            new AdminNotification { Title = "t", Message = "kept across requests" },
            CancellationToken.None
        );

        var result = await CreateService(TenantA).GetAdminNotifiesAsync("admin", CancellationToken.None);

        Assert.Equal("kept across requests", Assert.Single(result.Message.Notifies).Message);
    }

    [Fact]
    public async Task GetAdminNotifiesAsync_NotificationOfAnotherTenant_IsNotCountedOrShown()
    {
        _mockAuthorizationService
            .Setup(a => a.CheckPermissionAsync("admin", "*:*:admin"))
            .ReturnsAsync(true);
        await CreateService(TenantA).AddAdminNotificationAsync(
            new AdminNotification { Title = "t", Message = "tenant a only" },
            CancellationToken.None
        );

        var result = await CreateService(TenantB).GetAdminNotifiesAsync("admin", CancellationToken.None);

        Assert.Empty(result.Message.Notifies);
        Assert.Equal(0, result.Message.NotifyCount);
    }

    [Fact]
    public async Task ClearAllAdminNotificationsAsync_ClearsOnlyTheCallersTenant()
    {
        await CreateService(TenantA).AddAdminNotificationAsync(
            new AdminNotification { Title = "t", Message = "a" },
            CancellationToken.None
        );
        await CreateService(TenantB).AddAdminNotificationAsync(
            new AdminNotification { Title = "t", Message = "b" },
            CancellationToken.None
        );

        await CreateService(TenantA).ClearAllAdminNotificationsAsync(CancellationToken.None);

        var a = await CreateService(TenantA).GetAdminNotifiesAsync(null, CancellationToken.None);
        var b = await CreateService(TenantB).GetAdminNotifiesAsync(null, CancellationToken.None);
        Assert.Equal(0, a.Message.NotifyCount);
        Assert.Equal(1, b.Message.NotifyCount);
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
