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

public class CareLinkAuthTokenProviderTests
{
    /// <summary>
    /// A refresh token minted outside the connect flow (by an external tool, then pasted into the
    /// connector settings) arrives on its own — the settings form has no client-id or token-URL
    /// field, and both are public values in CareLink's discovery config. The provider must resolve
    /// them and redeem the token, rather than silently skipping the refresh and demanding a password.
    /// </summary>
    [Fact]
    public async Task AcquireToken_RefreshesWithOnlyARefreshToken_ResolvingClientIdFromDiscovery()
    {
        var handler = new CareLinkFakeHandler();
        var provider = CreateProvider(handler);

        provider.InitializeFromSecrets("pasted-refresh-token", clientId: null, tokenUrl: null, audience: null);

        var token = await provider.GetValidTokenAsync(
            new CareLinkConnectorConfiguration { Username = "user@example.com", Server = "EU" },
            CancellationToken.None);

        token.Should().Be("new-access-token");

        var refreshPost = handler.Requests.Should().ContainSingle(r =>
            r.Method == HttpMethod.Post && r.Url == CareLinkFakeHandler.TokenUrl).Subject;
        refreshPost.Body.Should().Contain("grant_type=refresh_token")
            .And.Contain("refresh_token=pasted-refresh-token")
            .And.Contain($"client_id={CareLinkFakeHandler.ClientId}");

        // The resolved parameters must be cached with the session so the next refresh needs no
        // second discovery round trip, and so the connector service can persist them.
        var session = await provider.GetCachedSessionAsync();
        session!.Metadata!["ClientId"].Should().Be(CareLinkFakeHandler.ClientId);
        session.Metadata["TokenUrl"].Should().Be(CareLinkFakeHandler.TokenUrl);
        session.Metadata["RefreshToken"].Should().Be("rotated-refresh-token");
    }

    /// <summary>
    /// With neither a refresh token nor a password there is nothing to authenticate with, so the
    /// provider must fail without reaching for discovery.
    /// </summary>
    [Fact]
    public async Task AcquireToken_WithNoCredentials_FailsWithoutCallingCareLink()
    {
        var handler = new CareLinkFakeHandler();
        var provider = CreateProvider(handler);

        var token = await provider.GetValidTokenAsync(
            new CareLinkConnectorConfiguration { Username = "user@example.com", Server = "EU" },
            CancellationToken.None);

        token.Should().BeNull();
        handler.Requests.Should().BeEmpty();
    }

    private static TestableProvider CreateProvider(CareLinkFakeHandler handler)
    {
        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.Setup(t => t.IsResolved).Returns(true);
        tenantAccessor.Setup(t => t.TenantId).Returns(Guid.NewGuid());

        var retryDelay = new Mock<IRetryDelayStrategy>();
        retryDelay.Setup(r => r.ApplyRetryDelayAsync(It.IsAny<int>())).Returns(Task.CompletedTask);

        return new TestableProvider(
            new HttpClient(handler),
            new ConnectorTokenCache(),
            new ConnectorServerResolver<CareLinkConnectorConfiguration>(null, null, CareLinkConstants.Servers.Eu),
            tenantAccessor.Object,
            NullLogger<CareLinkAuthTokenProvider>.Instance,
            retryDelay.Object,
            handler);
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
}
