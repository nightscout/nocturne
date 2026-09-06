using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Tandem.Configurations;
using Nocturne.Connectors.Tandem.Services;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

namespace Nocturne.Connectors.Tandem.Tests.Services;

/// <summary>
///     Tandem's OIDC sign-in runs on the shared login retry loop, so a configured
///     <see cref="Nocturne.Connectors.Core.Models.BaseConnectorConfiguration.MaxRetryAttempts"/>
///     buys another attempt for a transport failure — and none at all for a rejected credential.
/// </summary>
public class TandemAuthTokenProviderRetryTests
{
    [Fact]
    public async Task GetValidTokenAsync_RetriesTransportFailure_AndSucceedsOnSecondAttempt()
    {
        var handler = new TandemLoginHandler
        {
            FailFirstLoginWith = new HttpRequestException("connection reset")
        };

        var token = await AuthenticateAsync(handler);

        token.Should().Be("access-token-1");
        handler.LoginCalls.Should().Be(2, "a transport failure is worth exactly one more attempt");
    }

    [Fact]
    public async Task GetValidTokenAsync_DoesNotRetry_WhenTandemRejectsTheCredentialsWith401()
    {
        var handler = new TandemLoginHandler { LoginStatus = HttpStatusCode.Unauthorized };

        var token = await AuthenticateAsync(handler);

        token.Should().BeNull();
        handler.LoginCalls.Should().Be(1, "retrying a rejected credential cannot help and risks lockout");
    }

    /// <summary>
    ///     Tandem's own bad-credentials answer: HTTP 200 carrying a non-SUCCESS status in the body.
    /// </summary>
    [Fact]
    public async Task GetValidTokenAsync_DoesNotRetry_WhenLoginStatusIsNotSuccess()
    {
        var handler = new TandemLoginHandler { LoginBodyStatus = "INVALID_CREDENTIALS" };

        var token = await AuthenticateAsync(handler);

        token.Should().BeNull();
        handler.LoginCalls.Should().Be(1, "a non-SUCCESS login body is a rejected credential");
    }

    /// <summary>
    ///     The configured MaxRetryAttempts is what the login loop spends.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task GetValidTokenAsync_SpendsExactlyMaxRetryAttempts_OnAPersistentRetryableError(
        int maxRetryAttempts)
    {
        var handler = new TandemLoginHandler { LoginStatus = HttpStatusCode.ServiceUnavailable };

        var token = await AuthenticateAsync(handler, maxRetryAttempts);

        token.Should().BeNull();
        handler.LoginCalls.Should().Be(maxRetryAttempts, "the configured attempt budget is what gets spent");
    }

    private static async Task<string?> AuthenticateAsync(
        TandemLoginHandler handler, int maxRetryAttempts = 3)
    {
        using var httpClient = new HttpClient();

        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.Setup(t => t.IsResolved).Returns(true);
        tenantAccessor.Setup(t => t.TenantId).Returns(Guid.NewGuid());

        var retryDelay = new Mock<IRetryDelayStrategy>();
        retryDelay.Setup(r => r.ApplyRetryDelayAsync(It.IsAny<int>())).Returns(Task.CompletedTask);

        using var provider = new FakeTransportTandemAuthTokenProvider(
            handler,
            httpClient,
            new ConnectorTokenCache(),
            new ConnectorServerResolver<TandemConnectorConfiguration>(null, null, null),
            tenantAccessor.Object,
            NullLogger<TandemAuthTokenProvider>.Instance,
            retryDelay.Object);

        var config = new TandemConnectorConfiguration
        {
            Email = "someone@example.com",
            Password = "hunter2",
            Region = "US",
            MaxRetryAttempts = maxRetryAttempts
        };

        return await provider.GetValidTokenAsync(config, CancellationToken.None);
    }

    /// <summary>Runs the real sign-in flow over a fake transport instead of the network.</summary>
    private sealed class FakeTransportTandemAuthTokenProvider(
        HttpMessageHandler transport,
        HttpClient httpClient,
        IConnectorTokenCache tokenCache,
        IConnectorServerResolver<TandemConnectorConfiguration> serverResolver,
        ITenantAccessor tenantAccessor,
        ILogger<TandemAuthTokenProvider> logger,
        IRetryDelayStrategy retryDelayStrategy)
        : TandemAuthTokenProvider(httpClient, tokenCache, serverResolver, tenantAccessor, logger, retryDelayStrategy)
    {
        // disposeHandler: false — the sign-in disposes its client per attempt, and the handler has to
        // outlive that to answer (and count) the next one.
        protected override HttpClient CreateLoginClient() => new(transport, disposeHandler: false);
    }

    /// <summary>Answers the four requests of a Tandem OIDC login, with per-test failure injection.</summary>
    private sealed class TandemLoginHandler : HttpMessageHandler
    {
        private static readonly TandemConstants.RegionUrls Region = TandemConstants.Us;

        public HttpRequestException? FailFirstLoginWith { get; init; }
        public HttpStatusCode LoginStatus { get; init; } = HttpStatusCode.OK;
        public string LoginBodyStatus { get; init; } = "SUCCESS";

        public int LoginCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();

            if (url == TandemConstants.LoginPageUrl)
                return Task.FromResult(Ok(string.Empty));

            if (url == Region.LoginApiUrl)
            {
                LoginCalls++;
                if (FailFirstLoginWith != null && LoginCalls == 1)
                    throw FailFirstLoginWith;

                if (LoginStatus != HttpStatusCode.OK)
                    return Task.FromResult(new HttpResponseMessage(LoginStatus)
                    {
                        Content = new StringContent("{\"message\":\"unauthorized\"}")
                    });

                return Task.FromResult(Ok($"{{\"status\":\"{LoginBodyStatus}\"}}"));
            }

            if (url.StartsWith(Region.AuthorizationEndpoint, StringComparison.Ordinal))
            {
                var response = Ok(string.Empty);
                // The authorization code is read off the URL the redirect chain landed on.
                response.RequestMessage = new HttpRequestMessage(
                    HttpMethod.Get, $"{Region.RedirectUri}?code=authorization-code-1");
                return Task.FromResult(response);
            }

            if (url == Region.TokenEndpoint)
                return Task.FromResult(Ok(
                    "{\"access_token\":\"access-token-1\"," +
                    $"\"id_token\":\"{IdToken()}\"," +
                    "\"expires_in\":3600}"));

            throw new InvalidOperationException($"Unexpected Tandem request: {url}");
        }

        private static HttpResponseMessage Ok(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body) };

        private static string IdToken() =>
            $"{Base64Url("{\"alg\":\"RS256\"}")}.{Base64Url("{\"pumperId\":\"pumper-1\",\"accountId\":\"account-1\"}")}.signature";

        private static string Base64Url(string json) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
