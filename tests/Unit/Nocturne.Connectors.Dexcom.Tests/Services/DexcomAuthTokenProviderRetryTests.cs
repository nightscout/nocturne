using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Dexcom.Configurations;
using Nocturne.Connectors.Dexcom.Services;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

namespace Nocturne.Connectors.Dexcom.Tests.Services;

/// <summary>
///     Dexcom Share's two-step login runs on the shared login retry loop, so a configured
///     <see cref="Nocturne.Connectors.Core.Models.BaseConnectorConfiguration.MaxRetryAttempts"/>
///     buys another attempt for a transport failure — and none at all for a rejected credential.
/// </summary>
public class DexcomAuthTokenProviderRetryTests
{
    [Fact]
    public async Task GetValidTokenAsync_RetriesTransportFailure_AndSucceedsOnSecondAttempt()
    {
        var handler = new DexcomLoginHandler
        {
            FailFirstAuthenticateWith = new HttpRequestException("connection reset")
        };

        var token = await AuthenticateAsync(handler);

        token.Should().Be("session-id-1");
        handler.AuthenticateCalls.Should().Be(2, "a transport failure is worth exactly one more attempt");
    }

    [Fact]
    public async Task GetValidTokenAsync_DoesNotRetry_WhenDexcomRejectsTheCredentialsWith401()
    {
        var handler = new DexcomLoginHandler { AuthenticateStatus = HttpStatusCode.Unauthorized };

        var token = await AuthenticateAsync(handler);

        token.Should().BeNull();
        handler.AuthenticateCalls.Should().Be(1, "retrying a rejected credential cannot help and risks lockout");
    }

    /// <summary>
    ///     Dexcom rejects a bad account name on the first call and a bad password on the second, so
    ///     the session step needs its own guard.
    /// </summary>
    [Fact]
    public async Task GetValidTokenAsync_DoesNotRetry_WhenTheSessionStepIsRejectedWith401()
    {
        var handler = new DexcomLoginHandler { LoginStatus = HttpStatusCode.Unauthorized };

        var token = await AuthenticateAsync(handler);

        token.Should().BeNull();
        handler.LoginCalls.Should().Be(1, "a rejected session request is a rejected credential");
        handler.AuthenticateCalls.Should().Be(1);
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
        var handler = new DexcomLoginHandler { AuthenticateStatus = HttpStatusCode.ServiceUnavailable };

        var token = await AuthenticateAsync(handler, maxRetryAttempts);

        token.Should().BeNull();
        handler.AuthenticateCalls.Should().Be(maxRetryAttempts, "the configured attempt budget is what gets spent");
    }

    private static async Task<string?> AuthenticateAsync(
        DexcomLoginHandler handler, int maxRetryAttempts = 3)
    {
        using var httpClient = new HttpClient(handler);

        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.Setup(t => t.IsResolved).Returns(true);
        tenantAccessor.Setup(t => t.TenantId).Returns(Guid.NewGuid());

        var retryDelay = new Mock<IRetryDelayStrategy>();
        retryDelay.Setup(r => r.ApplyRetryDelayAsync(It.IsAny<int>())).Returns(Task.CompletedTask);

        using var provider = new DexcomAuthTokenProvider(
            httpClient,
            new ConnectorTokenCache(),
            new ConnectorServerResolver<DexcomConnectorConfiguration>(
                new Dictionary<string, string> { ["US"] = DexcomConstants.Servers.Us },
                config => ((DexcomConnectorConfiguration)config).Server,
                null),
            tenantAccessor.Object,
            NullLogger<DexcomAuthTokenProvider>.Instance,
            retryDelay.Object);

        var config = new DexcomConnectorConfiguration
        {
            Username = "someone@example.com",
            Password = "hunter2",
            Server = "US",
            MaxRetryAttempts = maxRetryAttempts
        };

        return await provider.GetValidTokenAsync(config, CancellationToken.None);
    }

    /// <summary>Answers the two requests of a Dexcom Share login, with per-test failure injection.</summary>
    private sealed class DexcomLoginHandler : HttpMessageHandler
    {
        public HttpRequestException? FailFirstAuthenticateWith { get; init; }
        public HttpStatusCode AuthenticateStatus { get; init; } = HttpStatusCode.OK;
        public HttpStatusCode LoginStatus { get; init; } = HttpStatusCode.OK;

        public int AuthenticateCalls { get; private set; }
        public int LoginCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();

            if (url.EndsWith("AuthenticatePublisherAccount", StringComparison.Ordinal))
            {
                AuthenticateCalls++;
                if (FailFirstAuthenticateWith != null && AuthenticateCalls == 1)
                    throw FailFirstAuthenticateWith;

                return Task.FromResult(Respond(AuthenticateStatus, "\"account-id-1\""));
            }

            if (url.EndsWith("LoginPublisherAccountById", StringComparison.Ordinal))
            {
                LoginCalls++;
                return Task.FromResult(Respond(LoginStatus, "\"session-id-1\""));
            }

            throw new InvalidOperationException($"Unexpected Dexcom request: {url}");
        }

        private static HttpResponseMessage Respond(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(status == HttpStatusCode.OK ? body : "\"error\"") };
    }
}
