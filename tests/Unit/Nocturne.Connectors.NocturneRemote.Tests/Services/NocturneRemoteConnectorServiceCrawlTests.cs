using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.NocturneRemote.Configurations;
using Nocturne.Connectors.NocturneRemote.Services;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Connectors.NocturneRemote.Tests.Services;

public class NocturneRemoteConnectorServiceCrawlTests
{
    /// <summary>
    /// A page that never arrives is not the end of the range. Ending the crawl there publishes the
    /// newest pages and reports a green sync, and because the next lower bound is derived from the
    /// newest record then stored locally, the pages below the failure are never asked for again.
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
    /// A body that will not parse — a captive portal's HTML, a truncated response — reaches the
    /// crawl as an exception rather than a null, and has to reach the same conclusion. This is the
    /// route by which most "carried no page" failures actually arrive: an envelope that parses at
    /// all supplies an empty <c>Data</c> rather than a null one.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenAPageIsUnparseable_FailsTheRunAndPublishesNothing()
    {
        var handler = new RemoteFakeHandler()
            .Serve(NocturneRemoteConstants.SensorGlucose,
                RemoteFakeHandler.GlucosePage(total: 6, "2026-01-03T08:00:00Z", "2026-01-03T08:05:00Z"),
                RemoteFakeHandler.Json("<html>upstream is down</html>"));
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
    /// Discarding the range is only the right trade because the page was asked for until the retry
    /// budget ran out. Failing on one unlucky 502 would leave a remote with ordinary page-level
    /// flakiness permanently red and syncing nothing for that type — worse than the truncation this
    /// change exists to remove.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenATransientFailureClearsWithinTheRetryBudget_CompletesTheCrawl()
    {
        var handler = new RemoteFakeHandler()
            .Serve(NocturneRemoteConstants.SensorGlucose,
                RemoteFakeHandler.GlucosePage(total: 3, "2026-01-03T08:00:00Z", "2026-01-03T08:05:00Z"),
                RemoteFakeHandler.Status(HttpStatusCode.BadGateway),
                RemoteFakeHandler.Status(HttpStatusCode.ServiceUnavailable),
                RemoteFakeHandler.GlucosePage(total: 3, "2026-01-03T08:10:00Z"));
        var fixture = new ServiceFixture(handler, config: NewConfig(maxRetryAttempts: 3));

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] },
            fixture.Config,
            CancellationToken.None);

        result.Success.Should().BeTrue("the page arrived within the attempt budget");
        result.ItemsSynced.Should().BeEquivalentTo(new Dictionary<SyncDataType, int>
        {
            [SyncDataType.Glucose] = 3,
        });
        fixture.PublishedGlucose.Should().HaveCount(3);
    }

    /// <summary>
    /// Each data type crawls its own endpoint, so one endpoint failing must cost only that type. A
    /// failed run withholds the connector's last-successful-sync stamp and shows the tenant a red
    /// connector, which has to name what actually broke. The glucose crawl fails on its second page,
    /// so the failure is reached past the auth probe rather than by it.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenOneTypesCrawlFails_LeavesTheOtherTypeSynced()
    {
        var handler = new RemoteFakeHandler()
            .Serve(NocturneRemoteConstants.SensorGlucose,
                RemoteFakeHandler.GlucosePage(total: 6, "2026-01-03T08:00:00Z", "2026-01-03T08:05:00Z"),
                RemoteFakeHandler.Status(HttpStatusCode.BadGateway))
            .Serve(NocturneRemoteConstants.Boluses,
                RemoteFakeHandler.BolusPage(total: 1, "2026-01-03T09:00:00Z"));
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
    /// mark the connector red while everything the tenant enabled synced. Activity rather than
    /// glucose, because the auth probe reads the glucose endpoint and fails the whole run before the
    /// per-type gate is reached — see
    /// <see cref="SyncDataAsync_WhenTheGlucoseEndpointIsBroken_FailsBeforeAnyTypeIsConsidered"/>.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenASwitchedOffTypesEndpointIsBroken_ReportsSuccess()
    {
        var handler = new RemoteFakeHandler()
            .Break(NocturneRemoteConstants.Activity, HttpStatusCode.BadGateway)
            .Serve(NocturneRemoteConstants.Boluses,
                RemoteFakeHandler.BolusPage(total: 1, "2026-01-03T09:00:00Z"));
        var fixture = new ServiceFixture(handler, config: NewConfig(syncActivity: false));

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Activity, SyncDataType.Boluses] },
            fixture.Config,
            CancellationToken.None);

        result.Success.Should().BeTrue("no type the tenant enabled failed to sync");
        result.Errors.Should().BeEmpty();
        result.ItemsSynced.Should().BeEquivalentTo(new Dictionary<SyncDataType, int>
        {
            [SyncDataType.Boluses] = 1,
        });
        handler.Requests.Should().NotContain(url => url.Contains(NocturneRemoteConstants.Activity),
            "a switched-off type is not crawled at all, which is why its endpoint cannot fail the run");
    }

    /// <summary>
    /// Characterises a limitation this change does not remove, so it is not mistaken for a property
    /// the connector has. <c>AuthenticateWithConfigAsync</c> probes the sensor-glucose endpoint on
    /// every run whatever the tenant enabled, and a rejected probe fails the run before any type is
    /// considered. So a remote whose glucose endpoint alone is broken — or a grant without
    /// <c>glucose.read</c> — costs the tenant every other type too, and is reported as a credential
    /// problem. Per-type scoping begins after this gate, not before it.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenTheGlucoseEndpointIsBroken_FailsBeforeAnyTypeIsConsidered()
    {
        var handler = new RemoteFakeHandler()
            .Break(NocturneRemoteConstants.SensorGlucose, HttpStatusCode.BadGateway)
            .Serve(NocturneRemoteConstants.Boluses,
                RemoteFakeHandler.BolusPage(total: 1, "2026-01-03T09:00:00Z"));
        var fixture = new ServiceFixture(handler, config: NewConfig(syncGlucose: false));

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose, SyncDataType.Boluses] },
            fixture.Config,
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Authentication failed");
        result.Errors.Should().ContainSingle()
            .Which.Should().Be($"Authentication failed for {DataSources.NocturneRemoteConnector}");
        handler.Requests.Should().NotContain(url => url.Contains(NocturneRemoteConstants.Boluses),
            "the probe fails the run before the enabled types are reached");
    }

    /// <summary>
    /// Foods are a single flat fetch rather than a crawl, and used to answer a rejection with an
    /// empty list — which <c>RecordPublishOutcome</c> records as a confident zero, telling the
    /// tenant in green that the remote was reached and had no foods.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenTheFoodsFetchIsRejected_FailsTheRunAndRecordsNoCount()
    {
        var handler = new RemoteFakeHandler().Break(NocturneRemoteConstants.Foods, HttpStatusCode.Forbidden);
        var fixture = new ServiceFixture(handler);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Food] },
            fixture.Config,
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle()
            .Which.Should().StartWith($"Failed to sync {SyncDataType.Food}");
        result.ItemsSynced.Should().BeEmpty(
            "a rejected fetch never reached the remote, so it cannot report that there were no foods");
    }

    /// <summary>The foods equivalent of an exhausted range: a remote with no foods is still a success.</summary>
    [Fact]
    public async Task SyncDataAsync_WhenTheRemoteHasNoFoods_ReportsAnExplicitZero()
    {
        var handler = new RemoteFakeHandler()
            .Serve(NocturneRemoteConstants.Foods, RemoteFakeHandler.Json("[]"));
        var fixture = new ServiceFixture(handler);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Food] },
            fixture.Config,
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ItemsSynced.Should().BeEquivalentTo(new Dictionary<SyncDataType, int>
        {
            [SyncDataType.Food] = 0,
        });
    }

    /// <summary>
    /// Device status crawls the remote's v1 endpoint on a time cursor rather than an offset, and
    /// carried the same swallow — worse, it read a rejected page and a range with nothing left in it
    /// through one condition. A grant the remote will keep refusing is not retried, so it arrives as
    /// the empty result the walk backwards through history used to stop on.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenTheDeviceStatusCrawlIsRejectedMidRange_FailsTheRun()
    {
        var handler = new RemoteFakeHandler()
            .Serve(RemoteFakeHandler.V1DeviceStatus,
                RemoteFakeHandler.DeviceStatusPage("2026-01-03T08:05:00Z", "2026-01-03T08:00:00Z"),
                RemoteFakeHandler.Status(HttpStatusCode.Forbidden));
        var fixture = new ServiceFixture(handler);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.DeviceStatus] },
            fixture.Config,
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle()
            .Which.Should().StartWith($"Failed to sync {SyncDataType.DeviceStatus}");
        result.ItemsSynced.Should().BeEmpty();
    }

    /// <summary>A remote with no device statuses in range answers an empty array, and that succeeds.</summary>
    [Fact]
    public async Task SyncDataAsync_WhenTheRemoteHasNoDeviceStatuses_ReportsAnExplicitZero()
    {
        var handler = new RemoteFakeHandler()
            .Serve(RemoteFakeHandler.V1DeviceStatus, RemoteFakeHandler.Json("[]"));
        var fixture = new ServiceFixture(handler);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.DeviceStatus] },
            fixture.Config,
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ItemsSynced.Should().BeEquivalentTo(new Dictionary<SyncDataType, int>
        {
            [SyncDataType.DeviceStatus] = 0,
        });
    }

    /// <summary>
    /// On an open-ended catch-up each family asks for its own range. Sharing the glucose-derived
    /// bound strands every other family: this run's glucose publish moves that bound past the range
    /// a failed treatment crawl still owes, so the gap it left cannot be repaired next cycle.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenOpenEnded_AsksEachFamilyForItsOwnRange()
    {
        var latestTreatment = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var glucoseFrom = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var handler = new RemoteFakeHandler();
        var fixture = new ServiceFixture(handler, latestTreatment: latestTreatment);

        await fixture.Service.SyncDataAsync(
            new SyncRequest
            {
                From = glucoseFrom,
                To = null,
                DataTypes = [SyncDataType.Glucose, SyncDataType.Boluses],
            },
            fixture.Config,
            CancellationToken.None);

        handler.CrawlOf(NocturneRemoteConstants.SensorGlucose).Should()
            .Contain($"from={glucoseFrom:o}");
        handler.CrawlOf(NocturneRemoteConstants.Boluses).Should()
            .Contain($"from={latestTreatment.AddMinutes(-5):o}",
                "the treatment family resumes from its own newest stored record, not glucose's");
    }

    /// <summary>
    /// An explicit range is honoured as given for every family — that is how a cursor reset re-pulls
    /// history the per-family catch-up bounds would otherwise skip.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenGivenAnExplicitRange_AsksEveryFamilyForIt()
    {
        var from = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var handler = new RemoteFakeHandler();
        var fixture = new ServiceFixture(
            handler, latestTreatment: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        await fixture.Service.SyncDataAsync(
            new SyncRequest
            {
                From = from,
                To = new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc),
                DataTypes = [SyncDataType.Glucose, SyncDataType.Boluses],
            },
            fixture.Config,
            CancellationToken.None);

        handler.CrawlOf(NocturneRemoteConstants.SensorGlucose).Should().Contain($"from={from:o}");
        handler.CrawlOf(NocturneRemoteConstants.Boluses).Should().Contain($"from={from:o}");
    }

    private static NocturneRemoteConnectorConfiguration NewConfig(
        bool syncGlucose = true,
        bool syncActivity = true,
        int maxRetryAttempts = 1) => new()
    {
        Url = RemoteFakeHandler.BaseUrl,
        Token = "direct-grant-token",
        MaxCount = RemoteFakeHandler.PageSize,
        // One attempt unless a test is about the budget, so each scripted response answers exactly
        // one request and a crawl script reads in the order the crawl makes them.
        MaxRetryAttempts = maxRetryAttempts,
        SyncGlucose = syncGlucose,
        SyncActivity = syncActivity,
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
            NocturneRemoteConnectorConfiguration? config = null,
            DateTime? latestTreatment = null)
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
                .Setup(p => p.GetLatestTreatmentTimestampAsync(
                    It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(latestTreatment);
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
                Mock.Of<IRetryDelayStrategy>(),
                publisher.Object);
        }
    }

    /// <summary>
    /// Serves the remote instance's endpoints, one scripted response per request in the order the
    /// connector makes them.
    /// </summary>
    /// <remarks>
    /// <see cref="Break"/> models an endpoint that is down and answers every request to it — the
    /// auth probe included, because the probe reads the sensor-glucose endpoint like any other
    /// caller. <see cref="Serve"/> models the responses to successive pages of a working endpoint,
    /// which the probe is deliberately not served from: it asks for one record before the crawl
    /// starts, so letting it consume the crawl's first page would misdescribe every script.
    /// </remarks>
    private sealed class RemoteFakeHandler : HttpMessageHandler
    {
        internal const string BaseUrl = "https://remote.example";
        internal const string V1DeviceStatus = "/api/v1/devicestatus.json";

        /// <summary>Page size the fixture configures, small enough to script a multi-page crawl.</summary>
        internal const int PageSize = 2;

        private readonly Dictionary<string, Queue<HttpResponseMessage>> _pages = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HttpStatusCode> _broken = new(StringComparer.Ordinal);

        /// <summary>Every request made, so a test can assert on the range each crawl asked for.</summary>
        internal List<string> Requests { get; } = [];

        internal RemoteFakeHandler Serve(string path, params HttpResponseMessage[] responses)
        {
            _pages[path] = new Queue<HttpResponseMessage>(responses);
            return this;
        }

        /// <summary>The first crawl request made to <paramref name="path"/>, excluding the auth probe.</summary>
        internal string CrawlOf(string path) =>
            Requests.First(u => u.Contains(path, StringComparison.Ordinal)
                                && u.Contains("offset=", StringComparison.Ordinal));

        internal RemoteFakeHandler Break(string path, HttpStatusCode status)
        {
            _broken[path] = status;
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

        internal static HttpResponseMessage DeviceStatusPage(params string[] createdAt) =>
            Json("[" + string.Join(",", createdAt.Select(t =>
                $$"""{"_id":"{{Guid.NewGuid()}}","created_at":"{{t}}"}""")) + "]");

        private static HttpResponseMessage Page(int total, IEnumerable<string> records) =>
            Json("{\"data\":[" + string.Join(",", records)
                 + "],\"pagination\":{\"limit\":" + PageSize
                 + ",\"offset\":0,\"total\":" + total + "}}");

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(request.RequestUri.ToString());

            if (_broken.TryGetValue(path, out var status))
                return Task.FromResult(Status(status));

            if (IsAuthProbe(request))
                return Task.FromResult(GlucosePage(total: 0));

            if (_pages.TryGetValue(path, out var queue) && queue.Count > 0)
                return Task.FromResult(queue.Dequeue());

            return Task.FromResult(
                path == V1DeviceStatus || path == NocturneRemoteConstants.Foods
                    ? Json("[]")
                    : GlucosePage(total: 0));
        }

        private static bool IsAuthProbe(HttpRequestMessage request) =>
            request.RequestUri!.AbsolutePath == NocturneRemoteConstants.SensorGlucose
            && !request.RequestUri.Query.Contains("offset=", StringComparison.Ordinal);
    }
}
