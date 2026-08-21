using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Tidepool.Configurations;
using Nocturne.Connectors.Tidepool.Services;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

namespace Nocturne.Connectors.Tidepool.Tests.Services;

public class TidepoolConnectorServiceTests
{
    /// <summary>
    /// When authentication fails, the sync must report failure (so the connector surfaces as
    /// unhealthy) rather than silently returning a successful, empty result. Previously a missing
    /// token made the data fetches return null without error, so a bad-credential tenant was
    /// recorded as healthy and never alerted. The failure takes the shared shape: the summary in
    /// <c>Message</c> for the tenant's sync card and the source-qualified detail in <c>Errors</c>,
    /// which is what gets persisted as the connector's last error.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_ReportsFailure_WhenAuthenticationFails()
    {
        var fixture = new ServiceFixture(new TidepoolFakeHandler { LoginSucceeds = false });

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] },
            fixture.Config,
            CancellationToken.None);

        result.Success.Should().BeFalse("an authentication failure must mark the sync unhealthy");
        result.Message.Should().Be("Authentication failed");
        result.Errors.Should().ContainSingle()
            .Which.Should().Be($"Authentication failed for {DataSources.TidepoolConnector}");
    }

    /// <summary>
    /// A window Tidepool has no treatments for still records a count for each active treatment
    /// type: the tenant's sync card renders a badge per key, so a missing key reads as "never
    /// checked" rather than "checked, found nothing".
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenTreatmentTypesAreActiveButEmpty_RecordExplicitZeros()
    {
        var fixture = new ServiceFixture(new TidepoolFakeHandler());

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Boluses, SyncDataType.CarbIntake] },
            fixture.Config,
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ItemsSynced.Should().BeEquivalentTo(new Dictionary<SyncDataType, int>
        {
            [SyncDataType.Boluses] = 0,
            [SyncDataType.CarbIntake] = 0,
        });
    }

    /// <summary>Wires the connector service and a real token provider onto one fake handler.</summary>
    private sealed class ServiceFixture
    {
        internal TidepoolConnectorService Service { get; }
        internal TidepoolConnectorConfiguration Config { get; }

        internal ServiceFixture(TidepoolFakeHandler handler)
        {
            Config = new TidepoolConnectorConfiguration
            {
                Username = "user@example.com",
                Password = "secret",
            };

            var tenantAccessor = new Mock<ITenantAccessor>();
            tenantAccessor.Setup(t => t.IsResolved).Returns(true);
            tenantAccessor.Setup(t => t.TenantId).Returns(Guid.NewGuid());

            var serverResolver = new ConnectorServerResolver<TidepoolConnectorConfiguration>(
                null, null, TidepoolFakeHandler.Host);

            var tokenProvider = new TidepoolAuthTokenProvider(
                new HttpClient(handler),
                new ConnectorTokenCache(),
                serverResolver,
                tenantAccessor.Object,
                NullLogger<TidepoolAuthTokenProvider>.Instance,
                Mock.Of<IRetryDelayStrategy>());

            Service = new TidepoolConnectorService(
                new HttpClient(handler),
                serverResolver,
                NullLogger<TidepoolConnectorService>.Instance,
                Mock.Of<IRetryDelayStrategy>(),
                Mock.Of<IRateLimitingStrategy>(),
                tokenProvider);
        }
    }

    /// <summary>
    /// Serves Tidepool's Basic-auth login and an empty data collection for every requested type.
    /// A rejected login answers the way bad credentials do: a non-retryable 401.
    /// </summary>
    private sealed class TidepoolFakeHandler : HttpMessageHandler
    {
        internal const string Host = "api.tidepool.example";
        private const string UserId = "user-1";

        internal bool LoginSucceeds { get; init; } = true;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/auth/login")
            {
                if (!LoginSucceeds)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    {
                        Content = new StringContent(
                            """{"code":401,"reason":"No user matched the given details"}"""),
                    });

                var response = Json($$"""{"userid":"{{UserId}}"}""");
                response.Headers.Add(TidepoolConstants.Headers.SessionToken, "session-token");
                return Task.FromResult(response);
            }

            if (path == $"/data/{UserId}")
                return Task.FromResult(Json("[]"));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
