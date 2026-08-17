using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.CareLink.Configurations;
using Nocturne.Connectors.CareLink.Services;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

namespace Nocturne.Connectors.CareLink.Tests.Services;

public class CareLinkConnectorServiceTests
{
    /// <summary>
    /// Authentication succeeds but every data endpoint fails. A working CareLink account always
    /// returns a payload — even with no current readings — so no payload at all means the fetch
    /// failed, and the sync must say so. Reporting success marked the connector healthy while
    /// nothing reached the tenant, which is how a totally broken connector went unnoticed.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenEveryDataEndpointFails_ReportsFailureNotSuccess()
    {
        var handler = new CareLinkFakeHandler();
        var fixture = new ServiceFixture(handler);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeFalse(
            "a sync that obtained no data from any endpoint has not succeeded");
        result.Errors.Should().ContainMatch("*No data returned from any CareLink endpoint*");
    }

    /// <summary>
    /// A token CareLink has rejected must not stay cached until its nominal expiry, or every sync
    /// until then fails. Only a 401 drops it: the other failures a data endpoint returns say
    /// nothing about the token.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    public async Task SyncDataAsync_DropsTheCachedToken_OnlyWhenCareLinkRejectsIt(
        HttpStatusCode dataEndpointStatus, bool expectTokenDropped)
    {
        var handler = new CareLinkFakeHandler { UnmodelledStatus = dataEndpointStatus };
        var fixture = new ServiceFixture(handler);

        await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] }, fixture.Config, CancellationToken.None);

        var session = await fixture.TokenProvider.GetCachedSessionAsync();
        (session == null).Should().Be(expectTokenDropped);
    }

    /// <summary>Wires the connector service and a real token provider onto one fake handler.</summary>
    private sealed class ServiceFixture
    {
        internal CareLinkConnectorService Service { get; }
        internal CareLinkAuthTokenProvider TokenProvider { get; }
        internal CareLinkConnectorConfiguration Config { get; } = new()
        {
            Username = "user@example.com",
            Server = "EU",
        };

        internal ServiceFixture(CareLinkFakeHandler handler)
        {
            var tenantAccessor = new Mock<ITenantAccessor>();
            tenantAccessor.Setup(t => t.IsResolved).Returns(true);
            tenantAccessor.Setup(t => t.TenantId).Returns(Guid.NewGuid());

            var configService = new Mock<IConnectorConfigurationService>();
            configService
                .Setup(s => s.GetSecretsAsync("CareLink", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Dictionary<string, string> { ["refresh_token"] = "stored-refresh-token" });

            var serverResolver = new ConnectorServerResolver<CareLinkConnectorConfiguration>(
                null, null, CareLinkConstants.Servers.Eu);

            TokenProvider = new HandlerBackedTokenProvider(
                new HttpClient(handler),
                new ConnectorTokenCache(),
                serverResolver,
                tenantAccessor.Object,
                NullLogger<CareLinkAuthTokenProvider>.Instance,
                Mock.Of<IRetryDelayStrategy>(),
                handler);

            Service = new CareLinkConnectorService(
                new HttpClient(handler),
                serverResolver,
                TokenProvider,
                configService.Object,
                NullLogger<CareLinkConnectorService>.Instance);
        }
    }

    private sealed class HandlerBackedTokenProvider(
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
