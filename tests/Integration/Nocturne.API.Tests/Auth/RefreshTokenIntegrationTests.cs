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
[Collection(ApiIntegrationShards.One)]
[Trait("Category", "Integration")]
public class RefreshTokenIntegrationTests : ApiIntegrationTestBase
{
    private Guid _tenantId;
    private Guid _subjectId;
    private string _accessToken = null!;

    public RefreshTokenIntegrationTests(
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

        // Seed a subject for the refresh token tests
        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        _tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        (_subjectId, _accessToken) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, _tenantId, "Refresh Token Test User");
        _accessToken = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, _subjectId);

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

    private static Task<HttpResponseMessage> RefreshAsync(HttpClient client, string clientId, string refreshToken) =>
        client.PostAsync("/api/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = clientId
        }));

    private static async Task<string> RefreshTokenOfAsync(HttpResponseMessage response)
    {
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        return body.GetProperty("refresh_token").GetString()!;
    }

    /// <summary>
    /// Moves every rotation on <paramref name="clientId"/>'s grants a minute into the past, past
    /// the reuse grace window, as if the client came back with the old token much later.
    /// </summary>
    private async Task AgeRotationsAsync(string clientId)
    {
        await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT set_config('app.current_tenant_id', @tenant, false);
            UPDATE oauth_refresh_tokens
            SET revoked_at = revoked_at - interval '1 minute'
            WHERE revoked_at IS NOT NULL
              AND grant_id IN (SELECT g.id FROM oauth_grants g
                               JOIN oauth_clients c ON c.id = g.client_id
                               WHERE c.client_id = @client);
            """;
        cmd.Parameters.AddWithValue("tenant", _tenantId.ToString());
        cmd.Parameters.AddWithValue("client", clientId);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task RefreshToken_OldTokenRejectedAfterRotation()
    {
        using var authClient = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(authClient);
        var initial = await AuthTestHelpers.ExecutePkceFlowAsync(authClient, clientId);
        initial.RefreshToken.Should().NotBeNullOrWhiteSpace();

        var firstResponse = await RefreshAsync(authClient, clientId, initial.RefreshToken!);
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK, "first refresh should succeed");
        var successor = await RefreshTokenOfAsync(firstResponse);

        await AgeRotationsAsync(clientId);
        var replayResponse = await RefreshAsync(authClient, clientId, initial.RefreshToken!);

        replayResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = JsonSerializer.Deserialize<JsonElement>(await replayResponse.Content.ReadAsStringAsync());
        body.GetProperty("error").GetString().Should().Be("invalid_grant");
        (await RefreshAsync(authClient, clientId, successor)).StatusCode
            .Should().Be(HttpStatusCode.BadRequest, "reuse revokes the grant and every token it issued");
    }

    [Fact]
    public async Task RefreshToken_OldTokenRepresentedWithinTheGraceWindow_RedeemsOnce()
    {
        using var authClient = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(authClient);
        var initial = await AuthTestHelpers.ExecutePkceFlowAsync(authClient, clientId);

        var firstResponse = await RefreshAsync(authClient, clientId, initial.RefreshToken!);
        var graceResponse = await RefreshAsync(authClient, clientId, initial.RefreshToken!);
        var pastCapResponse = await RefreshAsync(authClient, clientId, initial.RefreshToken!);

        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        graceResponse.StatusCode.Should().Be(HttpStatusCode.OK, "a parallel refresh with the same token keeps the grant");
        pastCapResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest, "only one redemption fits in the grace window");
        (await RefreshAsync(authClient, clientId, await RefreshTokenOfAsync(graceResponse))).StatusCode
            .Should().Be(HttpStatusCode.BadRequest, "presenting the token past the cap revokes the grant");
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
