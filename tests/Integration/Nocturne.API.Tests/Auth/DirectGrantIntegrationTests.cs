using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// Integration tests for the Direct Grant token endpoints (POST/GET/DELETE /api/auth/direct-grants).
/// Covers token creation, authentication via noc_ tokens, scope enforcement, revocation, and
/// verification that the list endpoint never leaks plaintext tokens.
/// </summary>
[Trait("Category", "Integration")]
public class DirectGrantIntegrationTests : AspireIntegrationTestBase
{
    private Guid _tenantId;
    private Guid _subjectId;
    private string _accessToken = null!;

    public DirectGrantIntegrationTests(
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

        // Seed a subject for the direct grant tests
        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        _tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        (_subjectId, _accessToken) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, _tenantId, "DirectGrant Test User");

        Log($"Seeded tenant {_tenantId}, subject {_subjectId}");
    }

    [Fact]
    public async Task CreateDirectGrant_Authenticated_ReturnsToken()
    {
        // Arrange
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);

        var payload = new { label = "test-token", scopes = new[] { "entries.read", "treatments.read" } };

        // Act
        var response = await client.PostAsJsonAsync("/api/auth/direct-grants", payload);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(content);

        body.GetProperty("token").GetString().Should().StartWith("noc_");
        Guid.TryParse(body.GetProperty("id").GetString(), out _).Should().BeTrue("id should be a valid GUID");

        Log($"Created direct grant, token prefix: {body.GetProperty("token").GetString()![..10]}...");
    }

    [Fact]
    public async Task CreateDirectGrant_Unauthenticated_Returns401()
    {
        // Arrange
        var payload = new { label = "unauth-token", scopes = new[] { "entries.read" } };

        // Act — use the raw ApiClient with no auth headers
        var response = await ApiClient.PostAsJsonAsync("/api/auth/direct-grants", payload);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateDirectGrant_NoScopes_Returns400()
    {
        // Arrange
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);

        var payload = new { label = "no-scopes-token", scopes = Array.Empty<string>() };

        // Act
        var response = await client.PostAsJsonAsync("/api/auth/direct-grants", payload);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DirectGrantToken_CanAuthenticate()
    {
        // Arrange — create a direct grant token with read scope
        using var authClient = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);

        var payload = new { label = "auth-test-token", scopes = new[] { "entries.read" } };
        var createResponse = await authClient.PostAsJsonAsync("/api/auth/direct-grants", payload);
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var createContent = await createResponse.Content.ReadAsStringAsync();
        var createBody = JsonSerializer.Deserialize<JsonElement>(createContent);
        var nocToken = createBody.GetProperty("token").GetString()!;

        // Act — use the noc_ token as Bearer to read entries
        using var bearerClient = AuthTestHelpers.CreateBearerClient(Fixture, nocToken);
        var response = await bearerClient.GetAsync("/api/v1/entries/current");

        // Assert — should not be 401 or 403
        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);

        Log($"Direct grant token authenticated successfully, status: {response.StatusCode}");
    }

    [Fact]
    public async Task DirectGrantToken_ScopeEnforcement()
    {
        // Arrange — create a direct grant token with entries.read only
        using var authClient = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);

        var payload = new { label = "read-only-token", scopes = new[] { "entries.read" } };
        var createResponse = await authClient.PostAsJsonAsync("/api/auth/direct-grants", payload);
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var createContent = await createResponse.Content.ReadAsStringAsync();
        var createBody = JsonSerializer.Deserialize<JsonElement>(createContent);
        var nocToken = createBody.GetProperty("token").GetString()!;

        // Act — try to POST entries with a read-only token
        using var bearerClient = AuthTestHelpers.CreateBearerClient(Fixture, nocToken);
        var entryPayload = new[]
        {
            new
            {
                type = "sgv",
                sgv = 120,
                direction = "Flat",
                date = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                dateString = DateTime.UtcNow.ToString("o")
            }
        };

        var response = await bearerClient.PostAsJsonAsync("/api/v1/entries", entryPayload);

        // Assert — should be rejected
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);

        Log($"Scope enforcement on write attempt, status: {response.StatusCode}");
    }

    [Fact]
    public async Task RevokeDirectGrant_TokenStopsWorking()
    {
        // Arrange — create a direct grant token and verify it works
        using var authClient = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);

        var payload = new { label = "revoke-test-token", scopes = new[] { "entries.read" } };
        var createResponse = await authClient.PostAsJsonAsync("/api/auth/direct-grants", payload);
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var createContent = await createResponse.Content.ReadAsStringAsync();
        var createBody = JsonSerializer.Deserialize<JsonElement>(createContent);
        var nocToken = createBody.GetProperty("token").GetString()!;
        var grantId = createBody.GetProperty("id").GetString()!;

        // Verify the token works before revocation
        using var bearerClientBefore = AuthTestHelpers.CreateBearerClient(Fixture, nocToken);
        var beforeResponse = await bearerClientBefore.GetAsync("/api/v1/entries/current");
        beforeResponse.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized, "token should work before revocation");

        // Act — revoke the grant
        var deleteResponse = await authClient.DeleteAsync($"/api/auth/direct-grants/{grantId}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Assert — token should no longer authenticate
        using var bearerClientAfter = AuthTestHelpers.CreateBearerClient(Fixture, nocToken);
        var afterResponse = await bearerClientAfter.GetAsync("/api/v1/entries/current");
        afterResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        Log("Revoked direct grant token no longer authenticates");
    }

    [Fact]
    public async Task ListDirectGrants_NeverReturnsToken()
    {
        // Arrange — create a direct grant
        using var authClient = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);

        var payload = new { label = "list-test-token", scopes = new[] { "entries.read" } };
        var createResponse = await authClient.PostAsJsonAsync("/api/auth/direct-grants", payload);
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Act — list all direct grants
        var listResponse = await authClient.GetAsync("/api/auth/direct-grants");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var listContent = await listResponse.Content.ReadAsStringAsync();

        // Assert — the response body should never contain "noc_"
        listContent.Should().NotContain("noc_", "the list endpoint must never return plaintext tokens");

        // Also verify the list contains at least one grant
        var listBody = JsonSerializer.Deserialize<JsonElement>(listContent);
        listBody.GetArrayLength().Should().BeGreaterThan(0);

        Log($"List returned {listBody.GetArrayLength()} grants with no plaintext tokens");
    }
}
