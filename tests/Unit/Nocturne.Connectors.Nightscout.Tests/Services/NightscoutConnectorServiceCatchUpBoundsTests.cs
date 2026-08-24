using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services;
using Xunit;

namespace Nocturne.Connectors.Nightscout.Tests.Services;

public class NightscoutConnectorServiceCatchUpBoundsTests
{
    /// <summary>
    /// A caller's lower bound is never narrowed by a family's resume point. An explicit <c>from</c>
    /// with no <c>to</c> is a legitimate request shape and the one an admin repairing a months-old
    /// gap sends; answering it from the watermark fetches nothing and reports the run as a success
    /// with a zero count.
    /// </summary>
    [Theory]
    [InlineData(SyncDataType.Boluses, "treatments")]
    [InlineData(SyncDataType.DeviceStatus, "devicestatus")]
    [InlineData(SyncDataType.Activity, "activity")]
    public async Task SyncDataAsync_WhenGivenALowerBoundBelowTheResumePoint_HonoursTheCallersBound(
        SyncDataType dataType, string collection)
    {
        var askedFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var watermark = new DateTime(2026, 5, 31, 0, 0, 0, DateTimeKind.Utc);
        var handler = new CollectingHandler();
        var service = NewService(handler, watermarks: watermark);

        await service.SyncDataAsync(
            new SyncRequest { From = askedFrom, To = null, DataTypes = [dataType] },
            NewConfig(),
            CancellationToken.None);

        handler.CrawlOf(collection).Should()
            .Contain(LowerBound(askedFrom), "the caller asked for this lower bound");
    }

    /// <summary>
    /// On an open-ended catch-up a family that has fallen behind widens the bound back to its own
    /// resume point. Leaving it on the glucose-derived bound strands it: this run's glucose publish
    /// moves that bound past the range a failed crawl still owes, so the gap cannot be repaired
    /// next cycle.
    /// </summary>
    [Theory]
    [InlineData(SyncDataType.Boluses, "treatments")]
    [InlineData(SyncDataType.DeviceStatus, "devicestatus")]
    [InlineData(SyncDataType.Activity, "activity")]
    public async Task SyncDataAsync_WhenAFamilyHasFallenBehind_WidensToItsOwnResumePoint(
        SyncDataType dataType, string collection)
    {
        var watermark = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var glucoseFrom = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var handler = new CollectingHandler();
        var service = NewService(handler, watermarks: watermark);

        await service.SyncDataAsync(
            new SyncRequest { From = glucoseFrom, To = null, DataTypes = [dataType] },
            NewConfig(),
            CancellationToken.None);

        handler.CrawlOf(collection).Should()
            .Contain(LowerBound(watermark - CatchUpOverlap),
                "the family resumes from its own newest stored record, not glucose's");
    }

    /// <summary>
    /// A caller supplying no lower bound is not asking for everything, and each family stands on
    /// its own resume point. This connector's glucose floor is open, so a background cycle for a
    /// tenant with glucose switched off — or with none published yet — carries a null <c>from</c>
    /// on every run, as do the tenant's own sync button and the dev-admin sweep. Reading that as a
    /// request for the whole source re-crawls the entire history on every one of those runs.
    /// </summary>
    [Theory]
    [InlineData(SyncDataType.Boluses, "treatments")]
    [InlineData(SyncDataType.DeviceStatus, "devicestatus")]
    [InlineData(SyncDataType.Activity, "activity")]
    public async Task SyncDataAsync_WhenTheCallerSuppliesNoLowerBound_ResumesFromTheFamilysWatermark(
        SyncDataType dataType, string collection)
    {
        var watermark = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var handler = new CollectingHandler();
        var service = NewService(handler, watermarks: watermark);

        await service.SyncDataAsync(
            new SyncRequest { From = null, To = null, DataTypes = [dataType] },
            NewConfig(),
            CancellationToken.None);

        handler.CrawlOf(collection).Should()
            .Contain(LowerBound(watermark - CatchUpOverlap),
                "the family has a resume point and the caller supplied nothing to widen it with");
    }

    /// <summary>
    /// An explicit range is answered as asked, resume points and all: it is the shape a manual
    /// re-import of one window sends, and a bound widened back to the watermark re-crawls
    /// everything between — the whole source, for a family that has stored nothing.
    /// </summary>
    [Theory]
    [InlineData(SyncDataType.Boluses, "treatments")]
    [InlineData(SyncDataType.DeviceStatus, "devicestatus")]
    [InlineData(SyncDataType.Activity, "activity")]
    public async Task SyncDataAsync_WhenGivenAnExplicitRange_AsksForItAsGiven(
        SyncDataType dataType, string collection)
    {
        var askedFrom = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var watermark = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var handler = new CollectingHandler();
        var service = NewService(handler, watermarks: watermark);

        await service.SyncDataAsync(
            new SyncRequest
            {
                From = askedFrom,
                To = new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc),
                DataTypes = [dataType],
            },
            NewConfig(),
            CancellationToken.None);

