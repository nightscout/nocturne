using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Tandem.Configurations;
using Nocturne.Connectors.Tandem.Services;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;
using Nocturne.Tests.Shared.Connectors;

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

    /// <summary>
    ///     The sign-in walks a redirect chain on a cookie jar of its own, so it must not move onto
    ///     the installed client and its retry handler: that would give one sign-in two retry
    ///     mechanisms. The test pins that no sign-in request is sent on the installed client.
    /// </summary>
    [Fact]
    public async Task GetValidTokenAsync_NeverSendsTheSignInOnTheInstalledClient()
    {
        var installed = new CountingHandler();
        await using var services = BuildInstalledServices(installed);
        using var installedClient = services.GetRequiredService<IHttpClientFactory>()
            .CreateClient(nameof(TandemAuthTokenProvider));
        var handler = new TandemLoginHandler { LoginStatus = HttpStatusCode.ServiceUnavailable };
        using var provider = ProviderFor(handler, installedClient, TimeProvider.System);

        var token = await provider.GetValidTokenAsync(SignInConfig(3), CancellationToken.None);

        token.Should().BeNull();
        handler.LoginCalls.Should().Be(3, "the loop spends the whole attempt budget on a client with no handler");
        installed.Requests.Should().Be(0);
    }

    private static ServiceProvider BuildInstalledServices(CountingHandler installed)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<IRetryDelayStrategy>());
        services.AddSingleton(ResolvedTenant.Accessor());
        services.AddSingleton(new Mock<IConnectorConfigurationService>(MockBehavior.Strict).Object);

        new TandemConnectorInstaller().Install(
            services,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Connectors:Tandem:Enabled"] = "true" })
                .Build());

        services.AddHttpClient(nameof(TandemAuthTokenProvider))
            .ConfigurePrimaryHttpMessageHandler(() => installed);

        return services.BuildServiceProvider();
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private int _requests;

        public int Requests => _requests;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }

        protected override void Dispose(bool disposing)
        {
        }
    }

    [Fact]
    public async Task Token_ExpiresFiveMinutesBeforeItsLifetimeEnds()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        using var provider = ProviderFor(new TandemLoginHandler(), new HttpClient(), clock);

        var token = await provider.GetValidTokenAsync(SignInConfig(3), CancellationToken.None);

        token.Should().Be("access-token-1");
        provider.TokenExpiresAt.Should().Be(clock.GetUtcNow().UtcDateTime.AddMinutes(55));
        clock.Advance(TimeSpan.FromMinutes(55) - TimeSpan.FromSeconds(1));
        provider.IsTokenExpired.Should().BeFalse();
        clock.Advance(TimeSpan.FromSeconds(2));
        provider.IsTokenExpired.Should().BeTrue();
    }

    /// <summary>
    ///     A sync that could not sign in tells the tenant what the provider learned about why, rather
    ///     than a bare "Authentication failed".
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Sync_LoginRefused_ReportsTheProvidersSignInReason(HttpStatusCode refusal)
    {
        var result = await SyncAsync(new TandemLoginHandler { LoginStatus = refusal });

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("did not accept this sign-in");
        result.Errors.Should().ContainSingle().Which.Should().Contain("did not accept this sign-in");
    }

    [Fact]
    public async Task Sync_LoginBodyIsNotSuccess_ReportsTheProvidersSignInReason()
    {
        var result = await SyncAsync(new TandemLoginHandler { LoginBodyStatus = "INVALID_CREDENTIALS" });

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("did not accept this sign-in");
        result.Errors.Should().ContainSingle().Which.Should().Contain("did not accept this sign-in");
    }

    [Fact]
    public async Task Sync_LoginBodyHasNoStatus_ReportsATemporaryFailure()
    {
        var result = await SyncAsync(new TandemLoginHandler { LoginBodyStatus = null });

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("usually temporary").And.NotContain("did not accept this sign-in");
    }

    [Fact]
    public async Task Sync_LoginUnavailable_ReportsATemporaryFailure()
    {
        var result = await SyncAsync(new TandemLoginHandler { LoginStatus = HttpStatusCode.ServiceUnavailable });

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("usually temporary").And.NotContain("did not accept this sign-in");
    }

    private static async Task<SyncResult> SyncAsync(TandemLoginHandler handler)
    {
        using var httpClient = new HttpClient();
        using var provider = ProviderFor(handler, httpClient, TimeProvider.System);
        var service = new TandemConnectorService(
            new HttpClient(handler, disposeHandler: false),
            new ConnectorServerResolver<TandemConnectorConfiguration>(null, null, null),
            NullLogger<TandemConnectorService>.Instance,
            Mock.Of<IRetryDelayStrategy>(),
            provider);

        return await service.SyncDataAsync(new SyncRequest(), SignInConfig(3), CancellationToken.None);
    }

    private static async Task<string?> AuthenticateAsync(
        TandemLoginHandler handler, int maxRetryAttempts = 3)
    {
        using var httpClient = new HttpClient();
        using var provider = ProviderFor(handler, httpClient, TimeProvider.System);

        return await provider.GetValidTokenAsync(SignInConfig(maxRetryAttempts), CancellationToken.None);
    }

    private static TandemAuthTokenProvider ProviderFor(
        TandemLoginHandler handler, HttpClient httpClient, TimeProvider clock)
    {
        var tenantAccessor = ResolvedTenant.Accessor();

        var retryDelay = new Mock<IRetryDelayStrategy>();
        retryDelay.Setup(r => r.ApplyRetryDelayAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        return new FakeTransportTandemAuthTokenProvider(
            handler,
            httpClient,
            new ConnectorTokenCache(clock),
            new ConnectorServerResolver<TandemConnectorConfiguration>(null, null, null),
            tenantAccessor,
            NullLogger<TandemAuthTokenProvider>.Instance,
            retryDelay.Object,
            clock);
    }

    private static TandemConnectorConfiguration SignInConfig(int maxRetryAttempts) => new()
    {
        Email = "someone@example.com",
        Password = "hunter2",
        Region = "US",
        MaxRetryAttempts = maxRetryAttempts
    };

    /// <summary>Runs the real sign-in flow over a fake transport instead of the network.</summary>
    private sealed class FakeTransportTandemAuthTokenProvider(
        HttpMessageHandler transport,
        HttpClient httpClient,
        IConnectorTokenCache tokenCache,
        IConnectorServerResolver<TandemConnectorConfiguration> serverResolver,
        ITenantAccessor tenantAccessor,
        ILogger<TandemAuthTokenProvider> logger,
        IRetryDelayStrategy retryDelayStrategy,
        TimeProvider timeProvider)
        : TandemAuthTokenProvider(httpClient, tokenCache, serverResolver, tenantAccessor, logger, retryDelayStrategy, timeProvider)
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
        public string? LoginBodyStatus { get; init; } = "SUCCESS";

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

                return Task.FromResult(Ok(LoginBodyStatus is null ? "{}" : $"{{\"status\":\"{LoginBodyStatus}\"}}"));
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
