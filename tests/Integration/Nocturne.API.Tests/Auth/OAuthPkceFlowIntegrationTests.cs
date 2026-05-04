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
/// Integration tests for the OAuth 2.0 Authorization Code + PKCE flow (RFC 7636).
/// Covers dynamic client registration, authorization endpoint validation,
/// token exchange (happy path and error cases), and scope enforcement.
/// </summary>
[Trait("Category", "Integration")]
public class OAuthPkceFlowIntegrationTests : AspireIntegrationTestBase
{
    private Guid _tenantId;
    private Guid _subjectId;
    private string _accessToken = null!;

    public OAuthPkceFlowIntegrationTests(
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

        // Seed a subject for the PKCE flow tests
        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        _tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        (_subjectId, _accessToken) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, _tenantId, "PKCE Test User");

        Log($"Seeded tenant {_tenantId}, subject {_subjectId}");
    }

    #region Dynamic Client Registration

    [Fact]
    public async Task Register_ValidClient_ReturnsClientId()
    {
        // Arrange
        using var client = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);

        // Act
        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(client);

        // Assert
        clientId.Should().NotBeNullOrWhiteSpace();
        Log($"Registered OAuth client: {clientId}");
    }

    #endregion

    #region Authorize Endpoint Validation

    [Fact]
    public async Task Authorize_Unauthenticated_RedirectsToLogin()
    {
        // Arrange - register a client first
        using var authClient = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(authClient);
        var (_, codeChallenge) = AuthTestHelpers.GeneratePkceChallenge();

        // Act - GET /api/oauth/authorize with no auth (use a no-redirect client)
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var unauthClient = new HttpClient(handler)
        {
            BaseAddress = ApiClient.BaseAddress
        };

        var query = $"?client_id={clientId}&redirect_uri={Uri.EscapeDataString("http://localhost:9999/callback")}" +
                    $"&response_type=code&scope={Uri.EscapeDataString("entries.read treatments.read")}" +
                    $"&code_challenge={codeChallenge}&code_challenge_method=S256";

        var response = await unauthClient.GetAsync($"/api/oauth/authorize{query}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.ToString().Should().Contain("/auth/login");
    }

    [Fact]
    public async Task Authorize_MissingPkce_Returns400()
    {
        // Arrange
        using var authClient = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(authClient);

        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var noRedirectClient = new HttpClient(handler)
        {
            BaseAddress = ApiClient.BaseAddress
        };
        foreach (var header in authClient.DefaultRequestHeaders)
            noRedirectClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);

        // Act - GET /api/oauth/authorize WITHOUT code_challenge
        var query = $"?client_id={clientId}&redirect_uri={Uri.EscapeDataString("http://localhost:9999/callback")}" +
                    $"&response_type=code&scope={Uri.EscapeDataString("entries.read treatments.read")}";

        var response = await noRedirectClient.GetAsync($"/api/oauth/authorize{query}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(content);
        body.GetProperty("error").GetString().Should().Be("invalid_request");
    }

    [Fact]
    public async Task Authorize_InvalidScope_Returns400()
    {
        // Arrange
        using var authClient = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(authClient);
        var (_, codeChallenge) = AuthTestHelpers.GeneratePkceChallenge();

        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var noRedirectClient = new HttpClient(handler)
        {
            BaseAddress = ApiClient.BaseAddress
        };
        foreach (var header in authClient.DefaultRequestHeaders)
            noRedirectClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);

        // Act - GET /api/oauth/authorize with bogus scope
        var query = $"?client_id={clientId}&redirect_uri={Uri.EscapeDataString("http://localhost:9999/callback")}" +
                    $"&response_type=code&scope={Uri.EscapeDataString("bogus.scope.nonexistent")}" +
                    $"&code_challenge={codeChallenge}&code_challenge_method=S256";

        var response = await noRedirectClient.GetAsync($"/api/oauth/authorize{query}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(content);
        body.GetProperty("error").GetString().Should().Be("invalid_scope");
    }

    [Fact]
    public async Task Authorize_UnknownClient_Returns400()
    {
        // Arrange
        using var authClient = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var (_, codeChallenge) = AuthTestHelpers.GeneratePkceChallenge();

        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var noRedirectClient = new HttpClient(handler)
        {
            BaseAddress = ApiClient.BaseAddress
        };
        foreach (var header in authClient.DefaultRequestHeaders)
            noRedirectClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);

        // Act - GET /api/oauth/authorize with nonexistent client_id
        var query = $"?client_id=nonexistent-client-{Guid.NewGuid():N}" +
                    $"&redirect_uri={Uri.EscapeDataString("http://localhost:9999/callback")}" +
                    $"&response_type=code&scope={Uri.EscapeDataString("entries.read treatments.read")}" +
                    $"&code_challenge={codeChallenge}&code_challenge_method=S256";

        var response = await noRedirectClient.GetAsync($"/api/oauth/authorize{query}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(content);
        body.GetProperty("error").GetString().Should().Be("invalid_client");
    }

    [Fact]
    public async Task Authorize_InvalidRedirectUri_Returns400()
    {
        // Arrange
        using var authClient = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(authClient);
        var (_, codeChallenge) = AuthTestHelpers.GeneratePkceChallenge();

        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var noRedirectClient = new HttpClient(handler)
        {
            BaseAddress = ApiClient.BaseAddress
        };
        foreach (var header in authClient.DefaultRequestHeaders)
            noRedirectClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);

        // Act - GET /api/oauth/authorize with wrong redirect_uri
        var query = $"?client_id={clientId}" +
                    $"&redirect_uri={Uri.EscapeDataString("http://evil.example.com/callback")}" +
                    $"&response_type=code&scope={Uri.EscapeDataString("entries.read treatments.read")}" +
                    $"&code_challenge={codeChallenge}&code_challenge_method=S256";

        var response = await noRedirectClient.GetAsync($"/api/oauth/authorize{query}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(content);
        body.GetProperty("error").GetString().Should().Be("invalid_request");
    }

    #endregion

    #region Token Exchange

    [Fact]
    public async Task TokenExchange_ValidCode_ReturnsTokens()
    {
        // Arrange
        using var authClient = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(authClient);

        // Act - full PKCE flow
        var result = await AuthTestHelpers.ExecutePkceFlowAsync(authClient, clientId);

        // Assert
        result.AccessToken.Should().NotBeNullOrWhiteSpace();
        result.RefreshToken.Should().NotBeNullOrWhiteSpace();
        result.AuthorizationCode.Should().NotBeNullOrWhiteSpace();
        Log($"Token exchange succeeded, access_token prefix: {result.AccessToken[..Math.Min(10, result.AccessToken.Length)]}...");
    }

    [Fact]
    public async Task TokenExchange_InvalidCodeVerifier_Fails()
    {
        // Arrange - get an authorization code via the consent flow
        using var authClient = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(authClient);
        var (codeVerifier, codeChallenge) = AuthTestHelpers.GeneratePkceChallenge();

        // POST consent to get a code
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var noRedirectClient = new HttpClient(handler)
        {
            BaseAddress = authClient.BaseAddress
        };
        foreach (var header in authClient.DefaultRequestHeaders)
            noRedirectClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);

        var consentForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = "http://localhost:9999/callback",
            ["scope"] = "entries.read treatments.read",
            ["code_challenge"] = codeChallenge,
            ["approved"] = "true"
        });

        var authorizeResponse = await noRedirectClient.PostAsync("/api/oauth/authorize", consentForm);
        var location = authorizeResponse.Headers.Location!;
        var query = System.Web.HttpUtility.ParseQueryString(location.Query);
        var code = query["code"]!;

        // Act - exchange with wrong code_verifier
        var tokenForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = "wrong-verifier-that-does-not-match-challenge-at-all-padding",
            ["redirect_uri"] = "http://localhost:9999/callback",
            ["client_id"] = clientId
        });

        var tokenResponse = await authClient.PostAsync("/api/oauth/token", tokenForm);

        // Assert
        tokenResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await tokenResponse.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(content);
        body.GetProperty("error").GetString().Should().Be("invalid_grant");
    }

    [Fact]
    public async Task TokenExchange_ExpiredCode_Fails()
    {
        // Arrange - register a client, then seed an expired auth code directly via SQL
        using var authClient = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(authClient);

        var expiredCode = $"expired-code-{Guid.NewGuid():N}";
        var codeHash = AuthTestHelpers.ComputeSha256Hex(expiredCode);
        var (codeVerifier, codeChallenge) = AuthTestHelpers.GeneratePkceChallenge();

        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        // Look up the client entity ID
        Guid clientEntityId;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id FROM oauth_clients WHERE client_id = @clientId LIMIT 1;";
            cmd.Parameters.AddWithValue("clientId", clientId);
            clientEntityId = (Guid)(await cmd.ExecuteScalarAsync())!;
        }

        // Set tenant context for RLS
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT set_config('app.current_tenant_id', @tenantId, false);";
            cmd.Parameters.AddWithValue("tenantId", _tenantId.ToString());
            await cmd.ExecuteNonQueryAsync();
        }

        // Insert an expired authorization code
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO oauth_authorization_codes
                    (id, tenant_id, client_entity_id, subject_id, code_hash, scopes, redirect_uri, code_challenge, expires_at, created_at, limit_to_24_hours)
                VALUES
                    (@id, @tenantId, @clientEntityId, @subjectId, @codeHash, '["entries.read","treatments.read"]'::jsonb, @redirectUri, @codeChallenge, @expiresAt, @createdAt, false);
                """;
            cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
            cmd.Parameters.AddWithValue("tenantId", _tenantId);
            cmd.Parameters.AddWithValue("clientEntityId", clientEntityId);
            cmd.Parameters.AddWithValue("subjectId", _subjectId);
            cmd.Parameters.AddWithValue("codeHash", codeHash);
            cmd.Parameters.AddWithValue("redirectUri", "http://localhost:9999/callback");
            cmd.Parameters.AddWithValue("codeChallenge", codeChallenge);
            cmd.Parameters.AddWithValue("expiresAt", DateTime.UtcNow.AddMinutes(-5));
            cmd.Parameters.AddWithValue("createdAt", DateTime.UtcNow.AddMinutes(-15));
            await cmd.ExecuteNonQueryAsync();
        }

        // Act - try to exchange the expired code
        var tokenForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = expiredCode,
            ["code_verifier"] = codeVerifier,
            ["redirect_uri"] = "http://localhost:9999/callback",
            ["client_id"] = clientId
        });

        var tokenResponse = await authClient.PostAsync("/api/oauth/token", tokenForm);

        // Assert
        tokenResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await tokenResponse.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(content);
        body.GetProperty("error").GetString().Should().Be("invalid_grant");
    }

    [Fact]
    public async Task TokenExchange_CodeReuse_Fails()
    {
        // Arrange - complete a full PKCE flow to consume the code
        using var authClient = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(authClient);
        var result = await AuthTestHelpers.ExecutePkceFlowAsync(authClient, clientId);

        // Act - try to exchange the same code again
        var tokenForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = result.AuthorizationCode,
            ["code_verifier"] = result.CodeVerifier,
            ["redirect_uri"] = "http://localhost:9999/callback",
            ["client_id"] = clientId
        });

        var tokenResponse = await authClient.PostAsync("/api/oauth/token", tokenForm);

        // Assert - second exchange should fail
        tokenResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await tokenResponse.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(content);
        body.GetProperty("error").GetString().Should().Be("invalid_grant");
    }

    [Fact]
    public async Task TokenExchange_WrongRedirectUri_Fails()
    {
        // Arrange - get an authorization code
        using var authClient = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(authClient);
        var (codeVerifier, codeChallenge) = AuthTestHelpers.GeneratePkceChallenge();

        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var noRedirectClient = new HttpClient(handler)
        {
            BaseAddress = authClient.BaseAddress
        };
        foreach (var header in authClient.DefaultRequestHeaders)
            noRedirectClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);

        var consentForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = "http://localhost:9999/callback",
            ["scope"] = "entries.read treatments.read",
            ["code_challenge"] = codeChallenge,
            ["approved"] = "true"
        });

        var authorizeResponse = await noRedirectClient.PostAsync("/api/oauth/authorize", consentForm);
        var location = authorizeResponse.Headers.Location!;
        var queryParams = System.Web.HttpUtility.ParseQueryString(location.Query);
        var code = queryParams["code"]!;

        // Act - exchange with a different redirect_uri
        var tokenForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
            ["redirect_uri"] = "http://localhost:8888/different-callback",
            ["client_id"] = clientId
        });

        var tokenResponse = await authClient.PostAsync("/api/oauth/token", tokenForm);

        // Assert
        tokenResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await tokenResponse.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(content);
        body.GetProperty("error").GetString().Should().Be("invalid_grant");
    }

    #endregion

    #region Access Token Usage

    [Fact]
    public async Task AccessToken_FromPkceFlow_CanReadEntries()
    {
        // Arrange - complete a full PKCE flow
        using var authClient = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(authClient);
        var result = await AuthTestHelpers.ExecutePkceFlowAsync(authClient, clientId);

        // Act - use the PKCE-issued access token to read entries
        using var bearerClient = AuthTestHelpers.CreateBearerClient(Fixture, result.AccessToken);
        var response = await bearerClient.GetAsync("/api/v1/entries/current");

        // Assert - should not be 401 or 403
        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
        Log($"Read entries with PKCE token, status: {response.StatusCode}");
    }

    [Fact]
    public async Task AccessToken_ScopeEnforcement_RejectsOutOfScope()
    {
        // Arrange - complete a PKCE flow with read-only scope
        using var authClient = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);
        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(authClient, scope: "entries.read");
        var result = await AuthTestHelpers.ExecutePkceFlowAsync(authClient, clientId, scope: "entries.read");

        // Act - try to write an entry with a read-only token
        using var bearerClient = AuthTestHelpers.CreateBearerClient(Fixture, result.AccessToken);
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

        var response = await bearerClient.PostAsJsonAsync("/api/v1/entries", entryPayload);

        // Assert - should be rejected (401 or 403)
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        Log($"Scope enforcement on write attempt, status: {response.StatusCode}");
    }

    #endregion
}
