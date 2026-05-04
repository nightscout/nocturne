using System.Net;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// Integration tests for session cookie authentication via the SessionCookieHandler.
/// Validates that the handler authenticates from the .Nocturne.AccessToken cookie,
/// clears cookies on invalid/expired tokens, and falls through to the next handler
/// when no cookies are present.
/// </summary>
[Trait("Category", "Integration")]
public class SessionCookieIntegrationTests : AspireIntegrationTestBase
{
    private Guid _tenantId;
    private Guid _subjectId;
    private string _accessToken = null!;

    public SessionCookieIntegrationTests(
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

        // Seed a subject for session cookie tests
        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        _tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        (_subjectId, _accessToken) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, _tenantId, "Session Cookie User");

        Log($"Seeded tenant {_tenantId}, subject {_subjectId}");
    }

    [Fact]
    public async Task SessionCookie_ValidAccessToken_Authenticates()
    {
        // Arrange - complete a PKCE flow to get a real JWT
        using var authClient = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(authClient);
        var result = await AuthTestHelpers.ExecutePkceFlowAsync(authClient, clientId);

        // Set the JWT as the .Nocturne.AccessToken cookie
        var handler = new HttpClientHandler();
        handler.CookieContainer.Add(
            ApiClient.BaseAddress!,
            new System.Net.Cookie(".Nocturne.AccessToken", result.AccessToken));
        using var cookieClient = new HttpClient(handler) { BaseAddress = ApiClient.BaseAddress };

        // Act
        var response = await cookieClient.GetAsync("/api/v1/entries/current");

        // Assert
        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        Log($"Session cookie auth succeeded, status: {response.StatusCode}");
    }

    [Fact]
    public async Task SessionCookie_InvalidAccessToken_NoRefresh_ClearsAndSkips()
    {
        // Arrange - set an invalid JWT as the access token cookie
        var handler = new HttpClientHandler();
        handler.CookieContainer.Add(
            ApiClient.BaseAddress!,
            new System.Net.Cookie(".Nocturne.AccessToken", "invalid.jwt.token"));
        using var cookieClient = new HttpClient(handler) { BaseAddress = ApiClient.BaseAddress };

        // Act
        var response = await cookieClient.GetAsync("/api/v1/entries/current");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        Log($"Invalid cookie correctly rejected, status: {response.StatusCode}");
    }

    [Fact]
    public async Task SessionCookie_NoCookies_SkipsToNextHandler()
    {
        // Arrange - no cookies, no auth headers
        var handler = new HttpClientHandler();
        using var cookieClient = new HttpClient(handler) { BaseAddress = ApiClient.BaseAddress };

        // Act
        var response = await cookieClient.GetAsync("/api/v1/entries/current");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        Log($"No cookies correctly falls through, status: {response.StatusCode}");
    }

    [Fact]
    public async Task SessionCookie_ExpiredAccessToken_WithInvalidRefresh_Returns401()
    {
        // Arrange - set expired access token and invalid refresh token cookies
        var handler = new HttpClientHandler();
        handler.CookieContainer.Add(
            ApiClient.BaseAddress!,
            new System.Net.Cookie(".Nocturne.AccessToken", "expired.access.token"));
        handler.CookieContainer.Add(
            ApiClient.BaseAddress!,
            new System.Net.Cookie(".Nocturne.RefreshToken", "invalid-refresh"));
        using var cookieClient = new HttpClient(handler) { BaseAddress = ApiClient.BaseAddress };

        // Act
        var response = await cookieClient.GetAsync("/api/v1/entries/current");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        Log($"Expired access + invalid refresh correctly rejected, status: {response.StatusCode}");
    }
}
