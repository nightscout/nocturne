using System.Net;
using System.Security.Cryptography;
using System.Text;
using Nocturne.API.Tests.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V1;

/// <summary>
/// Regression tests for V1 API authentication on the <b>bare tenant host</b>
/// (<c>{slug}.{baseDomain}</c>).
///
/// As of the public-share-link feature the bare host is <b>login-only</b>: anonymous reads are no
/// longer served there even when the tenant's Public subject carries a read role. Anonymous public
/// read moved to the rotatable share host (<c>{token}.share.{baseDomain}</c>), covered by the
/// share-host / AuthenticationMiddleware suites. So on the bare host:
///   - GET (read) endpoints require authentication (api-secret or session) — unauthenticated → 401
///   - POST/PUT/DELETE (write) endpoints require the api-secret header
///   - The api-secret header contains a SHA1 hash of the configured secret
///
/// These tests ensure the bare-host authentication contract never regresses.
/// </summary>
[Trait("Category", "Unit")]
public class V1AuthenticationRegressionTests : IClassFixture<AuthenticationTestFactory>, IDisposable
{
    private readonly AuthenticationTestFactory _factory;
    private readonly HttpClient _anonymousClient;
    private readonly HttpClient _authenticatedClient;

    public V1AuthenticationRegressionTests(AuthenticationTestFactory factory)
    {
        _factory = factory;

        // Client with no authentication (anonymous)
        _anonymousClient = _factory.CreateClient();

        // Client with valid api-secret authentication (uses the factory's configured secret)
        _authenticatedClient = _factory.CreateClient();
        _authenticatedClient.DefaultRequestHeaders.Add("api-secret",
            ComputeSha1Hash(AuthenticationTestFactory.ApiSecret));
    }

    public void Dispose()
    {
        _anonymousClient.Dispose();
        _authenticatedClient.Dispose();
    }

    // ====================================================================
    // GET endpoints on the bare host: login-only (unauthenticated → 401)
    // ====================================================================

