using System.Net;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// Integration tests for TenantSetupMiddleware which returns 503 when the tenant
/// has no passkey credentials (setup required) or has orphaned subjects without
/// passkeys or OIDC identities (recovery mode).
/// </summary>
[Trait("Category", "Integration")]
public class TenantSetupGuardIntegrationTests : AspireIntegrationTestBase
{
    private Guid _tenantId;

    public TenantSetupGuardIntegrationTests(
        AspireIntegrationTestFixture fixture,
        ITestOutputHelper output)
        : base(fixture, output) { }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        // Provision the tenant via an authenticated status call
        using var client = CreateAuthenticatedClient();
        var response = await client.GetAsync("/api/v1/status");
        response.StatusCode.Should().Be(HttpStatusCode.OK, "tenant provisioning request should succeed");

        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        _tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        Log($"Provisioned tenant {_tenantId}");
    }

    [Fact]
    public async Task FreshTenant_NoPasskeys_Returns503SetupRequired()
    {
        // Arrange - delete all passkey credentials for tenant members
        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        await DeleteAllPasskeysForTenantAsync(conn, _tenantId);

        // Act
        using var client = CreateAuthenticatedClient();
        var response = await client.GetAsync("/api/v1/entries/current");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var content = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(content);
        body.GetProperty("error").GetString().Should().Be("setup_required");
        body.GetProperty("setupRequired").GetBoolean().Should().BeTrue();
        body.GetProperty("recoveryMode").GetBoolean().Should().BeFalse();
        Log("503 setup_required returned as expected");
    }

    [Fact]
    public async Task TenantWithPasskey_ApiAccessible()
    {
        // Arrange - seed a subject with passkey
        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        // Ensure clean state: remove existing passkeys, then seed one with a passkey
        await DeleteAllPasskeysForTenantAsync(conn, _tenantId);
        var (_, accessToken) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, _tenantId, "Setup Guard User");

        // Act
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, accessToken);
        var response = await client.GetAsync("/api/v1/entries/current");

        // Assert
        response.StatusCode.Should().NotBe(HttpStatusCode.ServiceUnavailable);
        Log($"API accessible with passkey, status: {response.StatusCode}");
    }

    [Fact]
    public async Task OrphanedSubject_Returns503RecoveryMode()
    {
        // Arrange - seed one subject WITH passkey, another WITHOUT passkey (and no OIDC)
        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        await DeleteAllPasskeysForTenantAsync(conn, _tenantId);

        var (_, accessToken) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, _tenantId, "Passkey User");
        await AuthTestHelpers.SeedSubjectWithoutPasskeyAsync(conn, _tenantId, "Orphaned User");

        // Act
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, accessToken);
        var response = await client.GetAsync("/api/v1/entries/current");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var content = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(content);
        body.GetProperty("error").GetString().Should().Be("recovery_mode_active");
        body.GetProperty("setupRequired").GetBoolean().Should().BeFalse();
        body.GetProperty("recoveryMode").GetBoolean().Should().BeTrue();
        Log("503 recovery_mode_active returned as expected");
    }

    [Fact]
    public async Task SetupEndpoints_AllowedDuringSetup()
    {
        // Arrange - delete all passkeys so setup guard is active
        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        await DeleteAllPasskeysForTenantAsync(conn, _tenantId);

        using var client = CreateAuthenticatedClient();

        // Verify regular API returns 503
        var regularResponse = await client.GetAsync("/api/v1/entries/current");
        regularResponse.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        // Act - passkey registration endpoint should bypass the setup guard
        var setupResponse = await client.PostAsync(
            "/api/oauth/passkey/register/begin",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        // Assert - should NOT be 503 (may be 400/401, but not 503)
        setupResponse.StatusCode.Should().NotBe(HttpStatusCode.ServiceUnavailable);
        Log($"Setup endpoint bypassed guard, status: {setupResponse.StatusCode}");
    }

    private static async Task DeleteAllPasskeysForTenantAsync(NpgsqlConnection conn, Guid tenantId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM passkey_credentials
            WHERE subject_id IN (
                SELECT subject_id FROM tenant_members WHERE tenant_id = @tenantId
            );
            """;
        cmd.Parameters.AddWithValue("tenantId", tenantId);
        await cmd.ExecuteNonQueryAsync();
    }
}
