using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// Integration tests for the bare-host access contract.
///
/// As of the public-share-link feature, the bare tenant host ({slug}.{baseDomain}) is
/// <b>login-only</b>: assigning the Public system subject a read role no longer opens the bare
/// host to anonymous callers. Anonymous read is served exclusively via the rotatable share host
/// ({token}.share.{baseDomain}); its positive paths (read works, 24-hour limit, read-only,
/// unknown-token 404, rotation) are covered by the share-host integration suite added with the
/// management API/web phases, plus the AuthenticationMiddleware/TenantResolution unit tests.
/// </summary>
[Trait("Category", "Integration")]
public class PublicAccessIntegrationTests : AspireIntegrationTestBase
{
    private Guid _tenantId;
    private string _accessToken = null!;

    public PublicAccessIntegrationTests(
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

        // Seed an admin subject for managing public access
        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        _tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        (_, _accessToken) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, _tenantId, "Public Access Admin");

        Log($"Seeded tenant {_tenantId}");
    }

    [Fact]
    public async Task NoPublicRole_NoAuth_Returns401()
    {
        // Default state: Public subject has no roles assigned.
        var response = await ApiClient.GetAsync("/api/v1/entries/current");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PublicRoleAssigned_BareHost_StillReturns401()
    {
        // New contract: even with the Public subject holding a read role, the bare host is
        // login-only. Anonymous read is only served via the {token}.share host.
        await EnablePublicAccessAsync();

        var response = await ApiClient.GetAsync("/api/v1/entries/current");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the bare host no longer serves public read — that moved to the share host");
    }

    [Fact]
    public async Task AnonymousWrite_BareHost_IsDenied()
    {
        await EnablePublicAccessAsync();

        var entry = new[]
        {
            new
            {
                type = "sgv",
                sgv = 120,
                direction = "Flat",
                date = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                dateString = DateTime.UtcNow.ToString("o"),
            }
        };

        var response = await ApiClient.PostAsJsonAsync("/api/v1/entries", entry);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AnonymousAdmin_BareHost_IsDenied()
    {
        await EnablePublicAccessAsync();

        var response = await ApiClient.GetAsync("/api/v2/authorization/subjects");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    #region Helpers

    private async Task EnablePublicAccessAsync()
    {
        var (publicMemberId, readableRoleId) = await GetPublicMemberAndReadableRoleAsync();

        using var adminClient = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var response = await adminClient.PutAsJsonAsync(
            $"/api/v4/member-invites/members/{publicMemberId}/roles",
            new { roleIds = new[] { readableRoleId } });
        response.EnsureSuccessStatusCode();

        await Task.Delay(200); // Allow cache eviction to propagate
    }

    private async Task<(Guid PublicMemberId, Guid ReadableRoleId)> GetPublicMemberAndReadableRoleAsync()
    {
        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        var publicMemberId = await AuthTestHelpers.GetPublicMemberIdAsync(conn, _tenantId);
        var roles = await AuthTestHelpers.GetRoleIdsByNameAsync(conn, "readable");
        var readableRoleId = roles["readable"];

        return (publicMemberId, readableRoleId);
    }

    #endregion
}
