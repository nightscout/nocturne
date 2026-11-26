using System.Net;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Models.Authorization;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// Integration tests for TenantSetupMiddleware which returns 503 when the tenant
/// has no passkey credentials (setup required) or is in recovery mode
/// (<see cref="Nocturne.Core.Models.Identity.SetupGate.RecoveryMode"/>).
/// </summary>
[Collection(ApiIntegrationShards.Two)]
[Trait("Category", "Integration")]
public class TenantSetupGuardIntegrationTests : ApiIntegrationTestBase
{
    private Guid _tenantId;

    public TenantSetupGuardIntegrationTests(
        ApiIntegrationTestFixture fixture,
        ITestOutputHelper output)
        : base(fixture, output) { }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        // Provision the tenant via an authenticated status call
        using var client = CreateAuthenticatedClient();
        var response = await client.GetAsync("/api/v1/status");
        response.StatusCode.Should().Be(HttpStatusCode.OK, "tenant provisioning request should succeed");

        await using var conn = await OpenPostgresAsync();

        _tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        Log($"Provisioned tenant {_tenantId}");
    }

    [Fact]
    public async Task FreshTenant_NoPasskeys_Returns503SetupRequired()
    {
        // Arrange - delete all passkey credentials for tenant members
        await using var conn = await OpenPostgresAsync();

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
        await using var conn = await OpenPostgresAsync();

        // Every member holds a passkey: the seeded owner does, and so does this one.
        var (_, accessToken) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, _tenantId, "Setup Guard User");

        // Act
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, accessToken);
        var response = await client.GetAsync("/api/v1/entries/current");

        // Assert
        response.StatusCode.Should().NotBe(HttpStatusCode.ServiceUnavailable);
        Log($"API accessible with passkey, status: {response.StatusCode}");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task AStuckMemberStopsTheSiteOnlyWhenNoMemberAdministratorCanSignIn(
        bool signedInMemberAdministers, bool recoveryMode)
    {
        await using var conn = await OpenPostgresAsync();

        await DeleteAllPasskeysForTenantAsync(conn, _tenantId);

        var (_, accessToken) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(
            conn, _tenantId, "Passkey User", signedInMemberAdministers ? null : [Scope.GlucoseRead]);
        await AuthTestHelpers.SeedSubjectWithoutPasskeyAsync(
            conn, _tenantId, "Stuck User", signedInMemberAdministers ? [Scope.GlucoseRead] : null);

        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, accessToken);
        var response = await client.GetAsync("/api/v1/entries/current");

        (response.StatusCode == HttpStatusCode.ServiceUnavailable).Should().Be(recoveryMode);
        if (recoveryMode)
        {
            var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
            body.GetProperty("error").GetString().Should().Be("recovery_mode_active");
            body.GetProperty("setupRequired").GetBoolean().Should().BeFalse();
            body.GetProperty("recoveryMode").GetBoolean().Should().BeTrue();
        }
    }

    [Fact]
    public async Task SetupEndpoints_AllowedDuringSetup()
    {
        // Arrange - delete all passkeys so setup guard is active
        await using var conn = await OpenPostgresAsync();

        await DeleteAllPasskeysForTenantAsync(conn, _tenantId);

        using var client = CreateAuthenticatedClient();

        // Verify regular API returns 503
        var regularResponse = await client.GetAsync("/api/v1/entries/current");
        regularResponse.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        // Act - passkey registration endpoint should bypass the setup guard
        var setupResponse = await client.PostAsync(
            "/api/auth/passkey/register/options",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        // Assert - should NOT be 503 (may be 400/401, but not 503)
        setupResponse.StatusCode.Should().NotBe(HttpStatusCode.ServiceUnavailable);
        Log($"Setup endpoint bypassed guard, status: {setupResponse.StatusCode}");
    }

    private static async Task DeleteAllPasskeysForTenantAsync(NpgsqlConnection conn, Guid tenantId)
    {
        await conn.ExecuteAsync("""
            DELETE FROM passkey_credentials
            WHERE subject_id IN (
                SELECT subject_id FROM tenant_members WHERE tenant_id = @tenantId
            );
            """, ("tenantId", tenantId));
    }
}
