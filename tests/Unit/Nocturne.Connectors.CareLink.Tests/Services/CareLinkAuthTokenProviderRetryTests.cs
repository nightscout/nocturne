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

        var run = await AuthenticateAsync(handler);

        run.Token.Should().BeNull();
        handler.LoginPosts.Should().Be(1, "retrying a rejected credential cannot help and risks lockout");
    }

    [Fact]
    public async Task GetValidTokenAsync_DoesNotRetry_WhenAuth0DemandsACaptcha()
    {
        var handler = new CareLinkLoginHandler { LoginPageBody = "please complete the captcha" };

        var run = await AuthenticateAsync(handler);

        run.Token.Should().BeNull();
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

        var run = await AuthenticateAsync(handler);

        run.Token.Should().BeNull();
        handler.LoginPosts.Should().Be(2, "a transport failure is worth exactly one more attempt");
    }

    /// <summary>
    ///     A 503 from the token endpoint is the endpoint being unwell, not the refresh token being
    ///     rejected, so the refresh is retried on the login's own loop and delay strategy. Replaying
    ///     the member's password during a transient outage risks a CAPTCHA and lockout, so it must
    ///     never happen.
    /// </summary>
    [Fact]
    public async Task GetValidTokenAsync_RetriesTransientRefresh_AndNeverPostsCredentials()
    {
        var handler = new CareLinkLoginHandler
        {
            LoginPageBody = "Wrong username or password",
            TokenEndpointStatus = HttpStatusCode.ServiceUnavailable,
        };

        var run = await AuthenticateAsync(handler, refreshToken: "seed-refresh-token");

        run.Token.Should().BeNull();
        handler.TokenPosts.Should().Be(3, "a transient failure spends the same bounded budget as the login");
        handler.LoginPosts.Should().Be(0, "a transient refresh failure must not replay the password");
    }

    /// <summary>
    ///     A rejected refresh token is the token endpoint's verdict on the token itself, the one
    ///     failure a credential login can replace, so it is not retried and falls through exactly once.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task GetValidTokenAsync_FallsBackToCredentialLogin_WhenRefreshTokenIsRejected(HttpStatusCode status)
    {
        var handler = new CareLinkLoginHandler
        {
            LoginPageBody = "Wrong username or password",
            TokenEndpointStatus = status,
            TokenEndpointBody = """{"error":"invalid_grant"}""",
        };

        var run = await AuthenticateAsync(handler, refreshToken: "seed-refresh-token");

        run.Token.Should().BeNull();
        handler.TokenPosts.Should().Be(1, "a rejected refresh token is not retried");
        handler.LoginPosts.Should().Be(1, "only a real rejection falls through to one credential login");
    }

    /// <summary>
    ///     CloudFront's WAF answers the authorize endpoint with a 403 and a page carrying no login
    ///     form. Rebuilding the login cannot clear it, and a WAF block says nothing about the member's
    ///     credentials, so one attempt must end as unavailable, never as a wrong password.
    /// </summary>
    [Fact]
    public async Task GetValidTokenAsync_DoesNotRetryOrBlameCredentials_WhenAuthorizeIsWafBlocked()
    {
        var handler = new CareLinkLoginHandler
        {
            LoginPageBody = "Wrong username or password",
            AuthorizeStatus = HttpStatusCode.Forbidden,
        };

        var run = await AuthenticateAsync(handler);

        run.Token.Should().BeNull();
        handler.Discoveries.Should().Be(1, "a WAF block cannot be cleared by rebuilding the login");
        var failure = run.Cache.GetSignInFailure("CareLink", run.TenantId);
        failure.Should().Contain("Could not sign in to");
        failure.Should().NotContain("did not accept this sign-in");
    }

    /// <summary>
    ///     A HttpClient timeout is a transient failure, but a cancellation the caller asked for must
    ///     travel, not be retried. The refresh path distinguishes them by the token's own state.
    /// </summary>
    [Fact]
    public async Task GetValidTokenAsync_PropagatesCallerCancellationDuringRefresh()
    {
        using var cts = new CancellationTokenSource();
        var handler = new CareLinkLoginHandler
        {
            LoginPageBody = "Wrong username or password",
            CancelOnTokenRequest = cts,
        };

        var act = async () => await AuthenticateAsync(handler, refreshToken: "seed-refresh-token", cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        handler.TokenPosts.Should().Be(1);
    }

    private sealed record AuthRun(string? Token, ConnectorTokenCache Cache, Guid TenantId);

    private static async Task<AuthRun> AuthenticateAsync(
        CareLinkLoginHandler handler, int maxRetryAttempts = 3, string? refreshToken = null,
        CancellationToken cancellationToken = default)
    {
        var tenantId = Guid.NewGuid();
        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.Setup(t => t.IsResolved).Returns(true);
        tenantAccessor.Setup(t => t.TenantId).Returns(tenantId);

        var retryDelay = new Mock<IRetryDelayStrategy>();
        retryDelay.Setup(r => r.ApplyRetryDelayAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var cache = new ConnectorTokenCache();
        using var provider = new TestableProvider(
            new HttpClient(handler),
            cache,
            new ConnectorServerResolver<CareLinkConnectorConfiguration>(null, null, CareLinkConstants.Servers.Eu),
            tenantAccessor.Object,
            NullLogger<CareLinkAuthTokenProvider>.Instance,
            retryDelay.Object,
            handler);

        if (refreshToken != null)
            provider.InitializeFromSecrets(
                refreshToken, CareLinkLoginHandler.ClientId, CareLinkLoginHandler.TokenUrl, audience: null);

        var token = await provider.GetValidTokenAsync(
            new CareLinkConnectorConfiguration
            {
                Username = "user@example.com",
                Password = "hunter2",
                Server = "EU",
                MaxRetryAttempts = maxRetryAttempts
            },
            cancellationToken);

        return new AuthRun(token, cache, tenantId);
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
    ///     Carries the Auth0 PKCE flow as far as the token and credential endpoints, whose answers
    ///     each test chooses.
    /// </summary>
    private sealed class CareLinkLoginHandler : HttpMessageHandler
    {
        private const string LoginHost = "carelink-login.example";
        private const string SsoConfigUrl = $"https://{LoginHost}/configs/carepartner_auth0_sso_config.json";
        private const string FormActionUrl = $"https://{LoginHost}/u/login";

        internal const string ClientId = "client-1";
        internal const string TokenUrl = $"https://{LoginHost}/oauth/token";

        public HttpRequestException? FailFirstLoginPostWith { get; init; }
        public required string LoginPageBody { get; init; }
        public HttpStatusCode AuthorizeStatus { get; init; } = HttpStatusCode.OK;
        public HttpStatusCode TokenEndpointStatus { get; init; } = HttpStatusCode.OK;
        public string TokenEndpointBody { get; init; } = """{"access_token":"refreshed-access-token","refresh_token":"rotated"}""";

        /// <summary>When set, the token-endpoint request cancels it and then throws, as a withdrawn run does.</summary>
        public CancellationTokenSource? CancelOnTokenRequest { get; init; }

        public int LoginPosts { get; private set; }
        public int TokenPosts { get; private set; }
        public int Discoveries { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();

            if (url.Contains("/discover/", StringComparison.Ordinal))
            {
                Discoveries++;
                return Task.FromResult(Html($$"""
                    {"CP":[{"region":"EU","Auth0SSOConfiguration":"{{SsoConfigUrl}}"}]}
                    """));
            }

            if (url == SsoConfigUrl)
                return Task.FromResult(Html($$"""
                    {
                      "server": { "hostname": "{{LoginHost}}", "port": 443, "prefix": "" },
                      "client": {
                        "client_id": "{{ClientId}}",
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
                return Task.FromResult(AuthorizeStatus == HttpStatusCode.OK
                    ? Html($"<html><form action=\"{FormActionUrl}\" method=\"post\">"
                           + "<input type=\"hidden\" name=\"state\" value=\"state-1\" /></form></html>")
                    : Html("Request blocked", AuthorizeStatus));

            if (url == TokenUrl)
            {
                TokenPosts++;
                if (CancelOnTokenRequest != null)
                {
                    CancelOnTokenRequest.Cancel();
                    throw new OperationCanceledException(CancelOnTokenRequest.Token);
                }

                return Task.FromResult(Html(TokenEndpointBody, TokenEndpointStatus));
            }

            if (url == FormActionUrl)
            {
                LoginPosts++;
                if (FailFirstLoginPostWith != null && LoginPosts == 1)
                    throw FailFirstLoginPostWith;

                return Task.FromResult(Html(LoginPageBody));
            }

            throw new InvalidOperationException($"Unexpected CareLink request: {url}");
        }

        private static HttpResponseMessage Html(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "text/html") };
    }
}
