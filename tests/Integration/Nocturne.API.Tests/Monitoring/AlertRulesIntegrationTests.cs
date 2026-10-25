using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
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
public class AlertRulesIntegrationTests : ApiIntegrationTestBase
{
    private Guid _tenantId;
    private Guid _subjectId;
    private string _accessToken = null!;

    public AlertRulesIntegrationTests(
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

        // Seed a subject for the alert rule tests
        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        _tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        (_subjectId, _accessToken) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, _tenantId, "AlertRules Test User");

        Log($"Seeded tenant {_tenantId}");
    }

    private static object CreateValidAlertRulePayload(string name = "Test High Alert") => new
    {
        name,
        conditionType = "threshold",
        conditionParams = new { direction = "above", value = 180 },
        severity = "warning",
        isEnabled = true,
        sortOrder = 0,
        channels = new[]
        {
            new { channelType = "in_app", destination = "" }
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
            severity = "warning",
            isEnabled = true,
            sortOrder = 0,
            channels = Array.Empty<object>()
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

        body.GetProperty("channels").GetArrayLength().Should().BeGreaterThan(0);

        var channel = body.GetProperty("channels")[0];
        channel.GetProperty("channelType").GetString().Should().Be("in_app");
        channel.GetProperty("destination").GetString().Should().Be(_subjectId.ToString(), "an in-app channel saved blank goes to the member who saved it");
    }

    [Fact]
    public async Task ReadingRules_WithoutAlertsRead_Returns403()
    {
        var ruleId = await AuthTestHelpers.SeedAlertRuleAsync(Fixture, _tenantId);
        using var bearerClient = await CreateScopedBearerClientAsync("glucose.read");

        var list = await bearerClient.GetAsync("/api/v4/alert-rules");
        var single = await bearerClient.GetAsync($"/api/v4/alert-rules/{ruleId}");

        list.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        single.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ReadingRules_WithAlertsRead_Returns200()
    {
        var ruleId = await AuthTestHelpers.SeedAlertRuleAsync(Fixture, _tenantId);
        using var bearerClient = await CreateScopedBearerClientAsync("alerts.read");

        var list = await bearerClient.GetAsync("/api/v4/alert-rules");
        var single = await bearerClient.GetAsync($"/api/v4/alert-rules/{ruleId}");

        list.StatusCode.Should().Be(HttpStatusCode.OK);
        single.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<HttpClient> CreateScopedBearerClientAsync(string scope)
    {
        var sessionToken = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, _subjectId);
        using var authClient = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, sessionToken);
        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(authClient, scope: scope);
        var result = await AuthTestHelpers.ExecutePkceFlowAsync(authClient, clientId, scope: scope);
        return AuthTestHelpers.CreateBearerClient(Fixture, result.AccessToken);
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

        var tenantBSlug = $"tenant-b-{Guid.NewGuid():N}"[..20];
        var tenantBId = await AuthTestHelpers.SeedTenantAsync(Fixture, tenantBSlug, "Tenant B");
        var (_, tenantBToken) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantBId, "Tenant B User");

        var baseDomain = AuthTestHelpers.GetBaseDomain(ApiClient);
        using var clientB = AuthTestHelpers.CreateAuthenticatedTenantClient(Fixture, tenantBSlug, baseDomain, tenantBToken);

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
    public async Task CreateAlertRule_WithSeveralChannels_PersistsThemInOrder()
    {
        // Arrange
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);

        var payload = new
        {
            name = "Multi-Channel Alert",
            conditionType = "threshold",
            conditionParams = new { direction = "above", value = 250 },
            severity = "warning",
            isEnabled = true,
            sortOrder = 0,
            channels = new[]
            {
                new { channelType = "in_app", destination = "" },
                new { channelType = "webhook", destination = "https://example.com/webhook" }
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

        var channels = body.GetProperty("channels").EnumerateArray()
            .OrderBy(c => c.GetProperty("sortOrder").GetInt32())
            .ToList();
        channels.Should().HaveCount(2);

        channels[0].GetProperty("channelType").GetString().Should().Be("in_app");
        channels[0].GetProperty("destination").GetString().Should().Be(_subjectId.ToString(), "an in-app channel saved blank goes to the member who saved it");

        channels[1].GetProperty("channelType").GetString().Should().Be("webhook");
        channels[1].GetProperty("destination").GetString().Should().Be("https://example.com/webhook");
    }

    [Fact]
    public async Task TestFire_InAppChannelSavedBlank_ReachesTheSaversNotificationCentre()
    {
        // Arrange
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var createResponse = await client.PostAsJsonAsync(
            "/api/v4/alert-rules", CreateValidAlertRulePayload("Reaches the saver"));
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var ruleId = created.GetProperty("id").GetGuid();

        // Act
        var fire = await client.PostAsync($"/api/v4/alert-rules/{ruleId}/test-fire", null);

        // Assert
        fire.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var notifications = await client.GetFromJsonAsync<JsonElement>("/api/v4/notifications");
        notifications.EnumerateArray()
            .Select(n => n.GetProperty("title").GetString())
            .Should().Contain("[Test] Reaches the saver");

        await using var db = Fixture.CreateDbContext(_tenantId);
        var deliveries = await db.AlertDeliveries
            .Where(d => db.AlertRuleChannels.Any(c => c.Id == d.AlertRuleChannelId && c.AlertRuleId == ruleId))
            .Select(d => new { d.Status, d.Destination })
            .ToListAsync();
        deliveries.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Status = "delivered", Destination = _subjectId.ToString() });
    }

    [Fact]
    public async Task GetInAppRecipients_ClinicianWithAlertsRead_IsForbidden()
    {
        // Arrange - a member whose only role is clinician, which grants alerts.read
        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        var (clinicianId, token) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, _tenantId, "Clinician");
        var clinicianRole = await AuthTestHelpers.GetTenantRoleIdAsync(conn, _tenantId, "clinician");
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                DELETE FROM subject_roles WHERE subject_id = @subjectId;
                UPDATE tenant_members SET direct_permissions = '[]'::jsonb
                 WHERE tenant_id = @tenantId AND subject_id = @subjectId;
                INSERT INTO tenant_member_roles (id, tenant_member_id, tenant_role_id)
                SELECT gen_random_uuid(), id, @roleId FROM tenant_members
                 WHERE tenant_id = @tenantId AND subject_id = @subjectId;
                """;
            cmd.Parameters.AddWithValue("subjectId", clinicianId);
            cmd.Parameters.AddWithValue("tenantId", _tenantId);
            cmd.Parameters.AddWithValue("roleId", clinicianRole);
            await cmd.ExecuteNonQueryAsync();
        }
        using var client = AuthTestHelpers.CreateBearerClient(Fixture, token);

        // Act
        var rules = await client.GetAsync("/api/v4/alert-rules");
        var recipients = await client.GetAsync("/api/v4/alert-rules/in-app-recipients");

        // Assert
        rules.StatusCode.Should().Be(HttpStatusCode.OK, "the clinician reads alert rules");
        recipients.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetInAppRecipients_GuestLinkWithAlertsRead_IsForbidden()
    {
        // Arrange - a guest link granted alerts.read, activated into a session cookie
        var session = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, _subjectId);
        using var owner = AuthTestHelpers.CreateBearerClient(Fixture, session);
        var link = await owner.PostAsJsonAsync("/api/v4/guest-links", new
        {
            label = "Alerts guest",
            scopes = new[] { "glucose.read", "alerts.read" },
        });
        link.StatusCode.Should().Be(HttpStatusCode.OK);
        var code = (await link.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

        using var guest = Fixture.CreateHttpClient(new HttpClientHandler { UseCookies = true });
        (await guest.PostAsJsonAsync("/api/v4/guest-links/activate", new { code }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Act
        var rules = await guest.GetAsync("/api/v4/alert-rules");
        var recipients = await guest.GetAsync("/api/v4/alert-rules/in-app-recipients");

        // Assert
        rules.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "a guest sees a firing alarm, not the rules behind it, which name delivery channels");
        recipients.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CreateAlertRule_WebPushChannel_IsRejectedWithItsReplacement()
    {
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var payload = new
        {
            name = "Browser push",
            conditionType = "threshold",
            conditionParams = new { direction = "above", value = 180 },
            severity = "warning",
            channels = new[] { new { channelType = "web_push", destination = "" } },
        };

        var response = await client.PostAsJsonAsync("/api/v4/alert-rules", payload);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("in_app");
    }
}
