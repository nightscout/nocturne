using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Models.Authorization;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// A key holding only <c>alerts.acknowledge</c> silences alarms and does nothing else. So does a
/// Nightscout token whose role held only <c>notifications:*:ack</c>, which the import stores as a
/// migrated direct grant of the translated scopes on the tenant's membership.
/// </summary>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class AlarmAcknowledgeCredentialIntegrationTests : ApiIntegrationTestBase
{
    private string _token = null!;
    private string _importedToken = null!;

    public AlarmAcknowledgeCredentialIntegrationTests(
        ApiIntegrationTestFixture fixture,
        ITestOutputHelper output)
        : base(fixture, output) { }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        using var client = CreateAuthenticatedClient();
        (await client.GetAsync("/api/v1/status")).StatusCode.Should().Be(HttpStatusCode.OK);

        await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();

        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        var (subjectId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, "Ack Holder");
        var session = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subjectId);

        using var owner = AuthTestHelpers.CreateBearerClient(Fixture, session);
        var response = await owner.PostAsJsonAsync(
            "/api/auth/direct-grants", new { label = "ack only", scopes = new[] { "alerts.acknowledge" } });
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        _token = JsonSerializer.Deserialize<JsonElement>(body).GetProperty("token").GetString()!;

        _importedToken = $"imported-{Guid.NewGuid():N}";
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT set_config('app.current_tenant_id', @tenantId, false);";
            cmd.Parameters.AddWithValue("tenantId", tenantId.ToString());
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO oauth_grants (id, tenant_id, subject_id, grant_type, scopes, label, token_hash, is_migrated, created_at)
                VALUES (@id, @tenantId, @subjectId, @grantType, @scopes, 'imported', @hash, true, now());
                """;
            cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("subjectId", subjectId);
            cmd.Parameters.AddWithValue("grantType", OAuthGrantTypes.Direct);
            cmd.Parameters.AddWithValue("scopes", ScopeTranslator.FromPermissions(["notifications:*:ack"]).ToArray());
            cmd.Parameters.AddWithValue("hash", HashUtils.Sha256Hex(_importedToken));
            await cmd.ExecuteNonQueryAsync();
        }
    }

    [Theory]
    [InlineData("/api/v1/notifications/ack?level=2&group=default&time=60000")]
    [InlineData("/api/v2/notifications/ack?level=2&group=default&time=60000")]
    public async Task An_imported_ack_only_token_acknowledges(string endpoint)
    {
        using var key = AuthTestHelpers.CreateBearerClient(Fixture, _importedToken);

        var response = await key.GetAsync(endpoint);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_imported_ack_only_token_cannot_raise_a_notification()
    {
        using var key = AuthTestHelpers.CreateBearerClient(Fixture, _importedToken);

        (await key.PostAsJsonAsync("/api/v2/notifications", new { level = 2, title = "t", message = "m" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("bearer", "/api/v1/notifications/ack?level=2&group=default&time=60000")]
    [InlineData("bearer", "/api/v2/notifications/ack?level=2&group=default&time=60000")]
    [InlineData("api-secret", "/api/v1/notifications/ack?level=2&group=default&time=60000")]
    [InlineData("api-secret", "/api/v2/notifications/ack?level=2&group=default&time=60000")]
    public async Task Acknowledges_an_alarm(string presentation, string endpoint)
    {
        using var key = Client(presentation);

        var response = await key.GetAsync(endpoint);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadAsStringAsync()).Should().Be("OK");
    }

    [Theory]
    [InlineData("bearer")]
    [InlineData("api-secret")]
    public async Task Reaches_the_v4_snooze(string presentation)
    {
        using var key = Client(presentation);

        var response = await key.PostAsJsonAsync(
            $"/api/v4/alerts/instances/{Guid.CreateVersion7()}/snooze", new { minutes = 15 });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "the gate admits it and no such instance exists");
    }

    [Theory]
    [InlineData("bearer")]
    [InlineData("api-secret")]
    public async Task Gains_nothing_else(string presentation)
    {
        using var key = Client(presentation);
        var rule = Guid.CreateVersion7();

        (await key.PostAsJsonAsync("/api/v2/notifications", new { level = 2, title = "t", message = "m" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await key.PostAsJsonAsync("/api/v4/alert-rules", new { name = "rule" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await key.PatchAsync($"/api/v4/alert-rules/{rule}/toggle", null))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await key.DeleteAsync($"/api/v4/alert-rules/{rule}"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await key.PostAsJsonAsync($"/api/v4/alerts/excursions/{Guid.CreateVersion7()}/acknowledge", new { }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await key.PostAsJsonAsync("/api/v1/adminnotifies", new { title = "t", message = "m" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await key.GetAsync("/api/v1/entries")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private HttpClient Client(string presentation)
    {
        if (presentation == "bearer")
            return AuthTestHelpers.CreateBearerClient(Fixture, _token);

        var client = Fixture.CreateHttpClient("nocturne-api", "http");
        client.DefaultRequestHeaders.Add("api-secret", _token);
        return client;
    }
}