        handler.CrawlOf(collection).Should()
            .Contain(LowerBound(askedFrom), "the caller bounded the range at both ends");
    }

    /// <summary>
    /// A Nightscout instance is a full data export, so with no treatments stored the initial
    /// backfill has no lower bound at all — and the glucose cursor <c>request.From</c> carries on a
    /// background run must not narrow it to one. A treatments crawl that failed on the first sync
    /// after glucose landed is exactly that state, and bounding its retry at the glucose cursor
    /// puts the history below permanently out of reach.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenNoTreatmentsAreStored_CrawlsTheFullHistory()
    {
        var handler = new CollectingHandler();
        var service = NewService(handler, watermarks: null);

        await service.SyncDataAsync(
            new SyncRequest
            {
                From = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                To = null,
                DataTypes = [SyncDataType.Boluses],
            },
            NewConfig(),
            CancellationToken.None);

        handler.CrawlOf("treatments").Should().NotContain("$gte");
    }

    /// <summary>
    /// Device status and activity answer with no resume point rather than an open one, and take the
    /// caller's bound instead: their initial window is high-volume telemetry that would otherwise
    /// be re-fetched in full on every sync until the first page stored.
    /// </summary>
    [Theory]
    [InlineData(SyncDataType.DeviceStatus, "devicestatus")]
    [InlineData(SyncDataType.Activity, "activity")]
    public async Task SyncDataAsync_WhenNoTelemetryIsStored_CrawlsFromTheCallersBound(
        SyncDataType dataType, string collection)
    {
        var askedFrom = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var handler = new CollectingHandler();
        var service = NewService(handler, watermarks: null);

        await service.SyncDataAsync(
            new SyncRequest { From = askedFrom, To = null, DataTypes = [dataType] },
            NewConfig(),
            CancellationToken.None);

        handler.CrawlOf(collection).Should().Contain(LowerBound(askedFrom));
    }

    /// <summary>Overlap the shared catch-up calculation subtracts to absorb clock drift.</summary>
    private static readonly TimeSpan CatchUpOverlap = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Envelope the created_at crawls widen their opening bounds by, to reach records an old
    /// uploader wrote with a local offset.
    /// </summary>
    private static readonly TimeSpan WallClockEnvelope = TimeSpan.FromHours(14);

    private static string LowerBound(DateTime from) =>
        $"find[created_at][$gte]={from - WallClockEnvelope:o}";

    private static NightscoutConnectorConfiguration NewConfig() => new()
    {
        Url = "https://ns.example",
        ApiSecret = "secret",
    };

    private static NightscoutConnectorService NewService(
        CollectingHandler handler,
        DateTime? watermarks)
    {
        var treatments = new Mock<ITreatmentPublisher>();
        treatments
            .Setup(p => p.GetLatestTreatmentTimestampAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(watermarks);

        var device = new Mock<IDevicePublisher>();
        device
            .Setup(p => p.GetLatestDeviceStatusTimestampAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(watermarks);

        var metadata = new Mock<IMetadataPublisher>();
        metadata
            .Setup(p => p.GetLatestActivityTimestampAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(watermarks);

        var publisher = new Mock<IConnectorPublisher>();
        publisher.Setup(p => p.IsAvailable).Returns(true);
        publisher.Setup(p => p.Glucose).Returns(Mock.Of<IGlucosePublisher>());
        publisher.Setup(p => p.Treatments).Returns(treatments.Object);
        publisher.Setup(p => p.Device).Returns(device.Object);
        publisher.Setup(p => p.Metadata).Returns(metadata.Object);

        var registration = new Mock<IConnectorRegistration<NightscoutConnectorConfiguration>>();
        registration.Setup(r => r.Defaults).Returns(new NightscoutConnectorConfiguration());

        return new NightscoutConnectorService(
            new HttpClient(handler),
            Mock.Of<IConnectorServerResolver<NightscoutConnectorConfiguration>>(),
            NullLogger<NightscoutConnectorService>.Instance,
            Mock.Of<IRetryDelayStrategy>(),
            Mock.Of<IRateLimitingStrategy>(),
            registration.Object,
            publisher.Object);
    }

    /// <summary>
    /// Records every request and answers each with an empty collection, so a crawl asks once per
    /// collection and the range it asked for is what the test reads.
    /// </summary>
    private sealed class CollectingHandler : HttpMessageHandler
    {
        private readonly List<string> _requests = [];

        /// <summary>The crawl request for <paramref name="collection"/>, as the source reads it.</summary>
        internal string CrawlOf(string collection) =>
            _requests.Single(u => u.Contains($"/api/v1/{collection}.json", StringComparison.Ordinal));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // The bracket syntax Nightscout filters by is escaped on the wire.
            _requests.Add(Uri.UnescapeDataString(request.RequestUri!.ToString()));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json"),
            });
        }
    }
}
