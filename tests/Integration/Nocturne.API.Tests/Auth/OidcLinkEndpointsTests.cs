using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.API.Tests.Integration.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// Integration tests for the OIDC account linking endpoints (list + unlink).
/// Tests the GET /api/v4/oidc/link/identities and DELETE /api/v4/oidc/link/identities/{id}
/// endpoints against a real API with real PostgreSQL.
///
/// Does NOT test link initiation or callback (those require a real IdP; unit tests cover them).
/// </summary>
[Trait("Category", "Integration")]
public class OidcLinkEndpointsTests : AspireIntegrationTestBase
{
    /// <summary>
    /// Known access token for test subject authentication.
    /// Format: {name}-{hex_digest} (Nightscout legacy access token format).
    /// </summary>
    private const string TestAccessToken = "oidctest-a1b2c3d4e5f6g7h8";

    private Guid _tenantId;

    public OidcLinkEndpointsTests(
        AspireIntegrationTestFixture fixture,
        ITestOutputHelper output)
        : base(fixture, output) { }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        // Ensure tenant is provisioned by making an authenticated request
        using var client = CreateAuthenticatedClient();
        var response = await client.GetAsync("/api/v1/status");
        response.StatusCode.Should().Be(HttpStatusCode.OK, "tenant provisioning request should succeed");

