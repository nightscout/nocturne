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
public class TrackerIntegrationTests : ApiIntegrationTestBase
{
    private Guid _tenantId;
    private string _accessToken = null!;

    public TrackerIntegrationTests(
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
        notificationThresholds = new[]
        {
            new
            {
                urgency = "Warn",
                hours = 648,
                displayOrder = 0,
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
            reason = "Completed",
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
        var response = await client.GetAsync(
            $"/api/v4/trackers/instances/upcoming?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}");

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

    // A definition that restarts on the very event it records: the run the start writes must be the
    // only run, not one replaced at once by a second the recorded event starts.
    [Fact]
    public async Task StartAndComplete_RecordTheDefinitionsEvents_WithoutADuplicateRun()
    {
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var created = await client.PostAsJsonAsync("/api/v4/trackers/definitions", new
        {
            name = "G7",
            category = "Sensor",
            lifespanHours = 240,
            mode = "Duration",
            triggerEventTypes = new[] { "Sensor Start" },
            startEventType = "Sensor Start",
            completionEventType = "Sensor Stop",
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var definitionId = JsonSerializer.Deserialize<JsonElement>(await created.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;

        var instanceId = await StartInstanceAndGetIdAsync(client, definitionId);

        var running = (await RunsAsync(client, "/api/v4/trackers/instances", definitionId)).ToList();
        running.Should().ContainSingle().Which.GetProperty("id").GetString().Should().Be(instanceId);

        var completed = await client.PutAsJsonAsync(
            $"/api/v4/trackers/instances/{instanceId}/complete", new { reason = "Completed" });
        completed.StatusCode.Should().Be(HttpStatusCode.OK);

        (await RunsAsync(client, "/api/v4/trackers/instances", definitionId)).Should().BeEmpty();
        (await RunsAsync(client, "/api/v4/trackers/instances/history", definitionId)).Should().ContainSingle();

        var events = await client.GetFromJsonAsync<JsonElement>("/api/v4/observations/device-events?limit=100");
        events.GetProperty("data").EnumerateArray()
            .Where(e => e.GetProperty("app").GetString() == "Nocturne Tracker")
            .Select(e => e.GetProperty("eventType").GetString())
            .Should().BeEquivalentTo(["SensorStart", "SensorStop"]);
    }

    private static async Task<IEnumerable<JsonElement>> RunsAsync(HttpClient client, string path, string definitionId)
    {
        var runs = await client.GetFromJsonAsync<JsonElement>(path);
        return runs.EnumerateArray().Where(r => r.GetProperty("definitionId").GetString() == definitionId).ToList();
    }

    [Fact]
    public async Task DeletingARecordedStartEvent_ResumesTheRunItReplaced()
    {
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var created = await client.PostAsJsonAsync("/api/v4/trackers/definitions", new
        {
            name = "Site",
            category = "Cannula",
            lifespanHours = 72,
            mode = "Duration",
            startEventType = "Site Change",
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var definitionId = JsonSerializer.Deserialize<JsonElement>(await created.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;

        var first = await client.PostAsJsonAsync("/api/v4/trackers/instances",
            new { definitionId, startedAt = DateTime.UtcNow.AddDays(-2) });
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        var firstId = JsonSerializer.Deserialize<JsonElement>(await first.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
        await StartInstanceAndGetIdAsync(client, definitionId);

        var events = await client.GetFromJsonAsync<JsonElement>("/api/v4/observations/device-events?limit=100");
        var latest = events.GetProperty("data").EnumerateArray()
            .Where(e => e.GetProperty("app").GetString() == "Nocturne Tracker")
            .MaxBy(e => e.GetProperty("timestamp").GetDateTime());
        var deleted = await client.DeleteAsync($"/api/v4/observations/device-events/{latest.GetProperty("id").GetString()}");
        deleted.IsSuccessStatusCode.Should().BeTrue(await deleted.Content.ReadAsStringAsync());

        (await RunsAsync(client, "/api/v4/trackers/instances", definitionId))
            .Should().ContainSingle().Which.GetProperty("id").GetString().Should().Be(firstId);
    }
}
