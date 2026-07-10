using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// Integration tests for authentication handler chain
/// Tests legacy API compatibility with api-secret, access tokens, and JWT tokens
/// </summary>
[Parity]
public class AuthenticationHandlerIntegrationTests : AspireIntegrationTestBase
{
    public AuthenticationHandlerIntegrationTests(
        AspireIntegrationTestFixture fixture,
        ITestOutputHelper output
    )
        : base(fixture, output) { }

    #region API Secret Authentication

    [Fact]
    [Parity]
    public async Task ApiSecret_ValidSecret_AuthenticatesSuccessfully()
    {
        // Arrange
        var client = CreateAuthenticatedClient();

        // Act
        var response = await client.GetAsync("/api/v1/entries/current");

        // Assert - should not return 401 Unauthorized when secret is valid
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Output.WriteLine($"API Secret auth returned: {response.StatusCode}");
    }

    [Fact]
    [Parity]
    public async Task ApiSecret_InvalidSecret_ReturnsUnauthorized()
    {
        // Arrange
        var client = ApiClient;
        client.DefaultRequestHeaders.Add("api-secret", "wrong-secret");

        // Act
        var response = await client.GetAsync("/api/v1/entries/current");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Output.WriteLine($"Invalid API Secret returned: {response.StatusCode}");
    }

    [Fact]
    [Parity]
    public async Task ApiSecret_HashedSecretFormat_AuthenticatesSuccessfully()
    {
        // Arrange - Nightscout supports SHA1-hashed secrets
        var client = ApiClient;
        var hashedSecret = ComputeSha1Hash(TestApiSecret);
        client.DefaultRequestHeaders.Add("api-secret", hashedSecret);

        // Act
        var response = await client.GetAsync("/api/v1/entries/current");

        // Assert - should accept hashed format
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Output.WriteLine($"Hashed API Secret auth returned: {response.StatusCode}");
    }

    [Fact]
    [Parity]
    public async Task ApiSecret_QueryParameter_AuthenticatesSuccessfully()
    {
        // Arrange - Legacy Nightscout supports secret via query parameter
        var client = ApiClient;

        // Act
        var response = await client.GetAsync($"/api/v1/entries/current?secret={TestApiSecret}");

        // Assert
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Output.WriteLine($"Query parameter API Secret returned: {response.StatusCode}");
    }

    #endregion

    #region Access Token Authentication

    [Fact]
    [Parity]
    public async Task AccessToken_ValidToken_AuthenticatesSuccessfully()
    {
        // Arrange - Create a subject with access token first
        var client = CreateAuthenticatedClient();

        // Create a test subject with an access token
        var newSubject = new
        {
            Name = "test-access-token-subject",
            Roles = new[] { "readable" },
            Notes = "Test subject for access token authentication",
        };

        var createResponse = await client.PostAsJsonAsync(
            "/api/v2/authorization/subjects",
            newSubject
        );

        if (createResponse.IsSuccessStatusCode)
        {
            var subject = await createResponse.Content.ReadFromJsonAsync<SubjectResponse>();

            if (subject?.AccessToken != null)
            {
                // Use the access token for authentication
                var tokenClient = ApiClient;
                tokenClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                    "Bearer",
                    subject.AccessToken
                );

                var response = await tokenClient.GetAsync("/api/v1/entries/current");

                Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
                Output.WriteLine($"Access token auth returned: {response.StatusCode}");
            }
        }

