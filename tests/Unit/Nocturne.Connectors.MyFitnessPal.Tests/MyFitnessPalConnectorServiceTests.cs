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
using Xunit;

namespace Nocturne.Connectors.MyFitnessPal.Tests;

public class MyFitnessPalConnectorServiceTests
{
    /// <summary>
    /// The legacy diary is the second host this connector talks to, and it rejects a revoked token
    /// the same way the GraphQL host does. A token left cached after that is re-sent for its full
    /// 30-day nominal lifetime. Only a 401 drops it: a 403 from this host is more likely a WAF
    /// block, and re-minting a token every cycle risks rate-limiting the login itself.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, true)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    public async Task SyncDataAsync_DropsTheCachedToken_OnlyWhenTheDiaryHostRejectsIt(
        HttpStatusCode diaryStatus, bool expectTokenDropped)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var handler = new MfpStubHandler(today, diaryStatus);
        var fixture = new ServiceFixture(handler);

        await fixture.Service.SyncDataAsync(
            new SyncRequest
            {
                DataTypes = [SyncDataType.Food],
                From = DateTime.UtcNow.AddDays(-1),
                To = DateTime.UtcNow.AddDays(1),
            },
            fixture.Config,
            CancellationToken.None);

        handler.DiaryRequests.Should().Be(1, "the day with entries must have been read");

        var session = await fixture.TokenProvider.GetCachedSessionAsync();
        (session == null).Should().Be(expectTokenDropped);
    }

    private sealed class ServiceFixture
    {
        internal MyFitnessPalConnectorService Service { get; }
        internal MyFitnessPalAuthTokenProvider TokenProvider { get; }

        internal MyFitnessPalConnectorConfiguration Config { get; } = new()
        {
            Username = "user@example.com",
            Password = "secret",
        };

        internal ServiceFixture(MfpStubHandler handler)
        {
            var tenantAccessor = new Mock<ITenantAccessor>();
            tenantAccessor.Setup(t => t.IsResolved).Returns(true);
            tenantAccessor.Setup(t => t.TenantId).Returns(Guid.NewGuid());

            var configService = new Mock<IConnectorConfigurationService>();
            configService
                .Setup(c => c.GetSecretsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Dictionary<string, string>());

            var resolver = new ConnectorServerResolver<MyFitnessPalConnectorConfiguration>(null, null, null);
            var http = new HttpClient(handler);

            TokenProvider = new MyFitnessPalAuthTokenProvider(
                http,
                new ConnectorTokenCache(),
                resolver,
                tenantAccessor.Object,
                NullLogger<MyFitnessPalAuthTokenProvider>.Instance,
                Mock.Of<IRetryDelayStrategy>());

            Service = new MyFitnessPalConnectorService(
                http,
                resolver,
                NullLogger<MyFitnessPalConnectorService>.Instance,
                Mock.Of<IRetryDelayStrategy>(),
                TokenProvider,
                configService.Object);
        }
    }

    /// <summary>
    /// Serves the token endpoint and a single-page GraphQL diary holding one entry for
    /// <paramref name="entryDate"/>, then answers the legacy per-day diary with
    /// <paramref name="diaryStatus"/>.
    /// </summary>
    private sealed class MfpStubHandler(DateOnly entryDate, HttpStatusCode diaryStatus) : HttpMessageHandler
    {
        internal int DiaryRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();

            if (url.Contains(MyFitnessPalConstants.Endpoints.Token, StringComparison.Ordinal))
                return Task.FromResult(Json(HttpStatusCode.OK, """
                    {"access_token":"access-1","refresh_token":"refresh-1","expires_in":2592000,"user_id":"user-1"}
                    """));

            if (url.Contains(MyFitnessPalConstants.Endpoints.GraphQl, StringComparison.Ordinal))
                return Task.FromResult(Json(HttpStatusCode.OK, DiaryPage()));

            if (url.Contains(MyFitnessPalConstants.Endpoints.Diary, StringComparison.Ordinal))
            {
                DiaryRequests++;
                return Task.FromResult(Json(diaryStatus, "{}"));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private string DiaryPage() => $$"""
            {
              "data": { "batchSync": { "foodDiaryEntries": {
                "edges": [ {
                  "node": {
                    "__typename": "ActiveFoodDiaryEntry",
                    "id": "entry-1",
                    "date": "{{entryDate:yyyy-MM-dd}}",
                    "consumedAt": "{{entryDate:yyyy-MM-dd}}T08:30:00Z",
                    "quantity": 1,
                    "servingSize": { "amount": 1, "nutritionMultiplier": 1, "isFraction": false, "unit": "slice" },
                    "food": { "__typename": "IndividualFood", "id": "food-1", "description": "Wholemeal Bread" },
                    "consumedNutrientSet": { "calories": 90, "protein": 4, "totalCarbohydrates": 15, "fat": 1 }
                  },
                  "syncEdgeInfo": { "operation": "UPSERT" }
                } ],
                "pageInfo": { "hasPreviousPage": false, "hasNextPage": false }
              } } }
            }
            """;

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
