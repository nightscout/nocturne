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
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.V4;
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

    /// <summary>
    /// Boluses come from the bolus fetch and carb intakes from the food fetch. The two counts
    /// differ, so reporting one type's batch under the other's key cannot pass, and the published
    /// payloads are checked as well so a swap beneath the counting cannot pass either.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_RecordsEachTreatmentTypeUnderItsOwnKey()
    {
        var handler = new TidepoolFakeHandler
        {
            BolusJson = """
                [
                  { "id": "b1", "time": "2026-01-01T08:00:00Z", "normal": 1.5 },
                  { "id": "b2", "time": "2026-01-01T09:00:00Z", "normal": 2.5 },
                  { "id": "b3", "time": "2026-01-01T10:00:00Z", "normal": 3.5 }
                ]
                """,
            FoodJson = """
                [
                  { "id": "f1", "time": "2026-01-01T12:00:00Z",
                    "nutrition": { "carbohydrate": { "net": 40 } } }
                ]
                """,
        };
        var fixture = new ServiceFixture(handler, withPublisher: true);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Boluses, SyncDataType.CarbIntake] },
            fixture.Config,
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ItemsSynced.Should().BeEquivalentTo(new Dictionary<SyncDataType, int>
        {
            [SyncDataType.Boluses] = 3,
            [SyncDataType.CarbIntake] = 1,
        });
        fixture.PublishedBoluses.Should().HaveCount(3);
        fixture.PublishedCarbIntakes.Should().ContainSingle()
            .Which.Carbs.Should().Be(40);
    }

    /// <summary>
    /// Every data request being rejected must not be reported as a window that held nothing. The
    /// fetch returns null rather than throwing, so without a guard the mapper yields empty lists
    /// and the sync card states — in green — that Tidepool was reached and had no insulin or carbs.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenEveryDataFetchIsRejected_ReportsFailureAndRecordsNoCounts()
    {
        var fixture = new ServiceFixture(
            new TidepoolFakeHandler { DataStatus = HttpStatusCode.Forbidden }, withPublisher: true);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest
            {
                DataTypes = [SyncDataType.Glucose, SyncDataType.Boluses, SyncDataType.CarbIntake],
            },
            fixture.Config,
            CancellationToken.None);

        result.Success.Should().BeFalse("a sync whose every request was rejected has not succeeded");
        result.Errors.Should().BeEquivalentTo(["Failed to fetch Glucose", "Failed to fetch Treatments"]);
        result.ItemsSynced.Should().BeEmpty(
            "a type the sync could not check must stay unreported rather than claim a zero");
    }

    /// <summary>Wires the connector service and a real token provider onto one fake handler.</summary>
    private sealed class ServiceFixture
    {
        internal TidepoolConnectorService Service { get; }
        internal TidepoolConnectorConfiguration Config { get; }
        internal List<Bolus> PublishedBoluses { get; } = [];
        internal List<CarbIntake> PublishedCarbIntakes { get; } = [];

        internal ServiceFixture(TidepoolFakeHandler handler, bool withPublisher = false)
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

            IConnectorPublisher? publisher = null;
            if (withPublisher)
            {
                var treatments = new Mock<ITreatmentPublisher>();
                treatments
                    .Setup(p => p.PublishBolusesAsync(
                        It.IsAny<IEnumerable<Bolus>>(), It.IsAny<string>(),
                        It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
                    .Callback<IEnumerable<Bolus>, string, WriteOrigin, CancellationToken>(
                        (batch, _, _, _) => PublishedBoluses.AddRange(batch))
                    .ReturnsAsync(true);
                treatments
                    .Setup(p => p.PublishCarbIntakesAsync(
                        It.IsAny<IEnumerable<CarbIntake>>(), It.IsAny<string>(),
                        It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
                    .Callback<IEnumerable<CarbIntake>, string, WriteOrigin, CancellationToken>(
                        (batch, _, _, _) => PublishedCarbIntakes.AddRange(batch))
                    .ReturnsAsync(true);

                var mock = new Mock<IConnectorPublisher>();
                mock.Setup(p => p.IsAvailable).Returns(true);
                mock.Setup(p => p.Treatments).Returns(treatments.Object);
                mock.Setup(p => p.Glucose).Returns(Mock.Of<IGlucosePublisher>());
                mock.Setup(p => p.Metadata).Returns(Mock.Of<IMetadataPublisher>());
                publisher = mock.Object;
            }

            Service = new TidepoolConnectorService(
                new HttpClient(handler),
                serverResolver,
                NullLogger<TidepoolConnectorService>.Instance,
                Mock.Of<IRetryDelayStrategy>(),
                Mock.Of<IRateLimitingStrategy>(),
                tokenProvider,
                publisher);
        }
    }

    /// <summary>
    /// Serves Tidepool's Basic-auth login and the data collection for each requested type.
    /// A rejected login answers the way bad credentials do: a non-retryable 401.
    /// </summary>
    private sealed class TidepoolFakeHandler : HttpMessageHandler
    {
        internal const string Host = "api.tidepool.example";
        private const string UserId = "user-1";

        internal bool LoginSucceeds { get; init; } = true;

        /// <summary>Status for the data endpoint; anything but OK is how a 403 or 404 arrives.</summary>
        internal HttpStatusCode DataStatus { get; init; } = HttpStatusCode.OK;

        internal string BolusJson { get; init; } = "[]";
        internal string FoodJson { get; init; } = "[]";

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
            {
                if (DataStatus != HttpStatusCode.OK)
                    return Task.FromResult(new HttpResponseMessage(DataStatus));

                var query = Uri.UnescapeDataString(request.RequestUri.Query);
                if (query.Contains($"type={TidepoolConstants.DataTypes.Bolus}", StringComparison.Ordinal))
                    return Task.FromResult(Json(BolusJson));
                if (query.Contains($"type={TidepoolConstants.DataTypes.Food}", StringComparison.Ordinal))
                    return Task.FromResult(Json(FoodJson));

                return Task.FromResult(Json("[]"));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