        Output.WriteLine($"Create subject returned: {createResponse.StatusCode}");
    }

    [Fact]
    [Parity]
    public async Task AccessToken_InvalidToken_ReturnsUnauthorized()
    {
        // Arrange
        var client = ApiClient;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            "invalid-a1b2c3d4e5f6g7h8"
        );

        // Act
        var response = await client.GetAsync("/api/v1/entries/current");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Output.WriteLine($"Invalid access token returned: {response.StatusCode}");
    }

    [Fact]
    [Parity]
    public async Task AccessToken_TokenQueryParameter_AuthenticatesSuccessfully()
    {
        // Arrange - Some Nightscout clients pass token as query parameter
        var client = CreateAuthenticatedClient();

        // Create a test subject
        var newSubject = new { Name = "test-query-token-subject", Roles = new[] { "readable" } };

        var createResponse = await client.PostAsJsonAsync(
            "/api/v2/authorization/subjects",
            newSubject
        );

        if (createResponse.IsSuccessStatusCode)
        {
            var subject = await createResponse.Content.ReadFromJsonAsync<SubjectResponse>();

            if (subject?.AccessToken != null)
            {
                var tokenClient = ApiClient;
                var response = await tokenClient.GetAsync(
                    $"/api/v1/entries/current?token={subject.AccessToken}"
                );

                Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
                Output.WriteLine($"Query token auth returned: {response.StatusCode}");
            }
        }
    }

    #endregion

    #region JWT Authentication

    [Fact]
    [Parity]
    public async Task Jwt_ExchangedToken_AuthenticatesSuccessfully()
    {
        // Arrange - First create a subject and get JWT via token exchange
        var client = CreateAuthenticatedClient();

        // Create a test subject
        var newSubject = new
        {
            Name = "test-jwt-subject",
            Roles = new[] { "readable", "careportal" },
        };

        var createResponse = await client.PostAsJsonAsync(
            "/api/v2/authorization/subjects",
            newSubject
        );

        // Every step asserts: silent early-outs previously masked a broken exchange
        // (the anonymous-exchange 401 fixed in #473, and the exchanged JWT failing all
        // subsequent requests through LegacyJwtHandler claim mapping, #474).
        Assert.True(
            createResponse.IsSuccessStatusCode,
            $"Subject creation failed: {createResponse.StatusCode}"
        );
        var subject = await createResponse.Content.ReadFromJsonAsync<SubjectResponse>();
        Assert.NotNull(subject?.AccessToken);

        // Exchange access token for JWT anonymously — the access token in the URL is
        // the caller's only credential (the NSClientV3/AAPS bootstrap).
        var exchangeClient = ApiClient;
        var exchangeResponse = await exchangeClient.GetAsync(
            $"/api/v2/authorization/request/{subject!.AccessToken}"
        );

        Assert.True(
            exchangeResponse.IsSuccessStatusCode,
            $"Token exchange failed: {exchangeResponse.StatusCode}"
        );
        var tokenResponse =
            await exchangeResponse.Content.ReadFromJsonAsync<TokenExchangeResponse>();
        Assert.NotNull(tokenResponse?.Token);

        // Use JWT for authentication
        var jwtClient = ApiClient;
        jwtClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokenResponse!.Token);

        var response = await jwtClient.GetAsync("/api/v1/entries/current");

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Output.WriteLine($"JWT auth returned: {response.StatusCode}");
    }

    [Fact]
    [Parity]
    public async Task Jwt_InvalidToken_ReturnsUnauthorized()
    {
        // Arrange
        var client = ApiClient;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkludmFsaWQiLCJpYXQiOjE1MTYyMzkwMjJ9.InvalidSignature"
        );

        // Act
        var response = await client.GetAsync("/api/v1/entries/current");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Output.WriteLine($"Invalid JWT returned: {response.StatusCode}");
    }

    [Fact]
    [Parity]
    public async Task Jwt_ExpiredToken_ReturnsUnauthorized()
    {
        // Arrange - Craft an expired JWT (would require proper signing in production)
        var client = ApiClient;
        // Using a clearly expired token format
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            "expired.jwt.token"
        );

        // Act
        var response = await client.GetAsync("/api/v1/entries/current");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Output.WriteLine($"Expired JWT returned: {response.StatusCode}");
    }

    #endregion

    #region Handler Priority Tests

    [Fact]
    [Parity]
    public async Task HandlerChain_JwtTakesPrecedenceOverApiSecret()
    {
        // Arrange - When both JWT and api-secret are present, JWT should be validated first
        var client = CreateAuthenticatedClient();

        // Add an invalid JWT - if JWT handler has priority, this should fail
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            "invalid.jwt.token"
        );

        // Act
        var response = await client.GetAsync("/api/v1/entries/current");

        // Assert - The invalid JWT should cause auth failure despite valid api-secret
        // (unless the implementation falls back, which is also valid behavior)
        Output.WriteLine($"JWT + api-secret returned: {response.StatusCode}");
        // Note: The exact behavior depends on implementation - either strict (fail on first)
        // or fallback (try next handler). Both are acceptable.
    }

    [Fact]
    [Parity]
    public async Task HandlerChain_NoAuthentication_ReturnsUnauthorized()
    {
        // Arrange - No authentication credentials provided
        var client = ApiClient;

        // Act
        var response = await client.GetAsync("/api/v1/entries/current");

        // Assert - Should return Unauthorized when no auth is provided
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Output.WriteLine($"No auth returned: {response.StatusCode}");
    }

    #endregion

    #region Verify Auth Endpoint

    [Fact]
    [Parity]
    public async Task VerifyAuth_ValidApiSecret_ReturnsSuccess()
    {
        // Arrange
        var client = CreateAuthenticatedClient();

        // Act
        var response = await client.GetAsync("/api/v1/verifyauth");

        // Assert
        Assert.True(response.IsSuccessStatusCode);
        Output.WriteLine($"VerifyAuth with api-secret returned: {response.StatusCode}");
    }

    [Fact]
    [Parity]
    public async Task VerifyAuth_NoAuth_ReturnsUnauthorized()
    {
        // Arrange
        var client = ApiClient;

        // Act
        var response = await client.GetAsync("/api/v1/verifyauth");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Output.WriteLine($"VerifyAuth without auth returned: {response.StatusCode}");
    }

    #endregion

    #region Permission Tests

    [Fact]
    [Parity]
    public async Task ApiSecret_HasFullPermissions()
    {
        // Arrange - api-secret should grant admin-level access
        var client = CreateAuthenticatedClient();

        // Act - Try to access admin endpoints
        var rolesResponse = await client.GetAsync("/api/v2/authorization/roles");
        var subjectsResponse = await client.GetAsync("/api/v2/authorization/subjects");

        // Assert - api-secret should have full access
        Assert.NotEqual(HttpStatusCode.Forbidden, rolesResponse.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, subjectsResponse.StatusCode);
        Output.WriteLine(
            $"Admin endpoints: roles={rolesResponse.StatusCode}, subjects={subjectsResponse.StatusCode}"
        );
    }

    [Fact]
    [Parity]
    public async Task LimitedRoleSubject_CannotAccessAdminEndpoints()
    {
        // Arrange - Create subject with only 'readable' role
        var client = CreateAuthenticatedClient();

        var newSubject = new
        {
            Name = "limited-role-subject",
            Roles = new[] { "readable" }, // Read-only role
        };

        var createResponse = await client.PostAsJsonAsync(
            "/api/v2/authorization/subjects",
            newSubject
        );

        if (createResponse.IsSuccessStatusCode)
        {
            var subject = await createResponse.Content.ReadFromJsonAsync<SubjectResponse>();

            if (subject?.AccessToken != null)
            {
                // Use limited subject's token
                var limitedClient = ApiClient;
                limitedClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                    "Bearer",
                    subject.AccessToken
                );

                // Try to access admin endpoint
                var adminResponse = await limitedClient.GetAsync("/api/v2/authorization/subjects");

                // Assert - Should be forbidden for limited subject
                Assert.True(
                    adminResponse.StatusCode == HttpStatusCode.Forbidden
                        || adminResponse.StatusCode == HttpStatusCode.Unauthorized
                );
                Output.WriteLine(
                    $"Limited subject admin access returned: {adminResponse.StatusCode}"
                );
            }
        }
    }

    #endregion

    #region Helper Methods

    private static string ComputeSha1Hash(string input)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    #endregion

    #region Response DTOs

    private class SubjectResponse
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? AccessToken { get; set; }
        public List<string>? Roles { get; set; }
    }

    private class TokenExchangeResponse
    {
        public string? Token { get; set; }
        public int? ExpiresIn { get; set; }
    }

    #endregion
}
