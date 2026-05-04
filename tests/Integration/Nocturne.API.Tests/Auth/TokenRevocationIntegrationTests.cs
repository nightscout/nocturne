using System.Net;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// Integration tests for OAuth 2.0 Token Revocation (RFC 7009) and
/// Token Introspection (RFC 7662) endpoints.
/// </summary>
[Trait("Category", "Integration")]
public class TokenRevocationIntegrationTests : AspireIntegrationTestBase
{
    private Guid _tenantId;
    private Guid _subjectId;
    private string _accessToken = null!;

    public TokenRevocationIntegrationTests(
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

        // Seed a subject for the OAuth flow tests
        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        _tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        (_subjectId, _accessToken) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, _tenantId, "Revocation Test User");

        Log($"Seeded tenant {_tenantId}, subject {_subjectId}");
    }

    [Fact]
    public async Task RevokeAccessToken_StopsWorking()
    {
        // Arrange — complete a PKCE flow to get tokens
        using var adminClient = CreateAuthenticatedClient();
        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(adminClient);
        using var authedClient = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var tokens = await AuthTestHelpers.ExecutePkceFlowAsync(authedClient, clientId);

        // Verify the access token is active before revocation
        var introspectBefore = await ApiClient.PostAsync("/api/oauth/introspect",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = tokens.AccessToken,
                ["token_type_hint"] = "access_token"
            }));
        introspectBefore.StatusCode.Should().Be(HttpStatusCode.OK);
        var beforeBody = JsonSerializer.Deserialize<JsonElement>(await introspectBefore.Content.ReadAsStringAsync());
        beforeBody.GetProperty("active").GetBoolean().Should().BeTrue("token should be active before revocation");

        // Act — revoke the access token
        var revokeResponse = await ApiClient.PostAsync("/api/oauth/revoke",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = tokens.AccessToken,
                ["token_type_hint"] = "access_token"
            }));

        // Assert — revoke always returns 200
        revokeResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Introspect again — should now be inactive
        var introspectAfter = await ApiClient.PostAsync("/api/oauth/introspect",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = tokens.AccessToken,
                ["token_type_hint"] = "access_token"
            }));
        introspectAfter.StatusCode.Should().Be(HttpStatusCode.OK);
        var afterBody = JsonSerializer.Deserialize<JsonElement>(await introspectAfter.Content.ReadAsStringAsync());
        afterBody.GetProperty("active").GetBoolean().Should().BeFalse("token should be inactive after revocation");
    }

    [Fact]
    public async Task RevokeRefreshToken_StopsWorking()
    {
        // Arrange — complete a PKCE flow to get tokens (including refresh token)
        using var adminClient = CreateAuthenticatedClient();
        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(adminClient);
        using var authedClient = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var tokens = await AuthTestHelpers.ExecutePkceFlowAsync(authedClient, clientId);

        tokens.RefreshToken.Should().NotBeNullOrWhiteSpace("PKCE flow should return a refresh token");

        // Act — revoke the refresh token
        var revokeResponse = await ApiClient.PostAsync("/api/oauth/revoke",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = tokens.RefreshToken!,
                ["token_type_hint"] = "refresh_token"
            }));
        revokeResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Assert — attempting to use the revoked refresh token should fail
        var refreshResponse = await ApiClient.PostAsync("/api/oauth/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = tokens.RefreshToken!,
                ["client_id"] = clientId
            }));
        refreshResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "using a revoked refresh token should return 400");
    }

    [Fact]
    public async Task Revoke_UnknownToken_Returns200()
    {
        // Act — revoke a token that does not exist (RFC 7009 requires 200)
        var revokeResponse = await ApiClient.PostAsync("/api/oauth/revoke",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = $"unknown-token-{Guid.NewGuid():N}"
            }));

        // Assert
        revokeResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "RFC 7009 requires 200 even for unknown tokens");
    }

    [Fact]
    public async Task Introspect_ValidToken_ReturnsActive()
    {
        // Arrange — complete a PKCE flow to get a valid access token
        using var adminClient = CreateAuthenticatedClient();
        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(adminClient);
        using var authedClient = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var tokens = await AuthTestHelpers.ExecutePkceFlowAsync(authedClient, clientId);

        // Act
        var introspectResponse = await ApiClient.PostAsync("/api/oauth/introspect",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = tokens.AccessToken
            }));

        // Assert
        introspectResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await introspectResponse.Content.ReadAsStringAsync());

        body.GetProperty("active").GetBoolean().Should().BeTrue();
        body.TryGetProperty("sub", out _).Should().BeTrue("introspection response should include sub claim");
        body.TryGetProperty("exp", out _).Should().BeTrue("introspection response should include exp claim");
        body.TryGetProperty("token_type", out _).Should().BeTrue("introspection response should include token_type");
    }

    [Fact]
    public async Task Introspect_ExpiredToken_ReturnsInactive()
    {
        // Act — introspect a clearly invalid JWT (expired / malformed)
        var introspectResponse = await ApiClient.PostAsync("/api/oauth/introspect",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwiZXhwIjoxfQ.invalid-signature",
                ["token_type_hint"] = "access_token"
            }));

        // Assert
        introspectResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await introspectResponse.Content.ReadAsStringAsync());
        body.GetProperty("active").GetBoolean().Should().BeFalse("an expired/invalid JWT should not be active");
    }

    [Fact]
    public async Task Introspect_GarbageToken_ReturnsInactive()
    {
        // Act — introspect a string that is clearly not a token
        var introspectResponse = await ApiClient.PostAsync("/api/oauth/introspect",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = "not-a-token"
            }));

        // Assert
        introspectResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await introspectResponse.Content.ReadAsStringAsync());
        body.GetProperty("active").GetBoolean().Should().BeFalse("garbage input should not be active");
    }
}
