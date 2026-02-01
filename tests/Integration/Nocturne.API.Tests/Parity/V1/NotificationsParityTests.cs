using System.Net.Http.Json;
using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Parity.V1;

/// <summary>
/// Parity tests for /api/v1/notifications/* and /api/v1/adminnotifies endpoints.
/// Covers notification acknowledgment, admin notifications, and Pushover integration.
/// </summary>
public class NotificationsParityTests : ParityTestBase
{
    public NotificationsParityTests(ParityTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    #region POST /api/v1/notifications/ack

    [Fact]
    public async Task PostNotificationAck_Simple_ReturnsSameShape()
    {
        var ack = new
        {
            level = 1,
            group = "test-group",
            silenceTime = 30
        };

        await AssertPostParityAsync("/api/v1/notifications/ack", ack);
    }

    [Fact]
    public async Task PostNotificationAck_WithAllFields_ReturnsSameShape()
    {
        var ack = new
        {
            level = 2,
            group = "urgent",
            silenceTime = 60,
            clear = true
        };

        await AssertPostParityAsync("/api/v1/notifications/ack", ack);
    }

    [Fact]
    public async Task PostNotificationAck_LevelZero_ReturnsSameShape()
    {
        var ack = new
        {
            level = 0,
            group = "info"
        };

        await AssertPostParityAsync("/api/v1/notifications/ack", ack);
    }

    #endregion

    #region GET /api/v1/adminnotifies

    [Fact]
    public async Task GetAdminNotifies_Empty_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v1/adminnotifies");
    }

    [Fact]
    public async Task GetAdminNotifies_WithData_ReturnsSameShape()
    {
        // First create some admin notifications
        var notification = new
        {
            title = "Test Notification",
            message = "This is a test admin notification",
            level = 1,
            group = "admin"
        };

        await NightscoutClient.PostAsJsonAsync("/api/v1/adminnotifies", notification);
        await NocturneClient.PostAsJsonAsync("/api/v1/adminnotifies", notification);

        await AssertGetParityAsync("/api/v1/adminnotifies");
    }

    #endregion

    #region POST /api/v1/adminnotifies

    [Fact]
    public async Task PostAdminNotify_Simple_ReturnsSameShape()
    {
        var notification = new
        {
            title = "Alert Title",
            message = "Alert message content",
            level = 1
        };

        await AssertPostParityAsync("/api/v1/adminnotifies", notification);
    }

    [Fact]
    public async Task PostAdminNotify_WithGroup_ReturnsSameShape()
    {
        var notification = new
        {
            title = "Grouped Alert",
            message = "Alert in a specific group",
            level = 2,
            group = "maintenance"
        };

        await AssertPostParityAsync("/api/v1/adminnotifies", notification);
    }

    [Fact]
    public async Task PostAdminNotify_Persistent_ReturnsSameShape()
    {
        var notification = new
        {
            title = "Persistent Alert",
            message = "This alert persists",
            level = 1,
            persistent = true
        };

        await AssertPostParityAsync("/api/v1/adminnotifies", notification);
    }

    #endregion

    #region DELETE /api/v1/adminnotifies

    [Fact]
    public async Task DeleteAdminNotifies_ByGroup_ReturnsSameShape()
    {
        // First create notifications to delete
        var notification = new
        {
            title = "To Delete",
            message = "Will be deleted",
            level = 1,
            group = "to-delete"
        };

        await NightscoutClient.PostAsJsonAsync("/api/v1/adminnotifies", notification);
        await NocturneClient.PostAsJsonAsync("/api/v1/adminnotifies", notification);

        await AssertDeleteParityAsync("/api/v1/adminnotifies?find[group]=to-delete");
    }

    #endregion

    #region POST /api/v1/notifications/pushovercallback

    [Fact]
    public async Task PostPushoverCallback_Valid_ReturnsSameShape()
    {
        var callback = new
        {
            receipt = "test-receipt-123",
            acknowledged = 1,
            acknowledged_at = TestTimeProvider.GetTestTime().ToUnixTimeSeconds(),
            acknowledged_by = "user123"
        };

        await AssertPostParityAsync("/api/v1/notifications/pushovercallback", callback);
    }

    #endregion

    #region Error Cases

    [Fact]
    public async Task PostNotificationAck_MissingLevel_ReturnsSameShape()
    {
        var ack = new
        {
            group = "test"
        };

        await AssertPostParityAsync("/api/v1/notifications/ack", ack);
    }

    [Fact]
    public async Task PostAdminNotify_Empty_ReturnsSameShape()
    {
        var notification = new { };

        await AssertPostParityAsync("/api/v1/adminnotifies", notification);
    }

    [Fact]
    public async Task PostAdminNotify_MissingTitle_ReturnsSameShape()
    {
        var notification = new
        {
            message = "Message without title",
            level = 1
        };

        await AssertPostParityAsync("/api/v1/adminnotifies", notification);
    }

    #endregion
}