        // Resolve the tenant ID for seeding
        _tenantId = await GetTenantIdAsync();
    }

    #region GetLinkedIdentities Tests

    [Fact]
    public async Task GetLinkedIdentities_Unauthenticated_Returns401()
    {
        // Act - use raw ApiClient (no auth header, no access token)
        var response = await ApiClient.GetAsync("/api/v4/oidc/link/identities");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetLinkedIdentities_Authenticated_ReturnsEmptyList()
    {
        // Arrange - create a subject with access token (no OIDC identities)
        var subjectId = await SeedSubjectWithAccessTokenAsync();

        using var client = CreateAccessTokenClient();

        // Act
        var response = await client.GetAsync("/api/v4/oidc/link/identities");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(content);

        body.TryGetProperty("identities", out var identities).Should().BeTrue();
        identities.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task GetLinkedIdentities_WithSeededData_ReturnsIdentities()
    {
        // Arrange
        var subjectId = await SeedSubjectWithAccessTokenAsync();
        var providerId = await SeedOidcProviderAsync("Test Provider", "https://idp.example.com");
        var identityId = await SeedOidcIdentityAsync(
            subjectId, providerId, "ext-user-123", "https://idp.example.com", "user@example.com");

        using var client = CreateAccessTokenClient();

        // Act
        var response = await client.GetAsync("/api/v4/oidc/link/identities");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(content);

        body.TryGetProperty("identities", out var identities).Should().BeTrue();
        identities.GetArrayLength().Should().Be(1);

        var identity = identities[0];
        identity.GetProperty("id").GetGuid().Should().Be(identityId);
        identity.GetProperty("providerName").GetString().Should().Be("Test Provider");
        identity.GetProperty("email").GetString().Should().Be("user@example.com");
    }

    #endregion

    #region UnlinkIdentity Tests

    [Fact]
    public async Task UnlinkIdentity_Unauthenticated_Returns401()
    {
        // Act - use raw ApiClient (no auth)
        var response = await ApiClient.DeleteAsync(
            $"/api/v4/oidc/link/identities/{Guid.CreateVersion7()}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UnlinkIdentity_NotFound_Returns404()
    {
        // Arrange
        await SeedSubjectWithAccessTokenAsync();
        using var client = CreateAccessTokenClient();

        // Act - delete a nonexistent identity
        var response = await client.DeleteAsync(
            $"/api/v4/oidc/link/identities/{Guid.CreateVersion7()}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UnlinkIdentity_LastPrimaryFactor_Returns409()
    {
        // Arrange - subject with exactly 1 OIDC identity and 0 passkeys
        var subjectId = await SeedSubjectWithAccessTokenAsync();
        var providerId = await SeedOidcProviderAsync("Solo Provider", "https://solo.example.com");
        var identityId = await SeedOidcIdentityAsync(
            subjectId, providerId, "solo-user-1", "https://solo.example.com", "solo@example.com");

        using var client = CreateAccessTokenClient();

        // Act
        var response = await client.DeleteAsync(
            $"/api/v4/oidc/link/identities/{identityId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var content = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(content);

        body.GetProperty("error").GetString().Should().Be("last_factor");
    }

    [Fact]
    public async Task UnlinkIdentity_WithMultipleFactors_Returns204()
    {
        // Arrange - subject with 1 OIDC identity AND 1 passkey
        var subjectId = await SeedSubjectWithAccessTokenAsync();
        var providerId = await SeedOidcProviderAsync("Multi Provider", "https://multi.example.com");
        var identityId = await SeedOidcIdentityAsync(
            subjectId, providerId, "multi-user-1", "https://multi.example.com", "multi@example.com");
        await SeedPasskeyAsync(subjectId);

        using var client = CreateAccessTokenClient();

        // Act
        var response = await client.DeleteAsync(
            $"/api/v4/oidc/link/identities/{identityId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Creates an HttpClient that authenticates via the test access token.
    /// This resolves to a real SubjectId, which the OIDC link endpoints require.
    /// </summary>
    private HttpClient CreateAccessTokenClient()
    {
        var client = Fixture.CreateHttpClient("nocturne-api", "api");
        client.DefaultRequestHeaders.Add("api-secret", TestApiSecret);
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {TestAccessToken}");
        return client;
    }

    /// <summary>
    /// Resolves the test tenant ID from the database.
    /// </summary>
    private async Task<Guid> GetTenantIdAsync()
    {
        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM tenants LIMIT 1;";
        var result = await cmd.ExecuteScalarAsync();
        return (Guid)result!;
    }

    /// <summary>
    /// Seeds a subject with a known access token hash and links it to the test tenant.
    /// Returns the subject ID.
    /// </summary>
    private async Task<Guid> SeedSubjectWithAccessTokenAsync()
    {
        var subjectId = Guid.CreateVersion7();
        var tokenHash = HashUtils.Sha256Hex(TestAccessToken);

        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        // Delete any prior test subject with this token hash to avoid conflicts
        await using (var delCmd = conn.CreateCommand())
        {
            delCmd.CommandText = "DELETE FROM subjects WHERE access_token_hash = @hash;";
            delCmd.Parameters.AddWithValue("hash", tokenHash);
            await delCmd.ExecuteNonQueryAsync();
        }

        // Insert subject
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO subjects (id, name, access_token_hash, access_token_prefix, is_active, is_system_subject, created_at, updated_at, approval_status)
                VALUES (@id, @name, @hash, @prefix, true, false, now(), now(), 'Approved');
                """;
            cmd.Parameters.AddWithValue("id", subjectId);
            cmd.Parameters.AddWithValue("name", "OIDC Test Subject");
            cmd.Parameters.AddWithValue("hash", tokenHash);
            cmd.Parameters.AddWithValue("prefix", "oidctest-a1b2...");
            await cmd.ExecuteNonQueryAsync();
        }

        // Link subject to tenant
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO tenant_members (id, tenant_id, subject_id, sys_created_at, sys_updated_at, limit_to_24_hours)
                VALUES (@id, @tenantId, @subjectId, now(), now(), false);
                """;
            cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
            cmd.Parameters.AddWithValue("tenantId", _tenantId);
            cmd.Parameters.AddWithValue("subjectId", subjectId);
            await cmd.ExecuteNonQueryAsync();
        }

        // Grant admin role to the subject on the tenant
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO subject_roles (id, subject_id, role_id, sys_created_at, sys_updated_at)
                SELECT @id, @subjectId, r.id, now(), now()
                FROM roles r WHERE r.name = 'admin'
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
            cmd.Parameters.AddWithValue("subjectId", subjectId);
            await cmd.ExecuteNonQueryAsync();
        }

        Log($"Seeded test subject {subjectId}");
        return subjectId;
    }

    /// <summary>
    /// Seeds an OIDC provider row. Returns the provider ID.
    /// </summary>
    private async Task<Guid> SeedOidcProviderAsync(string name, string issuerUrl)
    {
        var providerId = Guid.CreateVersion7();

        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO oidc_providers (id, name, issuer_url, client_id, is_enabled, display_order, scopes, claim_mappings, default_roles, created_at, updated_at)
            VALUES (@id, @name, @issuerUrl, @clientId, true, 0, '["openid","profile","email"]'::jsonb, '{}'::jsonb, '["readable"]'::jsonb, now(), now());
            """;
        cmd.Parameters.AddWithValue("id", providerId);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("issuerUrl", issuerUrl);
        cmd.Parameters.AddWithValue("clientId", $"test-client-{providerId:N}");
        await cmd.ExecuteNonQueryAsync();

        Log($"Seeded OIDC provider {providerId} ({name})");
        return providerId;
    }

    /// <summary>
    /// Seeds a subject_oidc_identities row. Returns the identity ID.
    /// </summary>
    private async Task<Guid> SeedOidcIdentityAsync(
        Guid subjectId, Guid providerId, string oidcSubjectId, string issuer, string email)
    {
        var identityId = Guid.CreateVersion7();

        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO subject_oidc_identities (id, subject_id, provider_id, oidc_subject_id, issuer, email, linked_at)
            VALUES (@id, @subjectId, @providerId, @oidcSubjectId, @issuer, @email, now());
            """;
        cmd.Parameters.AddWithValue("id", identityId);
        cmd.Parameters.AddWithValue("subjectId", subjectId);
        cmd.Parameters.AddWithValue("providerId", providerId);
        cmd.Parameters.AddWithValue("oidcSubjectId", oidcSubjectId);
        cmd.Parameters.AddWithValue("issuer", issuer);
        cmd.Parameters.AddWithValue("email", email);
        await cmd.ExecuteNonQueryAsync();

        Log($"Seeded OIDC identity {identityId} for subject {subjectId}");
        return identityId;
    }

    /// <summary>
    /// Seeds a passkey credential for the given subject.
    /// Provides a second auth factor so the OIDC identity is not the last factor.
    /// </summary>
    private async Task SeedPasskeyAsync(Guid subjectId)
    {
        var passkeyId = Guid.CreateVersion7();

        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO passkey_credentials (id, subject_id, credential_id, public_key, sign_count, created_at)
            VALUES (@id, @subjectId, @credentialId, @publicKey, 0, now());
            """;
        cmd.Parameters.AddWithValue("id", passkeyId);
        cmd.Parameters.AddWithValue("subjectId", subjectId);
        cmd.Parameters.AddWithValue("credentialId", Encoding.UTF8.GetBytes("test-credential-id"));
        cmd.Parameters.AddWithValue("publicKey", Encoding.UTF8.GetBytes("test-public-key"));
        await cmd.ExecuteNonQueryAsync();

        Log($"Seeded passkey {passkeyId} for subject {subjectId}");
    }

    #endregion
}
