using System.Net;
using System.Text;
using System.Text.Json;
using Nocturne.Connectors.GoogleHealth.Services;
using Xunit;

namespace Nocturne.Connectors.GoogleHealth.Tests.Services;

public class GoogleHealthOAuthClientTests
{
    [Theory]
    [InlineData("invalid_client", "invalid_client_credentials")]
    [InlineData("redirect_uri_mismatch", "invalid_callback")]
    [InlineData("invalid_scope", "oauth_scope_configuration")]
    [InlineData("invalid_grant", "expired_signin")]
    public async Task Maps_exchange_errors_to_actionable_codes(string providerError, string expected)
    {
        var client = CreateClient(JsonSerializer.Serialize(new
        {
            error = providerError,
            error_description = "do not expose"
        }));

        var exception = await Assert.ThrowsAsync<GoogleHealthException>(
            () => client.ExchangeAuthorizationCodeAsync([], default));

        Assert.Equal(expected, exception.Message);
        Assert.Equal("authorization_code", exception.Stage);
        Assert.DoesNotContain("expose", exception.Message);
    }

    [Fact]
    public async Task Invalid_refresh_grant_requires_reconnection()
    {
        var client = CreateClient("{\"error\":\"invalid_grant\"}");

        var exception = await Assert.ThrowsAsync<GoogleHealthException>(
            () => client.RefreshAccessTokenAsync([], default));

        Assert.Equal("reconnect_required", exception.Message);
        Assert.Equal("token_refresh", exception.Stage);
    }

    public static IEnumerable<object[]> MalformedSuccessResponses() =>
        from stage in new[] { "authorization_code", "token_refresh", "account_identity" }
        from body in new[] { "{sensitive-provider-text", "null", "[]", "42", "true", "\"secret\"" }
        select new object[] { stage, body };

    [Theory]
    [MemberData(nameof(MalformedSuccessResponses))]
    public async Task Malformed_success_responses_return_safe_domain_errors(string stage, string body)
    {
        var client = CreateClient(body, HttpStatusCode.OK);
        var exception = await Assert.ThrowsAsync<GoogleHealthException>(async () =>
        {
            if (stage == "authorization_code") await client.ExchangeAuthorizationCodeAsync([], default);
            else if (stage == "token_refresh") await client.RefreshAccessTokenAsync([], default);
            else await client.AccountKeyAsync("access-token", default);
        });

        Assert.Equal("invalid_token_response", exception.Message);
        Assert.Equal(stage, exception.Stage);
        Assert.Null(exception.InnerException);
    }

    private static GoogleHealthOAuthClient CreateClient(string responseBody, HttpStatusCode status = HttpStatusCode.BadRequest) =>
        new(new HttpClient(new StubHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
        })));

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responder(request));
    }
}
