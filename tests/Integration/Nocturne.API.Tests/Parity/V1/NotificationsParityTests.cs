using System.Net.Http.Json;
using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Parity.V1;

/// <summary>
/// Parity tests for /api/v1/notifications/* and /api/v1/adminnotifies endpoints.
/// Covers notification acknowledgment and admin notifications.
/// </summary>
public class NotificationsParityTests : ParityTestBase
{
    public NotificationsParityTests(ParityTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    #region GET /api/v1/notifications/ack

    [Fact]
    public async Task GetNotificationAck_Simple_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v1/notifications/ack?level=1&group=test-group");
    }

    [Fact]
    public async Task GetNotificationAck_WithAllFields_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v1/notifications/ack?level=2&group=urgent&time=1800000");
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

    #region Error Cases

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
