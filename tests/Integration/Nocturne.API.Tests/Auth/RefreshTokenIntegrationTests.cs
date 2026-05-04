using System.Net;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// Integration tests for OAuth 2.0 refresh token rotation, replay detection,
/// client binding, and revocation.
/// </summary>
[Trait("Category", "Integration")]
public class RefreshTokenIntegrationTests : AspireIntegrationTestBase
{
    private Guid _tenantId;
    private Guid _subjectId;
    private string _accessToken = null!;

    public RefreshTokenIntegrationTests(
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

        // Seed a subject for the refresh token tests
        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        _tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        (_subjectId, _accessToken) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, _tenantId, "Refresh Token Test User");

        Log($"Seeded tenant {_tenantId}, subject {_subjectId}");
    }

    [Fact]
    public async Task RefreshToken_ValidRotation_ReturnsNewTokens()
    {
        // Arrange — complete a PKCE flow to obtain initial tokens
        using var authClient = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(authClient);
        var initial = await AuthTestHelpers.ExecutePkceFlowAsync(authClient, clientId);
        initial.RefreshToken.Should().NotBeNullOrWhiteSpace("PKCE flow should issue a refresh token");

        // Act — exchange the refresh token for new tokens
        var refreshForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = initial.RefreshToken!,
            ["client_id"] = clientId
        });

        var response = await authClient.PostAsync("/api/oauth/token", refreshForm);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(content);

        var newAccessToken = body.GetProperty("access_token").GetString();
        var newRefreshToken = body.TryGetProperty("refresh_token", out var rtEl) ? rtEl.GetString() : null;

        newAccessToken.Should().NotBeNullOrWhiteSpace();
        newRefreshToken.Should().NotBeNullOrWhiteSpace();
        newAccessToken.Should().NotBe(initial.AccessToken, "rotated access token must differ from the original");
        newRefreshToken.Should().NotBe(initial.RefreshToken, "rotated refresh token must differ from the original");

        Log($"Rotation succeeded — new access_token prefix: {newAccessToken![..Math.Min(10, newAccessToken.Length)]}...");
    }

    [Fact]
    public async Task RefreshToken_OldTokenRejectedAfterRotation()
    {
        // Arrange — complete a PKCE flow and rotate once
        using var authClient = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(authClient);
        var initial = await AuthTestHelpers.ExecutePkceFlowAsync(authClient, clientId);
        initial.RefreshToken.Should().NotBeNullOrWhiteSpace();

        // Consume the original refresh token
        var firstRefresh = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = initial.RefreshToken!,
            ["client_id"] = clientId
        });

        var firstResponse = await authClient.PostAsync("/api/oauth/token", firstRefresh);
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK, "first refresh should succeed");

        // Act — replay the same (now-consumed) refresh token
        var replayForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = initial.RefreshToken!,
            ["client_id"] = clientId
        });

        var replayResponse = await authClient.PostAsync("/api/oauth/token", replayForm);

        // Assert
        replayResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await replayResponse.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(content);
        body.GetProperty("error").GetString().Should().NotBeNullOrWhiteSpace();

        Log("Old refresh token correctly rejected after rotation");
    }

    [Fact]
    public async Task RefreshToken_WrongClientId_Rejected()
    {
        // Arrange — complete a PKCE flow with client A
        using var authClient = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var clientIdA = await AuthTestHelpers.RegisterOAuthClientAsync(authClient);
        var result = await AuthTestHelpers.ExecutePkceFlowAsync(authClient, clientIdA);
        result.RefreshToken.Should().NotBeNullOrWhiteSpace();

        // Register a second client (B)
        var clientIdB = await AuthTestHelpers.RegisterOAuthClientAsync(authClient);
        clientIdB.Should().NotBe(clientIdA);

        // Act — try to refresh client A's token using client B's client_id
        var refreshForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = result.RefreshToken!,
            ["client_id"] = clientIdB
        });

        var response = await authClient.PostAsync("/api/oauth/token", refreshForm);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(content);
        body.GetProperty("error").GetString().Should().NotBeNullOrWhiteSpace();

        Log("Refresh token correctly rejected when presented with wrong client_id");
    }

    [Fact]
    public async Task RefreshToken_RevokedGrant_Rejected()
    {
        // Arrange — complete a PKCE flow
        using var authClient = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(authClient);
        var result = await AuthTestHelpers.ExecutePkceFlowAsync(authClient, clientId);
        result.RefreshToken.Should().NotBeNullOrWhiteSpace();

        // Revoke the refresh token
        var revokeForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = result.RefreshToken!,
            ["token_type_hint"] = "refresh_token"
        });

        var revokeResponse = await authClient.PostAsync("/api/oauth/revoke", revokeForm);
        revokeResponse.StatusCode.Should().BeOneOf(
            [HttpStatusCode.OK, HttpStatusCode.NoContent],
            "revocation endpoint should accept the request");

        // Act — try to use the revoked refresh token
        var refreshForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = result.RefreshToken!,
            ["client_id"] = clientId
        });

        var response = await authClient.PostAsync("/api/oauth/token", refreshForm);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(content);
        body.GetProperty("error").GetString().Should().NotBeNullOrWhiteSpace();

        Log("Revoked refresh token correctly rejected");
    }
}
