using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Infrastructure.Data.Entities;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Monitoring;

/// <summary>
/// Integration tests for the alert lifecycle endpoints at
/// <c>/api/v4/alerts</c> covering active alerts, acknowledgement,
/// snooze, history, and delivery workflows.
/// </summary>
[Trait("Category", "Integration")]
public class AlertLifecycleIntegrationTests : ApiIntegrationTestBase
{
    private Guid _tenantId;
    private string _accessToken = null!;

    public AlertLifecycleIntegrationTests(
        ApiIntegrationTestFixture fixture,
        ITestOutputHelper output)
        : base(fixture, output) { }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        // Provision the tenant
        using var client = CreateAuthenticatedClient();
        var response = await client.GetAsync("/api/v1/status");
        response.StatusCode.Should().Be(HttpStatusCode.OK, "tenant provisioning request should succeed");

        // Seed a subject for the alert lifecycle tests
        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        _tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        (_, _accessToken) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(
            conn, _tenantId, "AlertLifecycle Test User");

        Log($"Seeded tenant {_tenantId}");
    }

    [Fact]
    public async Task GetActiveAlerts_NoExcursions_ReturnsEmpty()
    {
        // Arrange
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);

        // Act
        var response = await client.GetAsync("/api/v4/alerts/active");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(content);

        body.ValueKind.Should().Be(JsonValueKind.Array);
        body.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task GetActiveAlerts_WithSeededExcursion_ReturnsIt()
    {
        // Arrange
        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        var ruleId = await AuthTestHelpers.SeedAlertRuleAsync(Fixture, _tenantId);
        var (excursionId, _) = await AuthTestHelpers.SeedAlertExcursionAsync(Fixture, _tenantId, ruleId);

        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);

        // Act
        var response = await client.GetAsync("/api/v4/alerts/active");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(content);

        body.ValueKind.Should().Be(JsonValueKind.Array);
        body.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);

        var excursion = body.EnumerateArray()
            .First(e => e.GetProperty("id").GetString() == excursionId.ToString());
        excursion.GetProperty("alertRuleId").GetString().Should().Be(ruleId.ToString());
    }

    [Fact]
    public async Task AcknowledgeAlerts_SetsAcknowledgedTimestamp()
    {
        // Arrange
        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        var ruleId = await AuthTestHelpers.SeedAlertRuleAsync(Fixture, _tenantId);
        await AuthTestHelpers.SeedAlertExcursionAsync(Fixture, _tenantId, ruleId);

        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);

        // Act
        var ackResponse = await client.PostAsJsonAsync("/api/v4/alerts/acknowledge",
            new { acknowledgedBy = "test-user" });

        // Assert
        ackResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var activeResponse = await client.GetAsync("/api/v4/alerts/active");
        activeResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await activeResponse.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(content);

        // All active excursions should now have acknowledgedAt set
        foreach (var excursion in body.EnumerateArray())
        {
            excursion.GetProperty("acknowledgedAt").ValueKind.Should().NotBe(JsonValueKind.Null,
                "acknowledgedAt should be set after acknowledgement");
        }
    }

    [Fact]
    public async Task SnoozeInstance_ValidInstance_Returns204()
    {
        // Arrange
        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        var ruleId = await AuthTestHelpers.SeedAlertRuleAsync(Fixture, _tenantId);
        var (_, instanceId) = await AuthTestHelpers.SeedAlertExcursionAsync(Fixture, _tenantId, ruleId);

        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);

        // Act
        var response = await client.PostAsJsonAsync(
            $"/api/v4/alerts/instances/{instanceId}/snooze",
            new { minutes = 30 });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task SnoozeInstance_IsReportedOnActiveAlerts()
    {
        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        var ruleId = await AuthTestHelpers.SeedAlertRuleAsync(Fixture, _tenantId);
        var (excursionId, instanceId) = await AuthTestHelpers.SeedAlertExcursionAsync(Fixture, _tenantId, ruleId);

        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var before = DateTime.UtcNow;

        var snooze = await client.PostAsJsonAsync(
            $"/api/v4/alerts/instances/{instanceId}/snooze",
            new { minutes = 30 });
        snooze.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var active = JsonSerializer.Deserialize<JsonElement>(
            await client.GetStringAsync("/api/v4/alerts/active"));
        var excursion = active.EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == excursionId);

        excursion.GetProperty("snoozedUntil").GetDateTime().Should()
            .BeCloseTo(before.AddMinutes(30), TimeSpan.FromMinutes(1));
        excursion.GetProperty("acknowledgedAt").ValueKind.Should().Be(JsonValueKind.Null);
        var instance = excursion.GetProperty("activeInstances").EnumerateArray().Single();
        instance.GetProperty("snoozedUntil").ValueKind.Should().Be(JsonValueKind.String);
        instance.GetProperty("snoozeCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task SnoozeInstance_MaxSnoozesExceeded_Returns409()
    {
        // Arrange
        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        var ruleId = await AuthTestHelpers.SeedAlertRuleAsync(Fixture, _tenantId);
        var (_, instanceId) = await AuthTestHelpers.SeedAlertExcursionAsync(Fixture, _tenantId, ruleId);

        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);

        for (var i = 0; i < SmartSnoozeConfig.DefaultMaxCount; i++)
        {
            var snoozeResponse = await client.PostAsJsonAsync(
                $"/api/v4/alerts/instances/{instanceId}/snooze",
                new { minutes = 30 });
            snoozeResponse.StatusCode.Should().Be(HttpStatusCode.NoContent,
                $"snooze attempt {i + 1} should succeed");
        }

        // Act
        var response = await client.PostAsJsonAsync(
            $"/api/v4/alerts/instances/{instanceId}/snooze",
            new { minutes = 30 });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task SnoozeInstance_NonexistentInstance_Returns404()
    {
        // Arrange
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var randomId = Guid.NewGuid();

        // Act
        var response = await client.PostAsJsonAsync(
            $"/api/v4/alerts/instances/{randomId}/snooze",
            new { minutes = 30 });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetAlertHistory_ReturnsPaginatedResults()
    {
        // Arrange
        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        var ruleId = await AuthTestHelpers.SeedAlertRuleAsync(Fixture, _tenantId);
        var (excursionId, _) = await AuthTestHelpers.SeedAlertExcursionAsync(Fixture, _tenantId, ruleId);

        // Close the excursion so it appears in history
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT set_config('app.current_tenant_id', @tenantId, false);";
            cmd.Parameters.AddWithValue("tenantId", _tenantId.ToString());
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE alert_excursions SET ended_at = now() WHERE id = @excursionId;";
            cmd.Parameters.AddWithValue("excursionId", excursionId);
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "UPDATE alert_instances SET status = 'resolved', resolved_at = now() WHERE alert_excursion_id = @excursionId;";
            cmd.Parameters.AddWithValue("excursionId", excursionId);
            await cmd.ExecuteNonQueryAsync();
        }

        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);

        // Act
        var response = await client.GetAsync("/api/v4/alerts/history?page=1&pageSize=10");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(content);

        body.GetProperty("totalCount").GetInt32().Should().BeGreaterThanOrEqualTo(1);
        body.GetProperty("items").GetArrayLength().Should().BeGreaterThanOrEqualTo(1);

        var item = body.GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("id").GetString() == excursionId.ToString());
        item.GetProperty("endedAt").ValueKind.Should().NotBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task DeliveryLifecycle_PendingToDelivered()
    {
        // Arrange
        var ruleId = await AuthTestHelpers.SeedAlertRuleAsync(Fixture, _tenantId);
        var (_, instanceId) = await AuthTestHelpers.SeedAlertExcursionAsync(Fixture, _tenantId, ruleId);

        // A pending delivery on the rule's channel
        var deliveryId = Guid.CreateVersion7();
        await using (var db = Fixture.CreateDbContext(_tenantId))
        {
            var channel = await db.AlertRuleChannels.SingleAsync(c => c.AlertRuleId == ruleId);
            db.AlertDeliveries.Add(new AlertDeliveryEntity
            {
                Id = deliveryId,
                TenantId = _tenantId,
                AlertInstanceId = instanceId,
                AlertRuleChannelId = channel.Id,
                ChannelType = channel.ChannelType,
                Destination = channel.Destination,
                Payload = """{"alertType":"threshold"}""",
                Status = "pending",
            });
            await db.SaveChangesAsync();
        }

        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);

        // Act
        var response = await client.PostAsJsonAsync(
            $"/api/v4/alerts/deliveries/{deliveryId}/delivered",
            new { platformMessageId = "msg-123", platformThreadId = "thread-456" });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
