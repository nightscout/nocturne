using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Nocturne.API.Tests.Integration.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Hubs;

/// <summary>
/// Integration tests for AlertHub SignalR functionality.
/// Tests real-time alert subscriptions, acknowledgement broadcasts, tenant isolation, and snooze.
/// </summary>
[Trait("Category", "Integration")]
public class AlertHubIntegrationTests : AspireIntegrationTestBase
{
    private Guid _tenantId;
    private string _accessToken = null!;

    public AlertHubIntegrationTests(
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

        // Seed a subject for the alert hub tests
        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        _tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        (_, _accessToken) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, _tenantId, "AlertHub Test User");

        Log($"Seeded tenant {_tenantId}");
    }

    private HubConnection CreateAlertHubConnection(string? apiSecret = null)
    {
        var baseAddress = ApiClient.BaseAddress
            ?? throw new InvalidOperationException("API client base address is not configured");

        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(baseAddress, "hubs/alerts"), options =>
            {
                if (apiSecret != null)
                    options.Headers.Add("api-secret", apiSecret);
            })
            .ConfigureLogging(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Warning);
            })
            .Build();

        HubConnections.Add(connection);
        return connection;
    }

    [Fact]
    public async Task Subscribe_ReceivesConnection()
    {
        // Arrange
        var connection = CreateAlertHubConnection(TestApiSecret);

        // Act
        await connection.StartAsync();
        await connection.InvokeAsync("Subscribe");

        // Assert
        connection.State.Should().Be(HubConnectionState.Connected);
        Log("Subscribe completed successfully");
    }

    [Fact]
    public async Task Subscribe_Unauthenticated_Rejected()
    {
        // Arrange — no api-secret header
        var connection = CreateAlertHubConnection(apiSecret: null);

        // Act & Assert — expect either StartAsync or Subscribe to throw
        var rejected = false;
        try
        {
            await connection.StartAsync();
            await connection.InvokeAsync("Subscribe");
        }
        catch (Exception ex)
        {
            rejected = true;
            Log($"Connection rejected as expected: {ex.Message}");
        }

        rejected.Should().BeTrue("unauthenticated connections should be rejected");
    }

    [Fact]
    public async Task Acknowledge_BroadcastsToSubscribers()
    {
        // Arrange — seed an alert rule and excursion
        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        var ruleId = await AuthTestHelpers.SeedAlertRuleAsync(conn, _tenantId);
        await AuthTestHelpers.SeedAlertExcursionAsync(conn, _tenantId, ruleId);

        // Connect and subscribe
        var connection = CreateAlertHubConnection(TestApiSecret);
        await connection.StartAsync();
        await connection.InvokeAsync("Subscribe");

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<object>("alert_acknowledged", _ => tcs.TrySetResult(true));

        // Act — POST acknowledge via HTTP
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var response = await client.PostAsJsonAsync("/api/v4/alerts/acknowledge", new { acknowledgedBy = "test-user" });
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Assert — should receive the broadcast within 5 seconds
        var received = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5))) == tcs.Task;
        received.Should().BeTrue("subscriber should receive alert_acknowledged broadcast");
    }

    [Fact]
    public async Task Acknowledge_NoActiveExcursions_NoOp()
    {
        // Arrange — no excursions seeded; subscribe to alert hub
        var connection = CreateAlertHubConnection(TestApiSecret);
        await connection.StartAsync();
        await connection.InvokeAsync("Subscribe");

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<object>("alert_acknowledged", _ => tcs.TrySetResult(true));

        // Act — POST acknowledge with no active excursions
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var response = await client.PostAsJsonAsync("/api/v4/alerts/acknowledge", new { acknowledgedBy = "test-user" });
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Assert — no broadcast should be received (wait 1 second)
        var received = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(1))) == tcs.Task;
        received.Should().BeFalse("no broadcast should occur when there are no active excursions");
    }

    [Fact]
    public async Task Acknowledge_ViaHub_BroadcastsToSubscribers()
    {
        // Arrange — seed an alert rule and excursion
        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        var ruleId = await AuthTestHelpers.SeedAlertRuleAsync(conn, _tenantId);
        await AuthTestHelpers.SeedAlertExcursionAsync(conn, _tenantId, ruleId);

        // Connect and subscribe
        var connection = CreateAlertHubConnection(TestApiSecret);
        await connection.StartAsync();
        await connection.InvokeAsync("Subscribe");

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<object>("alert_acknowledged", _ => tcs.TrySetResult(true));

        // Act — acknowledge via the hub's Acknowledge method
        await connection.InvokeAsync("Acknowledge", "hub-test-user");

        // Assert — should receive the broadcast within 5 seconds
        var received = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5))) == tcs.Task;
        received.Should().BeTrue("subscriber should receive alert_acknowledged broadcast when acknowledging via hub");
    }

    [Fact]
    public async Task MultipleSubscribers_AllReceiveBroadcast()
    {
        // Arrange — seed data
        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        var ruleId = await AuthTestHelpers.SeedAlertRuleAsync(conn, _tenantId);
        await AuthTestHelpers.SeedAlertExcursionAsync(conn, _tenantId, ruleId);

        // Create two connections and subscribe both
        var connection1 = CreateAlertHubConnection(TestApiSecret);
        await connection1.StartAsync();
        await connection1.InvokeAsync("Subscribe");

        var connection2 = CreateAlertHubConnection(TestApiSecret);
        await connection2.StartAsync();
        await connection2.InvokeAsync("Subscribe");

        var tcs1 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tcs2 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection1.On<object>("alert_acknowledged", _ => tcs1.TrySetResult(true));
        connection2.On<object>("alert_acknowledged", _ => tcs2.TrySetResult(true));

        // Act — POST acknowledge
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var response = await client.PostAsJsonAsync("/api/v4/alerts/acknowledge", new { acknowledgedBy = "test-user" });
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Assert — both should receive the broadcast
        var received1 = await Task.WhenAny(tcs1.Task, Task.Delay(TimeSpan.FromSeconds(5))) == tcs1.Task;
        var received2 = await Task.WhenAny(tcs2.Task, Task.Delay(TimeSpan.FromSeconds(5))) == tcs2.Task;

        received1.Should().BeTrue("first subscriber should receive alert_acknowledged broadcast");
        received2.Should().BeTrue("second subscriber should receive alert_acknowledged broadcast");
    }

    [Fact]
    public async Task TenantIsolation_AlertsNotCrossLeaked()
    {
        // Arrange — seed tenant B with its own subject, rule, and excursion
        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        var tenantBId = await AuthTestHelpers.SeedTenantAsync(conn, "alert-hub-tenant-b", "Alert Hub Tenant B");
        var (_, tenantBToken) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantBId, "Tenant B Alert User");

        var ruleId = await AuthTestHelpers.SeedAlertRuleAsync(conn, tenantBId, "Tenant B Rule");
        await AuthTestHelpers.SeedAlertExcursionAsync(conn, tenantBId, ruleId);

        // Connect as tenant A and subscribe
        var connectionA = CreateAlertHubConnection(TestApiSecret);
        await connectionA.StartAsync();
        await connectionA.InvokeAsync("Subscribe");

        var tcsA = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        connectionA.On<object>("alert_acknowledged", _ => tcsA.TrySetResult(true));

        // Act — acknowledge for tenant B using tenant B's authenticated client
        var baseDomain = AuthTestHelpers.GetBaseDomain(ApiClient);
        using var clientB = AuthTestHelpers.CreateAuthenticatedTenantClient(
            Fixture, "alert-hub-tenant-b", baseDomain, tenantBToken);
        var response = await clientB.PostAsJsonAsync("/api/v4/alerts/acknowledge", new { acknowledgedBy = "tenant-b-user" });
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Assert — tenant A should NOT receive the broadcast (wait 2 seconds)
        var receivedByA = await Task.WhenAny(tcsA.Task, Task.Delay(TimeSpan.FromSeconds(2))) == tcsA.Task;
        receivedByA.Should().BeFalse("tenant A should not receive alert_acknowledged from tenant B");
    }

    [Fact]
    public async Task Snooze_Returns204_ForValidInstance()
    {
        // Arrange — seed rule + excursion (which creates an instance)
        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        var ruleId = await AuthTestHelpers.SeedAlertRuleAsync(conn, _tenantId);
        var (_, instanceId) = await AuthTestHelpers.SeedAlertExcursionAsync(conn, _tenantId, ruleId);

        // Act
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var response = await client.PostAsJsonAsync($"/api/v4/alerts/instances/{instanceId}/snooze", new { minutes = 30 });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