    [Theory]
    [InlineData("/api/v1/entries")]
    [InlineData("/api/v1/entries/current")]
    [InlineData("/api/v1/entries/sgv")]
    [InlineData("/api/v1/treatments")]
    [InlineData("/api/v1/devicestatus")]
    [InlineData("/api/v1/food")]
    [InlineData("/api/v1/profile")]
    [InlineData("/api/v1/profile/current")]
    [InlineData("/api/v1/activity")]
    [InlineData("/api/v1/adminnotifies")]
    public async Task V1_GetEndpoints_OnBareHost_RejectUnauthenticatedRequests(string endpoint)
    {
        // Act
        var response = await _anonymousClient.GetAsync(endpoint);

        // Assert - the bare host is login-only; anonymous read moved to the share host.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/entries")]
    [InlineData("/api/v1/entries/current")]
    [InlineData("/api/v1/entries/sgv")]
    [InlineData("/api/v1/treatments")]
    [InlineData("/api/v1/devicestatus")]
    [InlineData("/api/v1/food")]
    [InlineData("/api/v1/profile")]
    [InlineData("/api/v1/profile/current")]
    [InlineData("/api/v1/activity")]
    [InlineData("/api/v1/adminnotifies")]
    public async Task V1_GetEndpoints_WithValidApiSecret_AreAccessible(string endpoint)
    {
        // Act
        var response = await _authenticatedClient.GetAsync(endpoint);

        // Assert - authenticated reads work (may be 200, 204, 304, etc.) but never 401/403.
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ====================================================================
    // POST endpoints: must reject unauthenticated requests
    // ====================================================================

    [Theory]
    [InlineData("/api/v1/entries")]
    [InlineData("/api/v1/treatments")]
    [InlineData("/api/v1/devicestatus")]
    [InlineData("/api/v1/food")]
    [InlineData("/api/v1/profile")]
    [InlineData("/api/v1/activity")]
    [InlineData("/api/v1/notifications/ack")]
    [InlineData("/api/v1/adminnotifies")]
    [InlineData("/api/v1/notifications/pushover")]
    public async Task V1_PostEndpoints_ShouldRejectUnauthenticatedRequests(string endpoint)
    {
        // Arrange
        var content = new StringContent("{}", Encoding.UTF8, "application/json");

        // Act
        var response = await _anonymousClient.PostAsync(endpoint, content);

        // Assert - unauthenticated write should be blocked
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/entries/abc123abc123abc123abc123")]
    [InlineData("/api/v1/treatments/test-id")]
    [InlineData("/api/v1/food/test-id")]
    [InlineData("/api/v1/activity/test-id")]
    public async Task V1_PutEndpoints_ShouldRejectUnauthenticatedRequests(string endpoint)
    {
        // Arrange
        var content = new StringContent("{}", Encoding.UTF8, "application/json");

        // Act
        var response = await _anonymousClient.PutAsync(endpoint, content);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/entries/abc123abc123abc123abc123")]
    [InlineData("/api/v1/treatments/test-id")]
    [InlineData("/api/v1/devicestatus/test-id")]
    [InlineData("/api/v1/food/test-id")]
    [InlineData("/api/v1/activity/test-id")]
    [InlineData("/api/v1/adminnotifies")]
    public async Task V1_DeleteEndpoints_ShouldRejectUnauthenticatedRequests(string endpoint)
    {
        // Arrange & Act
        var response = await _anonymousClient.DeleteAsync(endpoint);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ====================================================================
    // POST/PUT/DELETE with api-secret: must be accepted (Nightscout compat)
    // ====================================================================

    [Theory]
    [InlineData("/api/v1/entries")]
    [InlineData("/api/v1/treatments")]
    [InlineData("/api/v1/devicestatus")]
    [InlineData("/api/v1/food")]
    [InlineData("/api/v1/profile")]
    [InlineData("/api/v1/activity")]
    public async Task V1_PostEndpoints_ShouldAcceptValidApiSecret(string endpoint)
    {
        // Arrange - send minimal valid JSON body
        var content = new StringContent("{}", Encoding.UTF8, "application/json");

        // Act
        var response = await _authenticatedClient.PostAsync(endpoint, content);

        // Assert - should NOT be 401 or 403 (may be 400 due to invalid body, 200, etc.)
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/entries/abc123abc123abc123abc123")]
    [InlineData("/api/v1/treatments/test-id")]
    [InlineData("/api/v1/food/test-id")]
    [InlineData("/api/v1/activity/test-id")]
    public async Task V1_PutEndpoints_ShouldAcceptValidApiSecret(string endpoint)
    {
        // Arrange
        var content = new StringContent("{}", Encoding.UTF8, "application/json");

        // Act
        var response = await _authenticatedClient.PutAsync(endpoint, content);

        // Assert - should NOT be 401 or 403 (may be 400/404 for non-existent records)
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/entries/abc123abc123abc123abc123")]
    [InlineData("/api/v1/treatments/test-id")]
    [InlineData("/api/v1/devicestatus/test-id")]
    [InlineData("/api/v1/food/test-id")]
    [InlineData("/api/v1/activity/test-id")]
    public async Task V1_DeleteEndpoints_ShouldAcceptValidApiSecret(string endpoint)
    {
        // Act
        var response = await _authenticatedClient.DeleteAsync(endpoint);

        // Assert - should NOT be 401 or 403 (may be 404 for non-existent records)
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ====================================================================
    // Webhook endpoint: Pushover callback must be accessible without auth
    // (Pushover sends callbacks to our webhook - it doesn't have our api-secret)
    // ====================================================================

    [Fact]
    public async Task V1_PushoverCallback_ShouldBeAccessibleWithoutAuthentication()
    {
        // Arrange - Pushover sends a JSON callback to this webhook endpoint
        var content = new StringContent(
            """{"receipt":"test-receipt","acknowledged":1,"acknowledged_at":1234567890}""",
            Encoding.UTF8,
            "application/json");

        // Act
        var response = await _anonymousClient.PostAsync("/api/v1/notifications/pushovercallback", content);

        // Assert - webhook should not require authentication
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ====================================================================
    // Bulk delete endpoints with api-secret
    // ====================================================================

    [Theory]
    [InlineData("/api/v1/entries?find[type]=sgv")]
    [InlineData("/api/v1/treatments?find[created_at][$gte]=2024-01-01")]
    [InlineData("/api/v1/devicestatus?find[device]=test")]
    public async Task V1_BulkDeleteEndpoints_ShouldRejectUnauthenticatedRequests(string endpoint)
    {
        // Act
        var response = await _anonymousClient.DeleteAsync(endpoint);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/entries?find[type]=sgv")]
    [InlineData("/api/v1/treatments?find[created_at][$gte]=2024-01-01")]
    [InlineData("/api/v1/devicestatus?find[device]=test")]
    public async Task V1_BulkDeleteEndpoints_ShouldAcceptValidApiSecret(string endpoint)
    {
        // Act
        var response = await _authenticatedClient.DeleteAsync(endpoint);

        // Assert - should NOT be 401 or 403
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ====================================================================
    // Wrong api-secret: must be rejected
    // ====================================================================

    [Fact]
    public async Task V1_PostWithWrongApiSecret_ShouldBeRejected()
    {
        // Arrange
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("api-secret", ComputeSha1Hash("wrong-secret"));

        var content = new StringContent("{}", Encoding.UTF8, "application/json");

        // Act
        var response = await client.PostAsync("/api/v1/entries", content);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static string ComputeSha1Hash(string input)
    {
        var hashBytes = SHA1.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(hashBytes);
    }
}
