using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.NocturneRemote.Configurations;
using Nocturne.Connectors.NocturneRemote.Services;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Connectors.NocturneRemote.Tests.Services;

public class NocturneRemoteConnectorServiceCrawlTests
{
    /// <summary>
    /// A page that never arrives is not the end of the range. Ending the crawl there publishes the
    /// newest pages and reports a green sync, and because the next lower bound is derived from the
    /// newest record now stored locally, the pages below the failure are never asked for again.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenAPageMidCrawlIsRejected_FailsTheRunAndPublishesNothing()
    {
        var handler = new RemoteFakeHandler()
            .Serve(NocturneRemoteConstants.SensorGlucose,
                RemoteFakeHandler.GlucosePage(total: 6, "2026-01-03T08:00:00Z", "2026-01-03T08:05:00Z"),
                RemoteFakeHandler.Status(HttpStatusCode.BadGateway));
        var fixture = new ServiceFixture(handler);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] },
            fixture.Config,
            CancellationToken.None);

        result.Success.Should().BeFalse("a crawl that lost a page has not synced the range");
        result.Errors.Should().ContainSingle()
            .Which.Should().StartWith($"Failed to sync {SyncDataType.Glucose}");
        result.ItemsSynced.Should().BeEmpty(
            "a range the sync could not read through must stay unreported rather than claim a count");
        fixture.PublishedGlucose.Should().BeEmpty(
            "publishing the pages above the failure would put the ones below it out of reach");
    }

    /// <summary>
    /// The same swallow by a different route: a 200 whose envelope carries no page is a fetch that
    /// failed, not a range that ran out. A range that ran out answers an empty array.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenAPageCarriesNoData_FailsTheRunAndPublishesNothing()
    {
        var handler = new RemoteFakeHandler()
            .Serve(NocturneRemoteConstants.SensorGlucose,
                RemoteFakeHandler.GlucosePage(total: 6, "2026-01-03T08:00:00Z", "2026-01-03T08:05:00Z"),
                RemoteFakeHandler.Json("""{"data":null,"pagination":{"limit":2,"offset":2,"total":6}}"""));
        var fixture = new ServiceFixture(handler);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] },
            fixture.Config,
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ItemsSynced.Should().BeEmpty();
        fixture.PublishedGlucose.Should().BeEmpty();
    }

    /// <summary>
    /// The distinction the failure path must not erase: a range the remote genuinely has nothing
    /// left in answers with a short page, and that is a successful sync of everything there was.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenTheRangeIsExhausted_ReportsSuccessAndPublishesEveryPage()
    {
        var handler = new RemoteFakeHandler()
            .Serve(NocturneRemoteConstants.SensorGlucose,
                RemoteFakeHandler.GlucosePage(total: 3, "2026-01-03T08:00:00Z", "2026-01-03T08:05:00Z"),
                RemoteFakeHandler.GlucosePage(total: 3, "2026-01-03T08:10:00Z"));
        var fixture = new ServiceFixture(handler);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] },
            fixture.Config,
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Errors.Should().BeEmpty();
        result.ItemsSynced.Should().BeEquivalentTo(new Dictionary<SyncDataType, int>
        {
            [SyncDataType.Glucose] = 3,
        });
        fixture.PublishedGlucose.Should().HaveCount(3);
    }

    /// <summary>
    /// A window the remote holds nothing for still records a count: the tenant's sync card renders a
    /// badge per key, so "checked, found nothing" must not be reported the way "could not check" is.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenTheRemoteHasNoRecords_ReportsAnExplicitZero()
    {
        var handler = new RemoteFakeHandler()
            .Serve(NocturneRemoteConstants.SensorGlucose, RemoteFakeHandler.GlucosePage(total: 0));
        var fixture = new ServiceFixture(handler);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] },
            fixture.Config,
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ItemsSynced.Should().BeEquivalentTo(new Dictionary<SyncDataType, int>
        {
            [SyncDataType.Glucose] = 0,
        });
    }

    /// <summary>
    /// Each data type crawls its own endpoint, so one endpoint failing must cost only that type. A
    /// failed run withholds the connector's last-successful-sync stamp and shows the tenant a red
    /// connector, which has to name what actually broke.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenOneTypesCrawlFails_LeavesTheOtherTypeSynced()
    {
        var handler = new RemoteFakeHandler()
            .Serve(NocturneRemoteConstants.SensorGlucose, RemoteFakeHandler.Status(HttpStatusCode.BadGateway))
            .Serve(NocturneRemoteConstants.Boluses, RemoteFakeHandler.BolusPage(total: 1, "2026-01-03T09:00:00Z"));
        var fixture = new ServiceFixture(handler);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose, SyncDataType.Boluses] },
            fixture.Config,
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle()
            .Which.Should().StartWith($"Failed to sync {SyncDataType.Glucose}");
        result.ItemsSynced.Should().BeEquivalentTo(new Dictionary<SyncDataType, int>
        {
            [SyncDataType.Boluses] = 1,
        }, "a rejected glucose crawl must not cost the boluses, nor claim a glucose count");
        fixture.PublishedBoluses.Should().ContainSingle();
    }

    /// <summary>
    /// A type the tenant switched off is never crawled, so a remote that rejects its endpoint cannot
    /// mark the connector red while everything the tenant enabled synced.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenASwitchedOffTypesEndpointWouldFail_ReportsSuccess()
    {
        var handler = new RemoteFakeHandler()
            .Serve(NocturneRemoteConstants.SensorGlucose, RemoteFakeHandler.Status(HttpStatusCode.BadGateway))
            .Serve(NocturneRemoteConstants.Boluses, RemoteFakeHandler.BolusPage(total: 1, "2026-01-03T09:00:00Z"));
        var fixture = new ServiceFixture(handler, config: NewConfig(syncGlucose: false));

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose, SyncDataType.Boluses] },
            fixture.Config,
            CancellationToken.None);

        result.Success.Should().BeTrue("no type the tenant enabled failed to sync");
        result.Errors.Should().BeEmpty();
        result.ItemsSynced.Should().BeEquivalentTo(new Dictionary<SyncDataType, int>
        {
            [SyncDataType.Boluses] = 1,
        });
    }

    private static NocturneRemoteConnectorConfiguration NewConfig(bool syncGlucose = true) => new()
    {
        Url = RemoteFakeHandler.BaseUrl,
        Token = "direct-grant-token",
        MaxCount = RemoteFakeHandler.PageSize,
        SyncGlucose = syncGlucose,
    };

    /// <summary>Wires the connector service and a recording publisher onto one fake handler.</summary>
    private sealed class ServiceFixture
    {
        internal NocturneRemoteConnectorService Service { get; }
        internal NocturneRemoteConnectorConfiguration Config { get; }
        internal List<SensorGlucose> PublishedGlucose { get; } = [];
        internal List<Bolus> PublishedBoluses { get; } = [];

        internal ServiceFixture(
            RemoteFakeHandler handler,
            NocturneRemoteConnectorConfiguration? config = null)
        {
            Config = config ?? NewConfig();

            var glucose = new Mock<IGlucosePublisher>();
            glucose
                .Setup(p => p.PublishSensorGlucoseAsync(
                    It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<string>(),
                    It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
                .Callback<IEnumerable<SensorGlucose>, string, WriteOrigin, CancellationToken>(
                    (batch, _, _, _) => PublishedGlucose.AddRange(batch))
                .ReturnsAsync(true);

            var treatments = new Mock<ITreatmentPublisher>();
            treatments
                .Setup(p => p.PublishBolusesAsync(
                    It.IsAny<IEnumerable<Bolus>>(), It.IsAny<string>(),
                    It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
                .Callback<IEnumerable<Bolus>, string, WriteOrigin, CancellationToken>(
                    (batch, _, _, _) => PublishedBoluses.AddRange(batch))
                .ReturnsAsync(true);

            var publisher = new Mock<IConnectorPublisher>();
            publisher.Setup(p => p.IsAvailable).Returns(true);
            publisher.Setup(p => p.Glucose).Returns(glucose.Object);
            publisher.Setup(p => p.Treatments).Returns(treatments.Object);
            publisher.Setup(p => p.Device).Returns(Mock.Of<IDevicePublisher>());
            publisher.Setup(p => p.Metadata).Returns(Mock.Of<IMetadataPublisher>());

            var registration = new Mock<IConnectorRegistration<NocturneRemoteConnectorConfiguration>>();
            registration.Setup(r => r.Defaults).Returns(Config);

            Service = new NocturneRemoteConnectorService(
                new HttpClient(handler),
                Mock.Of<IConnectorServerResolver<NocturneRemoteConnectorConfiguration>>(),
                NullLogger<NocturneRemoteConnectorService>.Instance,
                registration.Object,
                publisher.Object);
        }
    }

    /// <summary>
    /// Serves the remote instance's V4 paginated endpoints, one scripted response per page in the
    /// order the crawl asks for them. The auth probe carries no offset, so it is answered separately
    /// and a rejected data endpoint does not read as a rejected credential.
    /// </summary>
    private sealed class RemoteFakeHandler : HttpMessageHandler
    {
        internal const string BaseUrl = "https://remote.example";

        /// <summary>Page size the fixture configures, small enough to script a multi-page crawl.</summary>
        internal const int PageSize = 2;

        private readonly Dictionary<string, Queue<HttpResponseMessage>> _pages = new(StringComparer.Ordinal);

        internal RemoteFakeHandler Serve(string path, params HttpResponseMessage[] responses)
        {
            _pages[path] = new Queue<HttpResponseMessage>(responses);
            return this;
        }

        internal static HttpResponseMessage Status(HttpStatusCode status) => new(status);

        internal static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        internal static HttpResponseMessage GlucosePage(int total, params string[] timestamps) =>
            Page(total, timestamps.Select(t =>
                $$"""{"id":"{{Guid.NewGuid()}}","timestamp":"{{t}}","mgdl":120}"""));

        internal static HttpResponseMessage BolusPage(int total, params string[] timestamps) =>
            Page(total, timestamps.Select(t =>
                $$"""{"id":"{{Guid.NewGuid()}}","timestamp":"{{t}}","insulin":1.5}"""));

        private static HttpResponseMessage Page(int total, IEnumerable<string> records) =>
            Json("{\"data\":[" + string.Join(",", records)
                 + "],\"pagination\":{\"limit\":" + PageSize
                 + ",\"offset\":0,\"total\":" + total + "}}");

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (!request.RequestUri.Query.Contains("offset=", StringComparison.Ordinal))
                return Task.FromResult(GlucosePage(total: 0));

            if (_pages.TryGetValue(path, out var queue) && queue.Count > 0)
                return Task.FromResult(queue.Dequeue());

            return Task.FromResult(GlucosePage(total: 0));
        }
    }
}
