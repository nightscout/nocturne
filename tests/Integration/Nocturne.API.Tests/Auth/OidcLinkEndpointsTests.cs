using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// Integration tests for the OIDC account linking endpoints (list + unlink).
/// Tests the GET /api/auth/oidc/link/identities and DELETE /api/auth/oidc/link/identities/{id}
/// endpoints against a real API with real PostgreSQL.
///
/// Does NOT test link initiation or callback (those require a real IdP; unit tests cover them).
/// </summary>
[Collection(ApiIntegrationShards.Two)]
[Trait("Category", "Integration")]
public class OidcLinkEndpointsTests : ApiIntegrationTestBase
{
    /// <summary>
    /// Known access token for test subject authentication.
    /// Format: {name}-{hex_digest} (Nightscout legacy access token format).
    /// </summary>
    private const string TestAccessToken = "oidctest-a1b2c3d4e5f6g7h8";
    private Guid _seededSubjectId;

    private Guid _tenantId;

    public OidcLinkEndpointsTests(
        ApiIntegrationTestFixture fixture,
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
        var response = await ApiClient.GetAsync("/api/auth/oidc/link/identities");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetLinkedIdentities_Authenticated_ReturnsEmptyList()
    {
        // Arrange - create a subject with access token (no OIDC identities). The passkey keeps
        // the tenant out of recovery mode, which a member with no sign-in factor puts it in.
        var subjectId = await SeedSubjectWithAccessTokenAsync();
        await SeedPasskeyAsync(subjectId);

        using var client = await CreateSessionClientAsync();

        // Act
        var response = await client.GetAsync("/api/auth/oidc/link/identities");

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

        using var client = await CreateSessionClientAsync();

        // Act
        var response = await client.GetAsync("/api/auth/oidc/link/identities");

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
            $"/api/auth/oidc/link/identities/{Guid.CreateVersion7()}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UnlinkIdentity_NotFound_Returns404()
    {
        // Arrange
        await SeedPasskeyAsync(await SeedSubjectWithAccessTokenAsync());
        using var client = await CreateSessionClientAsync();

        // Act - delete a nonexistent identity
        var response = await client.DeleteAsync(
            $"/api/auth/oidc/link/identities/{Guid.CreateVersion7()}");

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

        using var client = await CreateSessionClientAsync();

        // Act
        var response = await client.DeleteAsync(
            $"/api/auth/oidc/link/identities/{identityId}");

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

        using var client = await CreateSessionClientAsync();

        // Act
        var response = await client.DeleteAsync(
            $"/api/auth/oidc/link/identities/{identityId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Creates an HttpClient that authenticates via the test access token.
    /// This resolves to a real SubjectId, which the OIDC link endpoints require.
    /// </summary>
    /// <summary>
    /// A client signed in as the subject <see cref="SeedSubjectWithAccessTokenAsync"/> last seeded:
    /// linking and unlinking a sign-in method takes the person's own session.
    /// </summary>
    private async Task<HttpClient> CreateSessionClientAsync() =>
        AuthTestHelpers.CreateBearerClient(
            Fixture, await AuthTestHelpers.IssueSessionTokenAsync(Fixture, _seededSubjectId));

    /// <summary>
    /// Resolves the test tenant ID from the database.
    /// </summary>
    private async Task<Guid> GetTenantIdAsync()
    {
        await using var conn = await OpenPostgresAsync();

        return await AuthTestHelpers.GetTenantIdAsync(conn);
    }

    /// <summary>
    /// Seeds a subject, links it to the test tenant and issues it a direct grant carrying the
    /// known test token. Returns the subject ID.
    /// </summary>
    private async Task<Guid> SeedSubjectWithAccessTokenAsync()
    {
        var subjectId = _seededSubjectId = Guid.CreateVersion7();
        var tokenHash = HashUtils.Sha256Hex(TestAccessToken);

        await using var conn = await OpenPostgresAsync();

        // Drop any grant left by a prior run, so the token resolves to exactly one subject.
        await conn.ExecuteAsync("DELETE FROM oauth_grants WHERE token_hash = @hash;", ("hash", tokenHash));

        // Insert subject
        await conn.ExecuteAsync("""
            INSERT INTO subjects (id, name, is_active, is_system_subject, created_at, updated_at, approval_status)
            VALUES (@id, @name, true, false, now(), now(), 'Approved');
            """,
            ("id", subjectId), ("name", "OIDC Test Subject"));

        // Link subject to tenant. The membership is the ceiling MemberScopeMiddleware intersects
        // the grant's scopes against, so a grant on a member holding nothing resolves to nothing.
        await conn.ExecuteAsync("""
            INSERT INTO tenant_members (id, tenant_id, subject_id, direct_permissions, sys_created_at, sys_updated_at, limit_to_24_hours)
            VALUES (@id, @tenantId, @subjectId, '["*"]'::jsonb, now(), now(), false);
            """,
            ("id", Guid.CreateVersion7()), ("tenantId", _tenantId), ("subjectId", subjectId));

        // The credential itself. Device and service tokens are grants on oauth_grants; subjects
        // carry no token of their own.
        await using (var transaction = await conn.BeginTransactionAsync())
        {
            await conn.PinTenantAsync(_tenantId, local: true);
            await conn.ExecuteAsync("""
                INSERT INTO oauth_grants (id, tenant_id, subject_id, grant_type, scopes, label, token_hash, created_at)
                VALUES (@id, @tenantId, @subjectId, 'direct', ARRAY['*'], 'OIDC test token', @hash, now());
                """,
                ("id", Guid.CreateVersion7()), ("tenantId", _tenantId),
                ("subjectId", subjectId), ("hash", tokenHash));
            await transaction.CommitAsync();
        }

        // Grant admin role to the subject on the tenant
        await conn.ExecuteAsync("""
            INSERT INTO subject_roles (subject_id, role_id)
            SELECT @subjectId, r.id
            FROM roles r WHERE r.name = 'admin'
            LIMIT 1;
            """,
            ("subjectId", subjectId));

        Log($"Seeded test subject {subjectId}");
        return subjectId;
    }

    /// <summary>
    /// Seeds an OIDC provider row. Returns the provider ID.
    /// </summary>
    private async Task<Guid> SeedOidcProviderAsync(string name, string issuerUrl)
    {
        var providerId = Guid.CreateVersion7();

        await using var conn = await OpenPostgresAsync();

        await conn.ExecuteAsync("""
            INSERT INTO oidc_providers (id, name, issuer_url, client_id, is_enabled, display_order, scopes, claim_mappings, default_roles, created_at, updated_at)
            VALUES (@id, @name, @issuerUrl, @clientId, true, 0, '["openid","profile","email"]'::jsonb, '{}'::jsonb, '["readable"]'::jsonb, now(), now());
            """, ("id", providerId), ("name", name), ("issuerUrl", issuerUrl), ("clientId", $"test-client-{providerId:N}"));

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

        await using var conn = await OpenPostgresAsync();

        await conn.ExecuteAsync("""
            INSERT INTO subject_oidc_identities (id, subject_id, provider_id, oidc_subject_id, issuer, email, linked_at)
            VALUES (@id, @subjectId, @providerId, @oidcSubjectId, @issuer, @email, now());
            """, ("id", identityId), ("subjectId", subjectId), ("providerId", providerId), ("oidcSubjectId", oidcSubjectId), ("issuer", issuer), ("email", email));

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

        await using var conn = await OpenPostgresAsync();

        await conn.ExecuteAsync("""
            INSERT INTO passkey_credentials (id, subject_id, credential_id, public_key, sign_count, transports, created_at)
            VALUES (@id, @subjectId, @credentialId, @publicKey, 0, '{}', now());
            """, ("id", passkeyId), ("subjectId", subjectId), ("credentialId", Encoding.UTF8.GetBytes($"test-credential-{passkeyId:N}")), ("publicKey", Encoding.UTF8.GetBytes("test-public-key")));

        Log($"Seeded passkey {passkeyId} for subject {subjectId}");
    }

    #endregion
}
