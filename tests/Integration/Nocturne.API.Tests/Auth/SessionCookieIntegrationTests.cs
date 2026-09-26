using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Auth;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// Integration tests for session cookie authentication via the SessionCookieHandler.
/// Validates that the handler authenticates from the .Nocturne.AccessToken cookie,
/// rejects invalid/expired tokens (without clearing the domain-wide session cookies),
/// and falls through to the next handler when no cookies are present.
/// </summary>
[Trait("Category", "Integration")]
public class SessionCookieIntegrationTests : ApiIntegrationTestBase
{
    private Guid _tenantId;
    private Guid _subjectId;
    private string _accessToken = null!;

    public SessionCookieIntegrationTests(
        ApiIntegrationTestFixture fixture,
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
        // Arrange - a session for the seeded subject, as a sign-in issues one. A grant's access
        // token (from an OAuth flow, say) is refused in this cookie by design.
        SessionTokenPair session;
        using (var scope = Fixture.Services.CreateScope())
        {
            session = await scope.ServiceProvider.GetRequiredService<ISessionService>()
                .IssueSessionAsync(_subjectId, new SessionContext(DeviceDescription: "integration-test"));
        }

        var handler = new HttpClientHandler();
        handler.CookieContainer.Add(
            Fixture.CookieOrigin,
            new System.Net.Cookie(".Nocturne.AccessToken", session.AccessToken));
        using var cookieClient = Fixture.CreateHttpClient(handler);

        // Act
        var response = await cookieClient.GetAsync("/api/v1/entries/current");

        // Assert
        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        Log($"Session cookie auth succeeded, status: {response.StatusCode}");
    }

    [Fact]
    public async Task SessionCookie_InvalidAccessToken_NoRefresh_Rejects()
    {
        // Arrange - set an invalid JWT as the access token cookie
        var handler = new HttpClientHandler();
        handler.CookieContainer.Add(
            Fixture.CookieOrigin,
            new System.Net.Cookie(".Nocturne.AccessToken", "invalid.jwt.token"));
        using var cookieClient = Fixture.CreateHttpClient(handler);

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
        using var cookieClient = Fixture.CreateHttpClient(handler);

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
            Fixture.CookieOrigin,
            new System.Net.Cookie(".Nocturne.AccessToken", "expired.access.token"));
        handler.CookieContainer.Add(
            Fixture.CookieOrigin,
            new System.Net.Cookie(".Nocturne.RefreshToken", "invalid-refresh"));
        using var cookieClient = Fixture.CreateHttpClient(handler);

        // Act
        var response = await cookieClient.GetAsync("/api/v1/entries/current");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        Log($"Expired access + invalid refresh correctly rejected, status: {response.StatusCode}");
    }
}
