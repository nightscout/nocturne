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
/// Integration tests for the guest link lifecycle: creation, activation,
/// session-based access, revocation, and listing.
/// </summary>
[Trait("Category", "Integration")]
public class GuestLinkLifecycleIntegrationTests : AspireIntegrationTestBase
{
    private Guid _tenantId;
    private Guid _subjectId;
    private string _accessToken = null!;

    public GuestLinkLifecycleIntegrationTests(
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

        // Seed a subject for the guest link tests
        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        _tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        (_subjectId, _accessToken) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, _tenantId, "Guest Link Test User");

        Log($"Seeded tenant {_tenantId}, subject {_subjectId}");
    }

    #region Create Guest Link

    [Fact]
    public async Task CreateGuestLink_Authenticated_ReturnsCodeAndUrl()
    {
        // Arrange
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);

        // Act
        var response = await client.PostAsJsonAsync("/api/v4/guest-links", new
        {
            label = "Test Link",
            scopes = new[] { "entries.read" }
        });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(content);

        var code = body.GetProperty("code").GetString();
        code.Should().NotBeNullOrWhiteSpace();
        code.Should().MatchRegex(@"^[A-Z0-9]{3}-[A-Z0-9]{4}$", "code should be formatted as ABC-DEFG");

        var url = body.GetProperty("url").GetString();
        url.Should().NotBeNullOrWhiteSpace();
        url.Should().Contain("/guest/");

        Log($"Created guest link with code: {code}, url: {url}");
    }

    [Fact]
    public async Task CreateGuestLink_Unauthenticated_Returns401()
    {
        // Act - use raw ApiClient (no auth)
        var response = await ApiClient.PostAsJsonAsync("/api/v4/guest-links", new
        {
            label = "Unauthenticated Link",
            scopes = new[] { "entries.read" }
        });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateGuestLink_MaxLinksExceeded_RejectsCreation()
    {
        // Arrange
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);

        // Create 5 links
        for (var i = 0; i < 5; i++)
        {
            var createResponse = await client.PostAsJsonAsync("/api/v4/guest-links", new
            {
                label = $"Link {i + 1}",
                scopes = new[] { "entries.read" }
            });
            createResponse.StatusCode.Should().Be(HttpStatusCode.OK, $"link {i + 1} creation should succeed");
        }

        // Act - 6th link should be rejected
        var response = await client.PostAsJsonAsync("/api/v4/guest-links", new
        {
            label = "Link 6 - Over Limit",
            scopes = new[] { "entries.read" }
        });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateGuestLink_WriteScope_Rejected()
    {
        // Arrange
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);

        // Act
        var response = await client.PostAsJsonAsync("/api/v4/guest-links", new
        {
            label = "Write Scope Link",
            scopes = new[] { "entries.readwrite" }
        });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    #endregion

    #region Activate Guest Link

    [Fact]
    public async Task ActivateGuestLink_ValidCode_SetsSessionCookie()
    {
        // Arrange - create a link via the API
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var createResponse = await client.PostAsJsonAsync("/api/v4/guest-links", new
        {
            label = "Activate Test",
            scopes = new[] { "entries.read" }
        });
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var createContent = await createResponse.Content.ReadAsStringAsync();
        var createBody = JsonSerializer.Deserialize<JsonElement>(createContent);
        var code = createBody.GetProperty("code").GetString()!;

        // Act - activate the link (anonymous)
        var activateResponse = await ApiClient.PostAsJsonAsync("/api/v4/guest-links/activate", new
        {
            code
        });

        // Assert
        activateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var activateContent = await activateResponse.Content.ReadAsStringAsync();
        var activateBody = JsonSerializer.Deserialize<JsonElement>(activateContent);
        activateBody.GetProperty("expiresAt").GetString().Should().NotBeNullOrWhiteSpace();

        activateResponse.Headers.TryGetValues("Set-Cookie", out var cookies).Should().BeTrue();
        var cookieValues = cookies!.ToList();
        cookieValues.Should().Contain(c => c.Contains("nocturne-guest-session"));

        Log($"Guest link activated, cookie set");
    }

    [Fact]
    public async Task ActivateGuestLink_ExpiredCode_Rejected()
    {
        // Arrange - seed an expired link via SQL
        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        var (_, rawCode) = await AuthTestHelpers.SeedGuestLinkAsync(
            conn, _subjectId, expiresAt: DateTime.UtcNow.AddHours(-1));

        var formattedCode = $"{rawCode[..3]}-{rawCode[3..]}";

        // Act
        var response = await ApiClient.PostAsJsonAsync("/api/v4/guest-links/activate", new
        {
            code = formattedCode
        });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ActivateGuestLink_RevokedCode_Rejected()
    {
        // Arrange - seed a revoked link via SQL
        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        var (_, rawCode) = await AuthTestHelpers.SeedGuestLinkAsync(
            conn, _subjectId, revokedAt: DateTime.UtcNow.AddMinutes(-30));

        var formattedCode = $"{rawCode[..3]}-{rawCode[3..]}";

        // Act
        var response = await ApiClient.PostAsJsonAsync("/api/v4/guest-links/activate", new
        {
            code = formattedCode
        });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ActivateGuestLink_AlreadyActivated_Rejected()
    {
        // Arrange - seed an already-activated link via SQL
        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        var (_, rawCode) = await AuthTestHelpers.SeedGuestLinkAsync(
            conn, _subjectId, activatedAt: DateTime.UtcNow.AddMinutes(-10));

        var formattedCode = $"{rawCode[..3]}-{rawCode[3..]}";

        // Act
        var response = await ApiClient.PostAsJsonAsync("/api/v4/guest-links/activate", new
        {
            code = formattedCode
        });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    #endregion

    #region Guest Session Access

    [Fact]
    public async Task GuestSession_CanReadEntries()
    {
        // Arrange - create and activate a guest link with cookie-enabled client
        var code = await CreateGuestLinkCodeAsync();

        var handler = new HttpClientHandler { UseCookies = true };
        using var cookieClient = new HttpClient(handler)
        {
            BaseAddress = ApiClient.BaseAddress
        };

        // Activate to receive the session cookie
        var activateResponse = await cookieClient.PostAsJsonAsync("/api/v4/guest-links/activate", new
        {
            code
        });
        activateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Act - read entries with the session cookie
        var response = await cookieClient.GetAsync("/api/v1/entries/current");

        // Assert - should not be 401 or 403
        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
        Log($"Guest session read entries, status: {response.StatusCode}");
    }

    [Fact]
    public async Task GuestSession_CannotWriteEntries()
    {
        // Arrange - create and activate a guest link with cookie-enabled client
        var code = await CreateGuestLinkCodeAsync();

        var handler = new HttpClientHandler { UseCookies = true };
        using var cookieClient = new HttpClient(handler)
        {
            BaseAddress = ApiClient.BaseAddress
        };

        var activateResponse = await cookieClient.PostAsJsonAsync("/api/v4/guest-links/activate", new
        {
            code
        });
        activateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Act - attempt to write entries
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

        var response = await cookieClient.PostAsJsonAsync("/api/v1/entries", entryPayload);

        // Assert - should be rejected
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        Log($"Guest session write entries rejected, status: {response.StatusCode}");
    }

    [Fact]
    public async Task GuestSession_CannotAccessAdminEndpoints()
    {
        // Arrange - create and activate a guest link with cookie-enabled client
        var code = await CreateGuestLinkCodeAsync();

        var handler = new HttpClientHandler { UseCookies = true };
        using var cookieClient = new HttpClient(handler)
        {
            BaseAddress = ApiClient.BaseAddress
        };

        var activateResponse = await cookieClient.PostAsJsonAsync("/api/v4/guest-links/activate", new
        {
            code
        });
        activateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Act - attempt to access admin endpoint
        var response = await cookieClient.GetAsync("/api/v2/authorization/subjects");

        // Assert - should be rejected
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        Log($"Guest session admin access rejected, status: {response.StatusCode}");
    }

    #endregion

    #region Revoke Guest Link

    [Fact]
    public async Task RevokeGuestLink_ByOwner_Succeeds()
    {
        // Arrange - create a link, then get its ID from the list
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);

        var createResponse = await client.PostAsJsonAsync("/api/v4/guest-links", new
        {
            label = "Revoke Test",
            scopes = new[] { "entries.read" }
        });
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Get the link ID from the list
        var listResponse = await client.GetAsync("/api/v4/guest-links");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var listContent = await listResponse.Content.ReadAsStringAsync();
        var links = JsonSerializer.Deserialize<JsonElement>(listContent);
        var grantId = links.EnumerateArray().First().GetProperty("id").GetGuid();

        // Act
        var deleteResponse = await client.DeleteAsync($"/api/v4/guest-links/{grantId}");

        // Assert
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task RevokeGuestLink_ByNonOwner_Rejected()
    {
        // Arrange - create a link with the first subject
        using var ownerClient = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);

        var createResponse = await ownerClient.PostAsJsonAsync("/api/v4/guest-links", new
        {
            label = "Non-Owner Revoke Test",
            scopes = new[] { "entries.read" }
        });
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Get the link ID
        var listResponse = await ownerClient.GetAsync("/api/v4/guest-links");
        var listContent = await listResponse.Content.ReadAsStringAsync();
        var links = JsonSerializer.Deserialize<JsonElement>(listContent);
        var grantId = links.EnumerateArray().First().GetProperty("id").GetGuid();

        // Seed a second subject
        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        var (_, otherAccessToken) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(
            conn, _tenantId, "Other User");

        using var otherClient = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, otherAccessToken);

        // Act - try to revoke with the other subject
        var deleteResponse = await otherClient.DeleteAsync($"/api/v4/guest-links/{grantId}");

        // Assert
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    #endregion

    #region List Guest Links

    [Fact]
    public async Task ListGuestLinks_ReturnsAllForOwner()
    {
        // Arrange - create 2 links
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);

        for (var i = 0; i < 2; i++)
        {
            var createResponse = await client.PostAsJsonAsync("/api/v4/guest-links", new
            {
                label = $"List Test Link {i + 1}",
                scopes = new[] { "entries.read" }
            });
            createResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // Act
        var listResponse = await client.GetAsync("/api/v4/guest-links");

        // Assert
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await listResponse.Content.ReadAsStringAsync();
        var links = JsonSerializer.Deserialize<JsonElement>(content);
        links.GetArrayLength().Should().BeGreaterThanOrEqualTo(2);

        Log($"Listed {links.GetArrayLength()} guest links");
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Creates a guest link via the API and returns the formatted code.
    /// </summary>
    private async Task<string> CreateGuestLinkCodeAsync()
    {
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);

        var response = await client.PostAsJsonAsync("/api/v4/guest-links", new
        {
            label = "Session Test",
            scopes = new[] { "entries.read" }
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(content);
        return body.GetProperty("code").GetString()!;
    }

    #endregion
}
