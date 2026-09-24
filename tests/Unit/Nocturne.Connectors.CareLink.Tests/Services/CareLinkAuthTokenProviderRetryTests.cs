using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.CareLink.Configurations;
using Nocturne.Connectors.CareLink.Services;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

namespace Nocturne.Connectors.CareLink.Tests.Services;

/// <summary>
///     CareLink's credential login runs on the shared login retry loop, so a configured
///     <see cref="Nocturne.Connectors.Core.Models.BaseConnectorConfiguration.MaxRetryAttempts"/>
///     buys another attempt for a transport failure — and none at all for a rejected credential.
///     Auth0 refuses with 200 and an error page, so the refusal is counted at the form POST.
/// </summary>
public class CareLinkAuthTokenProviderRetryTests
{
    [Fact]
    public async Task GetValidTokenAsync_DoesNotRetry_WhenAuth0SaysWrongUsernameOrPassword()
    {
        var handler = new CareLinkLoginHandler { LoginPageBody = "Wrong username or password" };

        var token = await AuthenticateAsync(handler);

        token.Should().BeNull();
        handler.LoginPosts.Should().Be(1, "retrying a rejected credential cannot help and risks lockout");
    }

    [Fact]
    public async Task GetValidTokenAsync_DoesNotRetry_WhenAuth0DemandsACaptcha()
    {
        var handler = new CareLinkLoginHandler { LoginPageBody = "please complete the captcha" };

        var token = await AuthenticateAsync(handler);

        token.Should().BeNull();
        handler.LoginPosts.Should().Be(1, "a CAPTCHA cannot be cleared by repeating the request");
    }

    /// <summary>
    ///     A transport failure carries no answer from Auth0, so it is the one login failure another
    ///     attempt can change.
    /// </summary>
    [Fact]
    public async Task GetValidTokenAsync_RetriesTransportFailure()
    {
        var handler = new CareLinkLoginHandler
        {
            LoginPageBody = "Wrong username or password",
            FailFirstLoginPostWith = new HttpRequestException("connection reset")
        };

        var token = await AuthenticateAsync(handler);

        token.Should().BeNull();
        handler.LoginPosts.Should().Be(2, "a transport failure is worth exactly one more attempt");
    }

    private static async Task<string?> AuthenticateAsync(
        CareLinkLoginHandler handler, int maxRetryAttempts = 3)
    {
        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.Setup(t => t.IsResolved).Returns(true);
        tenantAccessor.Setup(t => t.TenantId).Returns(Guid.NewGuid());

        var retryDelay = new Mock<IRetryDelayStrategy>();
        retryDelay.Setup(r => r.ApplyRetryDelayAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        using var provider = new TestableProvider(
            new HttpClient(handler),
            new ConnectorTokenCache(),
            new ConnectorServerResolver<CareLinkConnectorConfiguration>(null, null, CareLinkConstants.Servers.Eu),
            tenantAccessor.Object,
            NullLogger<CareLinkAuthTokenProvider>.Instance,
            retryDelay.Object,
            handler);

        return await provider.GetValidTokenAsync(
            new CareLinkConnectorConfiguration
            {
                Username = "user@example.com",
                Password = "hunter2",
                Server = "EU",
                MaxRetryAttempts = maxRetryAttempts
            },
            CancellationToken.None);
    }

    /// <summary>Routes the provider's own auth-flow requests through the test handler.</summary>
    private sealed class TestableProvider(
        HttpClient httpClient,
        IConnectorTokenCache tokenCache,
        IConnectorServerResolver<CareLinkConnectorConfiguration> serverResolver,
        ITenantAccessor tenantAccessor,
        ILogger<CareLinkAuthTokenProvider> logger,
        IRetryDelayStrategy retryDelayStrategy,
        HttpMessageHandler handler)
        : CareLinkAuthTokenProvider(httpClient, tokenCache, serverResolver, tenantAccessor, logger, retryDelayStrategy)
    {
        protected override CareLinkAuthFlowService CreateAuthFlow() => new(NullLogger.Instance, handler);
    }

    /// <summary>
    ///     Carries the Auth0 PKCE flow as far as the credential POST, whose answer each test chooses.
    /// </summary>
    private sealed class CareLinkLoginHandler : HttpMessageHandler
    {
        private const string LoginHost = "carelink-login.example";
        private const string SsoConfigUrl = $"https://{LoginHost}/configs/carepartner_auth0_sso_config.json";
        private const string FormActionUrl = $"https://{LoginHost}/u/login";

        public HttpRequestException? FailFirstLoginPostWith { get; init; }
        public required string LoginPageBody { get; init; }

        public int LoginPosts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();

            if (url.Contains("/discover/", StringComparison.Ordinal))
                return Task.FromResult(Html($$"""
                    {"CP":[{"region":"EU","Auth0SSOConfiguration":"{{SsoConfigUrl}}"}]}
                    """));

            if (url == SsoConfigUrl)
                return Task.FromResult(Html($$"""
                    {
                      "server": { "hostname": "{{LoginHost}}", "port": 443, "prefix": "" },
                      "client": {
                        "client_id": "client-1",
                        "scope": "profile openid offline_access",
                        "audience": "carepartner.patient.ous",
                        "redirect_uri": "com.medtronic.carepartner:/sso"
                      },
                      "system_endpoints": {
                        "authorization_endpoint_path": "/authorize",
                        "token_endpoint_path": "/oauth/token"
                      }
                    }
                    """));

            if (url.StartsWith($"https://{LoginHost}/authorize", StringComparison.Ordinal))
                return Task.FromResult(Html(
                    $"<html><form action=\"{FormActionUrl}\" method=\"post\">"
                    + "<input type=\"hidden\" name=\"state\" value=\"state-1\" /></form></html>"));

            if (url == FormActionUrl)
            {
                LoginPosts++;
                if (FailFirstLoginPostWith != null && LoginPosts == 1)
                    throw FailFirstLoginPostWith;

                return Task.FromResult(Html(LoginPageBody));
            }

            throw new InvalidOperationException($"Unexpected CareLink request: {url}");
        }

        private static HttpResponseMessage Html(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") };
    }
}
