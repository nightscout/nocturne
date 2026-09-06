using System.Globalization;
using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.MyFitnessPal.Configurations;
using Nocturne.Connectors.MyFitnessPal.Services;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Connectors.MyFitnessPal.Tests.Services;

public class MyFitnessPalConnectorServiceTests
{
    /// <summary>
    /// A rejected food publish reports the shared per-type failure string and still counts the
    /// batch the sync handed over, so the tenant's sync card shows which type failed rather than a
    /// connector-specific phrasing the rest of the platform does not use.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenFoodPublishIsRejected_ReportsTheSharedFailure()
    {
        var fixture = new ServiceFixture(new MyFitnessPalFakeHandler(), publishSucceeds: false);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Food] }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeFalse("a food batch that never reached the tenant is not a successful sync");
        result.Errors.Should().Contain("Food publish failed");
        result.ItemsSynced[SyncDataType.Food].Should().Be(1);
    }

    /// <summary>
    /// An empty diary window records an explicit zero: the tenant's sync card renders a badge per
    /// key, so a missing key reads as "never checked" rather than "checked, found nothing".
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenTheDiaryWindowIsEmpty_RecordsAnExplicitZero()
    {
        var fixture = new ServiceFixture(
            new MyFitnessPalFakeHandler { HasDiaryEntry = false }, publishSucceeds: true);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Food] }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ItemsSynced.Should().Equal(new Dictionary<SyncDataType, int>
        {
            [SyncDataType.Food] = 0,
        });
    }

    /// <summary>Wires the connector service and a real token provider onto one fake handler.</summary>
    private sealed class ServiceFixture
    {
        internal MyFitnessPalConnectorService Service { get; }
        internal MyFitnessPalConnectorConfiguration Config { get; }

        internal ServiceFixture(MyFitnessPalFakeHandler handler, bool publishSucceeds)
        {
            Config = new MyFitnessPalConnectorConfiguration
            {
                Username = "user@example.com",
                Password = "secret",
            };

            var tenantAccessor = new Mock<ITenantAccessor>();
            tenantAccessor.Setup(t => t.IsResolved).Returns(true);
            tenantAccessor.Setup(t => t.TenantId).Returns(Guid.NewGuid());

            var serverResolver = new ConnectorServerResolver<MyFitnessPalConnectorConfiguration>(
                null, null, null);

            var tokenProvider = new MyFitnessPalAuthTokenProvider(
                new HttpClient(handler),
                new ConnectorTokenCache(),
                serverResolver,
                tenantAccessor.Object,
                NullLogger<MyFitnessPalAuthTokenProvider>.Instance,
                Mock.Of<IRetryDelayStrategy>());

            var configService = new Mock<IConnectorConfigurationService>();
            configService
                .Setup(s => s.GetSecretsAsync("MyFitnessPal", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Dictionary<string, string>());

            var metadata = new Mock<IMetadataPublisher>();
            metadata
                .Setup(p => p.PublishConnectorFoodEntriesAsync(
                    It.IsAny<IEnumerable<ConnectorFoodEntryImport>>(),
                    It.IsAny<string>(),
                    It.IsAny<WriteOrigin>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(publishSucceeds ? new List<ConnectorFoodEntry>() : null);

            var publisher = new Mock<IConnectorPublisher>();
            publisher.Setup(p => p.IsAvailable).Returns(true);
            publisher.Setup(p => p.Metadata).Returns(metadata.Object);

            Service = new MyFitnessPalConnectorService(
                new HttpClient(handler),
                serverResolver,
                NullLogger<MyFitnessPalConnectorService>.Instance,
                Mock.Of<IRetryDelayStrategy>(),
                tokenProvider,
                configService.Object,
                publisher.Object);
        }
    }

    /// <summary>
    /// Serves the OAuth token endpoint and one page of the GraphQL food diary. The legacy per-day
    /// diary is left to 404, which costs the day its meal names and nothing else.
    /// </summary>
    private sealed class MyFitnessPalFakeHandler : HttpMessageHandler
    {
        private static readonly string Today =
            DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        internal bool HasDiaryEntry { get; init; } = true;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == MyFitnessPalConstants.Endpoints.Token)
                return Task.FromResult(Json(
                    """{"access_token":"token","refresh_token":"refresh","expires_in":3600,"user_id":"u1"}"""));

            if (path == MyFitnessPalConstants.Endpoints.GraphQl)
                return Task.FromResult(Json(DiaryPage()));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private string DiaryPage()
        {
            var edges = HasDiaryEntry
                ? $$"""
                    {
                      "node": {
                        "__typename": "ActiveFoodDiaryEntry",
                        "id": "entry-1",
                        "date": "{{Today}}",
                        "consumedAt": "12:00:00",
                        "quantity": 1
                      },
                      "syncEdgeInfo": { "operation": "UPSERT" }
                    }
                    """
                : "";

            return $$"""
                {
                  "data": {
                    "batchSync": {
                      "foodDiaryEntries": {
                        "edges": [{{edges}}],
                        "pageInfo": { "hasPreviousPage": false }
                      }
                    }
                  }
                }
                """;
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
