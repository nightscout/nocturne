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
/// Integration tests for the v4 tracker endpoints at
/// <c>/api/v4/trackers</c>.
/// </summary>
[Trait("Category", "Integration")]
public class TrackerIntegrationTests : AspireIntegrationTestBase
{
    private Guid _tenantId;
    private string _accessToken = null!;

    public TrackerIntegrationTests(
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
        (_, _accessToken) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, _tenantId, "Tracker Test User");

        Log($"Seeded tenant {_tenantId}");
    }

    private static object CreateDefinitionPayload(string name = "Insulin Pen") => new
    {
        name,
        category = "Consumable",
        icon = "syringe",
        lifespanHours = 720,
        mode = "Duration",
        dashboardVisibility = "Always",
        visibility = "Public",
        notificationThresholds = new[]
        {
            new
            {
                urgency = "Warn",
                hours = 648,
                pushEnabled = true,
                displayOrder = 0,
                repeatIntervalMins = 60,
                maxRepeats = 3,
                respectQuietHours = true
            }
        }
    };

    private static object CreateInstancePayload(string definitionId) => new
    {
        definitionId,
        startNotes = "New pen started"
    };

    private static object CreatePresetPayload(string definitionId) => new
    {
        name = "Quick Pen Start",
        definitionId,
        defaultStartNotes = "Auto-started from preset"
    };

    private async Task<string> CreateDefinitionAndGetIdAsync(HttpClient client, string name = "Insulin Pen")
    {
        var response = await client.PostAsJsonAsync("/api/v4/trackers/definitions", CreateDefinitionPayload(name));
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var content = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(content);
        return body.GetProperty("id").GetString()!;
    }

    private async Task<string> StartInstanceAndGetIdAsync(HttpClient client, string definitionId)
    {
        var response = await client.PostAsJsonAsync("/api/v4/trackers/instances", CreateInstancePayload(definitionId));
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var content = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(content);
        return body.GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task CreateTrackerDefinition_Returns201()
    {
        // Arrange
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);

        // Act
        var response = await client.PostAsJsonAsync("/api/v4/trackers/definitions", CreateDefinitionPayload());

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var content = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(content);

        body.GetProperty("id").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CreateTrackerDefinition_Unauthenticated_Returns401()
    {
        // Arrange
        var payload = CreateDefinitionPayload();

        // Act
        var response = await ApiClient.PostAsJsonAsync("/api/v4/trackers/definitions", payload);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetTrackerDefinitions_ReturnsCreated()
    {
        // Arrange
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        await client.PostAsJsonAsync("/api/v4/trackers/definitions", CreateDefinitionPayload());

        // Act
        var response = await client.GetAsync("/api/v4/trackers/definitions");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(content);

        body.ValueKind.Should().Be(JsonValueKind.Array);
        body.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task StartTrackerInstance_Returns201()
    {
        // Arrange
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var definitionId = await CreateDefinitionAndGetIdAsync(client);

        // Act
        var response = await client.PostAsJsonAsync("/api/v4/trackers/instances", CreateInstancePayload(definitionId));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var content = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(content);

        body.GetProperty("id").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CompleteTrackerInstance_Returns200()
    {
        // Arrange
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var definitionId = await CreateDefinitionAndGetIdAsync(client);
        var instanceId = await StartInstanceAndGetIdAsync(client, definitionId);

        var completePayload = new
        {
            reason = "Manual",
            completionNotes = "Pen used up"
        };

        // Act
        var response = await client.PutAsJsonAsync($"/api/v4/trackers/instances/{instanceId}/complete", completePayload);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetActiveInstances_ReturnsRunning()
    {
        // Arrange
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var definitionId = await CreateDefinitionAndGetIdAsync(client);
        await StartInstanceAndGetIdAsync(client, definitionId);

        // Act
        var response = await client.GetAsync("/api/v4/trackers/instances");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(content);

        body.ValueKind.Should().Be(JsonValueKind.Array);
        body.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task GetUpcomingInstances_FiltersDateRange()
    {
        // Arrange
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var definitionId = await CreateDefinitionAndGetIdAsync(client);
        await StartInstanceAndGetIdAsync(client, definitionId);

        var from = DateTimeOffset.UtcNow.ToString("o");
        var to = DateTimeOffset.UtcNow.AddDays(60).ToString("o");

        // Act
        var response = await client.GetAsync($"/api/v4/trackers/instances/upcoming?from={from}&to={to}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(content);

        body.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task CreatePreset_AndApply_StartsInstance()
    {
        // Arrange
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var definitionId = await CreateDefinitionAndGetIdAsync(client);

        // Create preset
        var presetResponse = await client.PostAsJsonAsync("/api/v4/trackers/presets", CreatePresetPayload(definitionId));
        presetResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var presetContent = await presetResponse.Content.ReadAsStringAsync();
        var preset = JsonSerializer.Deserialize<JsonElement>(presetContent);
        var presetId = preset.GetProperty("id").GetString();

        // Act - apply the preset
        var applyResponse = await client.PostAsync($"/api/v4/trackers/presets/{presetId}/apply", null);
        applyResponse.IsSuccessStatusCode.Should().BeTrue();

        // Assert - an instance should now be active
        var instancesResponse = await client.GetAsync("/api/v4/trackers/instances");
        instancesResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var instancesContent = await instancesResponse.Content.ReadAsStringAsync();
        var instances = JsonSerializer.Deserialize<JsonElement>(instancesContent);

        instances.ValueKind.Should().Be(JsonValueKind.Array);
        instances.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);
    }
}
