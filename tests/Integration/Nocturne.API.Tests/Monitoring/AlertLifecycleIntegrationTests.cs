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
/// Integration tests for the alert lifecycle endpoints at
/// <c>/api/v4/alerts</c> covering active alerts, acknowledgement,
/// snooze, history, and delivery workflows.
/// </summary>
[Trait("Category", "Integration")]
public class AlertLifecycleIntegrationTests : AspireIntegrationTestBase
{
    private Guid _tenantId;
    private string _accessToken = null!;

    public AlertLifecycleIntegrationTests(
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

        var ruleId = await AuthTestHelpers.SeedAlertRuleAsync(conn, _tenantId);
        var (excursionId, _) = await AuthTestHelpers.SeedAlertExcursionAsync(conn, _tenantId, ruleId);

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

        var ruleId = await AuthTestHelpers.SeedAlertRuleAsync(conn, _tenantId);
        await AuthTestHelpers.SeedAlertExcursionAsync(conn, _tenantId, ruleId);

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

        var ruleId = await AuthTestHelpers.SeedAlertRuleAsync(conn, _tenantId);
        var (_, instanceId) = await AuthTestHelpers.SeedAlertExcursionAsync(conn, _tenantId, ruleId);

        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);

        // Act
        var response = await client.PostAsJsonAsync(
            $"/api/v4/alerts/instances/{instanceId}/snooze",
            new { minutes = 30 });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task SnoozeInstance_MaxSnoozesExceeded_Returns409()
    {
        // Arrange
        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        var ruleId = await AuthTestHelpers.SeedAlertRuleAsync(conn, _tenantId);
        var (_, instanceId) = await AuthTestHelpers.SeedAlertExcursionAsync(conn, _tenantId, ruleId);

        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);

        // Snooze 5 times (the default max)
        for (var i = 0; i < 5; i++)
        {
            var snoozeResponse = await client.PostAsJsonAsync(
                $"/api/v4/alerts/instances/{instanceId}/snooze",
                new { minutes = 30 });
            snoozeResponse.StatusCode.Should().Be(HttpStatusCode.NoContent,
                $"snooze attempt {i + 1} should succeed");
        }

        // Act - 6th attempt should be rejected
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

        var ruleId = await AuthTestHelpers.SeedAlertRuleAsync(conn, _tenantId);
        var (excursionId, _) = await AuthTestHelpers.SeedAlertExcursionAsync(conn, _tenantId, ruleId);

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
        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        var ruleId = await AuthTestHelpers.SeedAlertRuleAsync(conn, _tenantId);
        var (_, instanceId) = await AuthTestHelpers.SeedAlertExcursionAsync(conn, _tenantId, ruleId);

        // Set RLS context
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT set_config('app.current_tenant_id', @tenantId, false);";
            cmd.Parameters.AddWithValue("tenantId", _tenantId.ToString());
            await cmd.ExecuteNonQueryAsync();
        }

        // Get the escalation step ID
        Guid stepId;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT id FROM alert_escalation_steps WHERE alert_schedule_id = (
                    SELECT id FROM alert_schedules WHERE alert_rule_id = @ruleId LIMIT 1
                ) LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("ruleId", ruleId);
            var result = await cmd.ExecuteScalarAsync()
                         ?? throw new InvalidOperationException(
                             $"No escalation step found for rule {ruleId}.");
            stepId = (Guid)result;
        }

        // Insert a pending delivery
        var deliveryId = Guid.CreateVersion7();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO alert_deliveries (id, tenant_id, alert_instance_id, escalation_step_id, channel_type, destination, payload, status, created_at, retry_count)
                VALUES (@id, @tenantId, @instanceId, @stepId, 'WebPush', 'default', '{"alertType":"threshold"}'::jsonb, 'pending', now(), 0);
                """;
            cmd.Parameters.AddWithValue("id", deliveryId);
            cmd.Parameters.AddWithValue("tenantId", _tenantId);
            cmd.Parameters.AddWithValue("instanceId", instanceId);
            cmd.Parameters.AddWithValue("stepId", stepId);
            await cmd.ExecuteNonQueryAsync();
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
