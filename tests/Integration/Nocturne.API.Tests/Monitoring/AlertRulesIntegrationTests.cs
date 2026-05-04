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
/// Integration tests for the alert rules CRUD endpoints at
/// <c>/api/v4/alert-rules</c>.
/// </summary>
[Trait("Category", "Integration")]
public class AlertRulesIntegrationTests : AspireIntegrationTestBase
{
    private Guid _tenantId;
    private string _accessToken = null!;

    public AlertRulesIntegrationTests(
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

        // Seed a subject for the alert rule tests
        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        _tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        (_, _accessToken) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, _tenantId, "AlertRules Test User");

        Log($"Seeded tenant {_tenantId}");
    }

    private static object CreateValidAlertRulePayload(string name = "Test High Alert") => new
    {
        name,
        conditionType = "Threshold",
        conditionParams = new { direction = "above", value = 180 },
        hysteresisMinutes = 15,
        confirmationReadings = 2,
        severity = "Normal",
        isEnabled = true,
        sortOrder = 0,
        schedules = new[]
        {
            new
            {
                name = "Default",
                isDefault = true,
                timezone = "UTC",
                escalationSteps = new[]
                {
                    new
                    {
                        stepOrder = 0,
                        delaySeconds = 0,
                        channels = new[]
                        {
                            new
                            {
                                channelType = "WebPush",
                                destination = "default"
                            }
                        }
                    }
                }
            }
        }
    };

    [Fact]
    public async Task CreateAlertRule_ValidPayload_Returns201()
    {
        // Arrange
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var payload = CreateValidAlertRulePayload();

        // Act
        var response = await client.PostAsJsonAsync("/api/v4/alert-rules", payload);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var content = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(content);

        body.GetProperty("id").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("name").GetString().Should().Be("Test High Alert");
    }

    [Fact]
    public async Task CreateAlertRule_Unauthenticated_Returns401()
    {
        // Arrange
        var payload = CreateValidAlertRulePayload();

        // Act - use raw ApiClient with no auth headers
        var response = await ApiClient.PostAsJsonAsync("/api/v4/alert-rules", payload);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateAlertRule_InvalidCondition_Returns400()
    {
        // Arrange
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var payload = new
        {
            name = "Bad Rule",
            conditionType = "InvalidType",
            severity = "Normal",
            isEnabled = true,
            sortOrder = 0,
            schedules = Array.Empty<object>()
        };

        // Act
        var response = await client.PostAsJsonAsync("/api/v4/alert-rules", payload);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetAlertRules_ReturnsCreatedRules()
    {
        // Arrange
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);

        await client.PostAsJsonAsync("/api/v4/alert-rules", CreateValidAlertRulePayload("Rule A"));
        await client.PostAsJsonAsync("/api/v4/alert-rules", CreateValidAlertRulePayload("Rule B"));

        // Act
        var response = await client.GetAsync("/api/v4/alert-rules");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(content);

        body.ValueKind.Should().Be(JsonValueKind.Array);
        var rules = body.EnumerateArray().ToList();
        rules.Should().HaveCountGreaterThanOrEqualTo(2);

        var names = rules.Select(r => r.GetProperty("name").GetString()).ToList();
        names.Should().Contain("Rule A");
        names.Should().Contain("Rule B");
    }

    [Fact]
    public async Task GetAlertRule_ById_ReturnsFullTree()
    {
        // Arrange
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);

        var createResponse = await client.PostAsJsonAsync("/api/v4/alert-rules", CreateValidAlertRulePayload());
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var createContent = await createResponse.Content.ReadAsStringAsync();
        var created = JsonSerializer.Deserialize<JsonElement>(createContent);
        var ruleId = created.GetProperty("id").GetString();

        // Act
        var response = await client.GetAsync($"/api/v4/alert-rules/{ruleId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(content);

        body.GetProperty("schedules").GetArrayLength().Should().BeGreaterThan(0);

        var schedule = body.GetProperty("schedules")[0];
        schedule.GetProperty("escalationSteps").GetArrayLength().Should().BeGreaterThan(0);

        var step = schedule.GetProperty("escalationSteps")[0];
        step.GetProperty("channels").GetArrayLength().Should().BeGreaterThan(0);

        var channel = step.GetProperty("channels")[0];
        channel.GetProperty("channelType").GetString().Should().Be("WebPush");
        channel.GetProperty("destination").GetString().Should().Be("default");
    }

    [Fact]
    public async Task UpdateAlertRule_ModifiesFields()
    {
        // Arrange
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);

        var createResponse = await client.PostAsJsonAsync("/api/v4/alert-rules", CreateValidAlertRulePayload("Original Name"));
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var createContent = await createResponse.Content.ReadAsStringAsync();
        var created = JsonSerializer.Deserialize<JsonElement>(createContent);
        var ruleId = created.GetProperty("id").GetString();

        var updatePayload = CreateValidAlertRulePayload("Updated Name");

        // Act
        var updateResponse = await client.PutAsJsonAsync($"/api/v4/alert-rules/{ruleId}", updatePayload);

        // Assert
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var getResponse = await client.GetAsync($"/api/v4/alert-rules/{ruleId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var getContent = await getResponse.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(getContent);
        body.GetProperty("name").GetString().Should().Be("Updated Name");
    }

    [Fact]
    public async Task DeleteAlertRule_RemovesRule()
    {
        // Arrange
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);

        var createResponse = await client.PostAsJsonAsync("/api/v4/alert-rules", CreateValidAlertRulePayload());
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var createContent = await createResponse.Content.ReadAsStringAsync();
        var created = JsonSerializer.Deserialize<JsonElement>(createContent);
        var ruleId = created.GetProperty("id").GetString();

        // Act
        var deleteResponse = await client.DeleteAsync($"/api/v4/alert-rules/{ruleId}");

        // Assert
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var getResponse = await client.GetAsync($"/api/v4/alert-rules/{ruleId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ToggleAlertRule_FlipsEnabled()
    {
        // Arrange
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);

        var createResponse = await client.PostAsJsonAsync("/api/v4/alert-rules", CreateValidAlertRulePayload());
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var createContent = await createResponse.Content.ReadAsStringAsync();
        var created = JsonSerializer.Deserialize<JsonElement>(createContent);
        var ruleId = created.GetProperty("id").GetString();
        created.GetProperty("isEnabled").GetBoolean().Should().BeTrue();

        // Act - first toggle: enabled -> disabled
        var toggle1 = await client.PatchAsync($"/api/v4/alert-rules/{ruleId}/toggle", null);
        toggle1.StatusCode.Should().Be(HttpStatusCode.OK);

        var toggle1Content = await toggle1.Content.ReadAsStringAsync();
        var toggled1 = JsonSerializer.Deserialize<JsonElement>(toggle1Content);
        toggled1.GetProperty("isEnabled").GetBoolean().Should().BeFalse();

        // Act - second toggle: disabled -> enabled
        var toggle2 = await client.PatchAsync($"/api/v4/alert-rules/{ruleId}/toggle", null);
        toggle2.StatusCode.Should().Be(HttpStatusCode.OK);

        var toggle2Content = await toggle2.Content.ReadAsStringAsync();
        var toggled2 = JsonSerializer.Deserialize<JsonElement>(toggle2Content);
        toggled2.GetProperty("isEnabled").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task AlertRule_TenantIsolated()
    {
        // Arrange - create a rule in tenant A (default tenant)
        using var clientA = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);

        var createResponse = await clientA.PostAsJsonAsync("/api/v4/alert-rules", CreateValidAlertRulePayload("Tenant A Rule"));
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        // Seed tenant B with its own subject
        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        var tenantBId = await AuthTestHelpers.SeedTenantAsync(conn, "tenant-b", "Tenant B");
        var (_, tenantBToken) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantBId, "Tenant B User");

        var baseDomain = AuthTestHelpers.GetBaseDomain(ApiClient);
        using var clientB = AuthTestHelpers.CreateAuthenticatedTenantClient(Fixture, "tenant-b", baseDomain, tenantBToken);

        // Act - list rules from tenant B
        var response = await clientB.GetAsync("/api/v4/alert-rules");

        // Assert - tenant B should not see tenant A's rules
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(content);

        body.ValueKind.Should().Be(JsonValueKind.Array);
        var rules = body.EnumerateArray().ToList();
        rules.Should().BeEmpty("tenant B should not see tenant A's alert rules");
    }

    [Fact]
    public async Task CreateAlertRule_WithEscalationSteps_PersistsChannels()
    {
        // Arrange
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);

        var payload = new
        {
            name = "Multi-Step Alert",
            conditionType = "Threshold",
            conditionParams = new { direction = "above", value = 250 },
            hysteresisMinutes = 10,
            confirmationReadings = 1,
            severity = "Normal",
            isEnabled = true,
            sortOrder = 0,
            schedules = new[]
            {
                new
                {
                    name = "Default",
                    isDefault = true,
                    timezone = "UTC",
                    escalationSteps = new object[]
                    {
                        new
                        {
                            stepOrder = 0,
                            delaySeconds = 0,
                            channels = new[]
                            {
                                new
                                {
                                    channelType = "WebPush",
                                    destination = "default"
                                }
                            }
                        },
                        new
                        {
                            stepOrder = 1,
                            delaySeconds = 300,
                            channels = new[]
                            {
                                new
                                {
                                    channelType = "Webhook",
                                    destination = "https://example.com/webhook"
                                }
                            }
                        }
                    }
                }
            }
        };

        // Act
        var createResponse = await client.PostAsJsonAsync("/api/v4/alert-rules", payload);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var createContent = await createResponse.Content.ReadAsStringAsync();
        var created = JsonSerializer.Deserialize<JsonElement>(createContent);
        var ruleId = created.GetProperty("id").GetString();

        var getResponse = await client.GetAsync($"/api/v4/alert-rules/{ruleId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Assert
        var content = await getResponse.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(content);

        var steps = body.GetProperty("schedules")[0].GetProperty("escalationSteps");
        steps.GetArrayLength().Should().Be(2);

        var step0 = steps.EnumerateArray().First(s => s.GetProperty("stepOrder").GetInt32() == 0);
        var step1 = steps.EnumerateArray().First(s => s.GetProperty("stepOrder").GetInt32() == 1);

        step0.GetProperty("channels")[0].GetProperty("channelType").GetString().Should().Be("WebPush");
        step0.GetProperty("channels")[0].GetProperty("destination").GetString().Should().Be("default");

        step1.GetProperty("channels")[0].GetProperty("channelType").GetString().Should().Be("Webhook");
        step1.GetProperty("channels")[0].GetProperty("destination").GetString().Should().Be("https://example.com/webhook");
    }
}
