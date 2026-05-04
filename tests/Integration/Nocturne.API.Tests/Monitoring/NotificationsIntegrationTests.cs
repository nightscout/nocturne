using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Monitoring;

/// <summary>
/// Integration tests for the v4 notification endpoints at
/// <c>/api/v4/notifications</c>.
/// </summary>
[Trait("Category", "Integration")]
public class NotificationsIntegrationTests : AspireIntegrationTestBase
{
    private Guid _tenantId;
    private string _accessToken = null!;

    public NotificationsIntegrationTests(
        AspireIntegrationTestFixture fixture,
        ITestOutputHelper output)
        : base(fixture, output) { }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        // Provision the tenant
        using var client = CreateAuthenticatedClient();
        var response = await client.GetAsync("/api/v1/status");
        response.StatusCode.Should().Be(HttpStatusCode.OK, "tenant provisioning request should succeed");

        // Seed a subject
        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        _tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        (_, _accessToken) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, _tenantId, "Notifications Test User");

        Log($"Seeded tenant {_tenantId}");
    }

    private static object CreateNotificationPayload() => new
    {
        type = "test_notification",
        title = "Integration Test Notification",
        category = "General",
        urgency = "Info",
        source = "integration-test",
        actions = new[]
        {
            new { actionId = "test-action", label = "Test Action" }
        }
    };

    [Fact]
    public async Task CreateNotification_ValidPayload_Returns201()
    {
        // Arrange
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);

        // Act
        var response = await client.PostAsJsonAsync("/api/v4/notifications", CreateNotificationPayload());

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var content = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(content);

        body.GetProperty("id").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CreateNotification_Unauthenticated_Returns401()
    {
        // Arrange
        var payload = CreateNotificationPayload();

        // Act
        var response = await ApiClient.PostAsJsonAsync("/api/v4/notifications", payload);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetActiveNotifications_ReturnsCreated()
    {
        // Arrange
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);

        var createResponse = await client.PostAsJsonAsync("/api/v4/notifications", CreateNotificationPayload());
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        // Act
        var response = await client.GetAsync("/api/v4/notifications");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(content);

        body.ValueKind.Should().Be(JsonValueKind.Array);
        body.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task DismissNotification_Returns204()
    {
        // Arrange
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);

        var createResponse = await client.PostAsJsonAsync("/api/v4/notifications", CreateNotificationPayload());
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var createContent = await createResponse.Content.ReadAsStringAsync();
        var created = JsonSerializer.Deserialize<JsonElement>(createContent);
        var notificationId = created.GetProperty("id").GetString();

        // Act
        var deleteResponse = await client.DeleteAsync($"/api/v4/notifications/{notificationId}");

        // Assert
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify it no longer appears in active list
        var getResponse = await client.GetAsync("/api/v4/notifications");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var getContent = await getResponse.Content.ReadAsStringAsync();
        var notifications = JsonSerializer.Deserialize<JsonElement>(getContent);

        var ids = notifications.EnumerateArray()
            .Select(n => n.GetProperty("id").GetString())
            .ToList();
        ids.Should().NotContain(notificationId);
    }

    [Fact]
    public async Task ExecuteAction_ValidAction_Returns204()
    {
        // Arrange
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);

        var createResponse = await client.PostAsJsonAsync("/api/v4/notifications", CreateNotificationPayload());
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var createContent = await createResponse.Content.ReadAsStringAsync();
        var created = JsonSerializer.Deserialize<JsonElement>(createContent);
        var notificationId = created.GetProperty("id").GetString();

        // Act
        var actionResponse = await client.PostAsync(
            $"/api/v4/notifications/{notificationId}/actions/test-action", null);

        // Assert
        actionResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task ExecuteAction_NonexistentNotification_Returns404()
    {
        // Arrange
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var randomId = Guid.NewGuid();

        // Act
        var response = await client.PostAsync(
            $"/api/v4/notifications/{randomId}/actions/test", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
