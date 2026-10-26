using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// Apple's two endpoints are reached the way Apple reaches them: anonymously, on the apex, as a
/// cross-site POST from appleid.apple.com. The sign-in itself needs Apple; unit tests cover it.
/// </summary>
[Trait("Category", "Integration")]
public class AppleSignInEndpointsTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : ApiIntegrationTestBase(fixture, output)
{
    private HttpClient ApexClient()
    {
        var client = Fixture.CreateHttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Host = AuthTestHelpers.GetBaseDomain(ApiClient);
        client.DefaultRequestHeaders.Add("Origin", "https://appleid.apple.com");
        return client;
    }

    [Fact]
    public async Task FormPostCallback_WithAStateThisInstanceDidNotIssue_RedirectsToTheErrorPage()
    {
        using var client = ApexClient();

        var response = await client.PostAsync("/api/auth/oidc/apple/callback", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["code"] = "c", ["state"] = "forged", ["user"] = "{}" }));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().StartWith("/auth/error?error=invalid_state");
    }

    [Fact]
    public async Task Notification_WithNoAppleProvider_IsRefused()
    {
        using var client = ApexClient();

        var response = await client.PostAsJsonAsync("/api/auth/oidc/apple/notifications", new { payload = "a.b.c" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
