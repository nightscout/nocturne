using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Models;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// Integration tests for public (unauthenticated) access controlled via
/// the Public system subject's role assignments and 24-hour data limit.
/// </summary>
[Trait("Category", "Integration")]
public class PublicAccessIntegrationTests : AspireIntegrationTestBase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

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
    public async Task PublicDisabled_NoAuth_Returns401()
    {
        // Arrange — default state: Public subject has no roles assigned
        var client = ApiClient;

        // Act
        var response = await client.GetAsync("/api/v1/entries/current");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PublicEnabled_NoAuth_CanReadEntries()
    {
        // Arrange — enable public access by assigning the readable role
        await EnablePublicAccessAsync();

        // Act
        var response = await ApiClient.GetAsync("/api/v1/entries/current");

        // Assert
        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
        Log($"Public read entries status: {response.StatusCode}");
    }

    [Fact]
    public async Task PublicEnabled_NoAuth_CannotWriteEntries()
    {
        // Arrange — enable public access (read-only)
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

        // Act
        var response = await ApiClient.PostAsJsonAsync("/api/v1/entries", entry);

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PublicEnabled_NoAuth_CannotAccessAdmin()
    {
        // Arrange — enable public access (read-only)
        await EnablePublicAccessAsync();

        // Act
        var response = await ApiClient.GetAsync("/api/v2/authorization/subjects");

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PublicEnabled_LimitTo24Hours_RestrictsData()
    {
        // Arrange — seed an entry 48 hours ago via the authenticated client
        using var authClient = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var oldMills = now - (48L * 60 * 60 * 1000); // 48 hours ago
        var recentMills = now - (1L * 60 * 60 * 1000); // 1 hour ago

        var entries = new[]
        {
            CreateTestEntry(sgv: 100, mills: oldMills),
            CreateTestEntry(sgv: 200, mills: recentMills),
        };

        var postResponse = await authClient.PostAsJsonAsync("/api/v1/entries", entries);
        postResponse.EnsureSuccessStatusCode();

        // Enable public access with 24-hour limit
        var (publicMemberId, readableRoleId) = await GetPublicMemberAndReadableRoleAsync();

        using var adminClient = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var rolesResponse = await adminClient.PutAsJsonAsync(
            $"/api/v4/member-invites/members/{publicMemberId}/roles",
            new { roleIds = new[] { readableRoleId } });
        rolesResponse.EnsureSuccessStatusCode();

        var limitResponse = await adminClient.PutAsJsonAsync(
            $"/api/v4/member-invites/members/{publicMemberId}/limit-to-24-hours",
            new { limitTo24Hours = true });
        limitResponse.EnsureSuccessStatusCode();

        await Task.Delay(200); // Allow cache eviction to propagate

        // Act — fetch entries without auth
        var response = await ApiClient.GetAsync("/api/v1/entries?count=100");
        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);

        var fetched = await response.Content.ReadFromJsonAsync<Entry[]>(JsonOptions);

        // Assert — all returned entries should be within the last 25 hours
        var cutoff = DateTimeOffset.UtcNow.AddHours(-25).ToUnixTimeMilliseconds();
        fetched.Should().NotBeNull();
        fetched!.Should().AllSatisfy(e =>
            e.Mills.Should().BeGreaterThan(cutoff, "public 24-hour limit should exclude old entries"));

        Log($"Fetched {fetched.Length} entries, all within 25h window");
    }

    [Fact]
    public async Task PublicDisabledAfterEnable_Returns401()
    {
        // Arrange — enable public access
        var (publicMemberId, readableRoleId) = await GetPublicMemberAndReadableRoleAsync();

        using var adminClient = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var enableResponse = await adminClient.PutAsJsonAsync(
            $"/api/v4/member-invites/members/{publicMemberId}/roles",
            new { roleIds = new[] { readableRoleId } });
        enableResponse.EnsureSuccessStatusCode();
        await Task.Delay(200);

        // Verify access works while enabled
        var accessResponse = await ApiClient.GetAsync("/api/v1/entries/current");
        accessResponse.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
            "public access should work after enabling");

        // Act — disable public access by removing all roles
        var disableResponse = await adminClient.PutAsJsonAsync(
            $"/api/v4/member-invites/members/{publicMemberId}/roles",
            new { roleIds = Array.Empty<Guid>() });
        disableResponse.EnsureSuccessStatusCode();
        await Task.Delay(200);

        // Assert
        var finalResponse = await ApiClient.GetAsync("/api/v1/entries/current");
        finalResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
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

    private static Entry CreateTestEntry(double sgv = 120, string direction = "Flat", long? mills = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new Entry
        {
            Sgv = sgv,
            Type = "sgv",
            Mills = mills ?? now.ToUnixTimeMilliseconds(),
            DateString = (mills.HasValue
                ? DateTimeOffset.FromUnixTimeMilliseconds(mills.Value)
                : now
            ).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            Direction = direction,
            Device = "test-device",
        };
    }

    #endregion
}
