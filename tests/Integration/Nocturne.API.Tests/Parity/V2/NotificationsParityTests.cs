using System.Net.Http.Json;
using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Parity.V2;

/// <summary>
/// Parity tests for /api/v2/notifications endpoints.
/// V2 Notifications API provides enhanced notifications system:
/// - POST /api/v2/notifications - Process generic notification
/// - POST /api/v2/notifications/loop - Send Loop notification
/// - GET /api/v2/notifications/status - Get notification system status
/// </summary>
public class NotificationsParityTests : ParityTestBase
{
    public NotificationsParityTests(ParityTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    protected override ComparisonOptions GetComparisonOptions()
    {
        // Notification responses contain dynamic timestamps
        return ComparisonOptions.Default.WithIgnoredFields(
            "timestamp",
            "processedAt",
            "sentAt"
        );
    }

    #region GET /api/v2/notifications/status

    [Fact]
    public async Task GetNotificationStatus_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v2/notifications/status");
    }

    #endregion

    #region POST /api/v2/notifications

    [Fact]
    public async Task PostNotification_Empty_ReturnsSameShape()
    {
        var notification = new { };

        await AssertPostParityAsync("/api/v2/notifications", notification);
    }

    [Fact]
    public async Task PostNotification_Simple_ReturnsSameShape()
    {
        var notification = new
        {
            title = "Test Notification",
            message = "This is a test notification message",
            level = 1
        };

        await AssertPostParityAsync("/api/v2/notifications", notification);
    }

    [Fact]
    public async Task PostNotification_WithGroup_ReturnsSameShape()
    {
        var notification = new
        {
            title = "Grouped Notification",
            message = "This notification has a group",
            level = 2,
            group = "alerts"
        };

        await AssertPostParityAsync("/api/v2/notifications", notification);
    }

    [Fact]
    public async Task PostNotification_HighLevel_ReturnsSameShape()
    {
        var notification = new
        {
            title = "Urgent Alert",
            message = "This is an urgent notification",
            level = 3,
            sound = "alarm",
            persistent = true
        };

        await AssertPostParityAsync("/api/v2/notifications", notification);
    }

    [Fact]
    public async Task PostNotification_WithPlugin_ReturnsSameShape()
    {
        var notification = new
        {
            title = "Plugin Notification",
            message = "Notification from a plugin",
            level = 1,
            plugin = "iob",
            isAnnouncement = false
        };

        await AssertPostParityAsync("/api/v2/notifications", notification);
    }

    #endregion

    #region POST /api/v2/notifications/loop

    [Fact]
    public async Task PostLoopNotification_Empty_ReturnsSameShape()
    {
        var request = new { };

        await AssertPostParityAsync("/api/v2/notifications/loop", request);
    }

    [Fact]
    public async Task PostLoopNotification_Simple_ReturnsSameShape()
    {
        var request = new
        {
            title = "Loop Alert",
            message = "Blood glucose update",
            type = "bg"
        };

        await AssertPostParityAsync("/api/v2/notifications/loop", request);
    }

    [Fact]
    public async Task PostLoopNotification_WithDeviceToken_ReturnsSameShape()
    {
        var request = new
        {
            title = "Loop Notification",
            message = "Test loop notification",
            type = "loop",
            deviceToken = "test-device-token-123",
            bundleIdentifier = "com.loopkit.Loop"
        };

        await AssertPostParityAsync("/api/v2/notifications/loop", request);
    }

    [Fact]
    public async Task PostLoopNotification_WithPushoverFields_ReturnsSameShape()
    {
        var request = new
        {
            title = "Pushover Loop Alert",
            message = "Alert with pushover fields",
            type = "loop",
            sound = "pushover",
            priority = 1,
            retry = 60,
            expire = 3600
        };

        await AssertPostParityAsync("/api/v2/notifications/loop", request);
    }

    [Fact]
    public async Task PostLoopNotification_Urgent_ReturnsSameShape()
    {
        var request = new
        {
            title = "URGENT: Low Blood Sugar",
            message = "Blood sugar is critically low at 50 mg/dL",
            type = "urgent",
            level = 3,
            sound = "critical"
        };

        await AssertPostParityAsync("/api/v2/notifications/loop", request);
    }

    #endregion

    #region Error Cases

    [Fact]
    public async Task PostNotification_MissingTitle_ReturnsSameShape()
    {
        var notification = new
        {
            message = "Message without title",
            level = 1
        };

        await AssertPostParityAsync("/api/v2/notifications", notification);
    }

    [Fact]
    public async Task PostNotification_MissingMessage_ReturnsSameShape()
    {
        var notification = new
        {
            title = "Title without message",
            level = 1
        };

        await AssertPostParityAsync("/api/v2/notifications", notification);
    }

    [Fact]
    public async Task PostNotification_InvalidLevel_ReturnsSameShape()
    {
        var notification = new
        {
            title = "Test",
            message = "Test message",
            level = -1  // Invalid level
        };

        await AssertPostParityAsync("/api/v2/notifications", notification);
    }

    #endregion
}
