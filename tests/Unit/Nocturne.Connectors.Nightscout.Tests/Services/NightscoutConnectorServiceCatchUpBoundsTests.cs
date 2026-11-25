using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Tests.Shared.Connectors;
using Xunit;
using Nocturne.Tests.Shared.Mocks;

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
    /// A background run's lower bound is glucose's resume point, which says nothing about any other
    /// family. Widening them to a stale one — an entries read that has failed for months — re-crawls
    /// every family from there on every run.
    /// </summary>
    [Theory]
    [InlineData("treatments")]
    [InlineData("devicestatus")]
    [InlineData("activity")]
    public async Task SyncDataAsync_OnABackgroundRunWithAStaleGlucoseCursor_ResumesFromTheFamilysWatermark(
        string collection)
    {
        var watermark = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var handler = new CollectingHandler();
        var service = NewService(handler, watermarks: watermark,
            latestGlucose: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        await service.SyncDataAsync(NewConfig(), CancellationToken.None);

        handler.CrawlOf(collection).Should().Contain(LowerBound(watermark - CatchUpOverlap));
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
    /// A Nightscout instance is a full data export, so a family with nothing stored has no lower
    /// bound at all — and the glucose cursor <c>request.From</c> carries on a background run must not
    /// narrow it to one. A crawl that failed on the first sync after glucose landed is exactly that
    /// state, and bounding its retry at the glucose cursor puts the history below permanently out of
    /// reach. The next run resumes from the newest record that crawl published, even when nothing it
    /// published left a stored watermark — a device status with no APS, pump or uploader fields.
    /// </summary>
    [Theory]
    [InlineData(SyncDataType.Boluses, "treatments")]
    [InlineData(SyncDataType.DeviceStatus, "devicestatus")]
    [InlineData(SyncDataType.Activity, "activity")]
    public async Task SyncDataAsync_WhenNothingIsStored_CrawlsTheFullHistoryThenResumesFromWhereItReached(
        SyncDataType dataType, string collection)
    {
        var newest = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var handler = new CollectingHandler().Seed(collection, newest);
        var service = NewService(handler, watermarks: null);
        var request = new SyncRequest
        {
            From = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            To = null,
            DataTypes = [dataType],
        };

        await service.SyncDataAsync(request, NewConfig(), CancellationToken.None);
        handler.CrawlOf(collection).Should().NotContain("$gte");

        handler.Clear();
        await service.SyncDataAsync(request, NewConfig(), CancellationToken.None);
        handler.CrawlOf(collection).Should().Contain(LowerBound(newest - CatchUpOverlap));
    }

    /// <summary>
    /// A history limit bounds what an open-ended sync reads, the first crawl of a family with
    /// nothing stored included: without it that crawl reads the whole source whatever was chosen.
    /// </summary>
    [Theory]
    [InlineData(SyncDataType.Boluses, "treatments")]
    [InlineData(SyncDataType.DeviceStatus, "devicestatus")]
    [InlineData(SyncDataType.Activity, "activity")]
    public async Task SyncDataAsync_WithAHistoryLimitAndNothingStored_CrawlsNoFurtherBackThanTheLimit(
        SyncDataType dataType, string collection)
    {
        var handler = new CollectingHandler();
        var service = NewService(handler, watermarks: null);

        await service.SyncDataAsync(
            new SyncRequest { From = null, To = null, DataTypes = [dataType] },
            NewConfig(historyDays: 30),
            CancellationToken.None);

        LowerBoundOf(handler.CrawlOf(collection)).Should().BeCloseTo(
            DateTime.UtcNow.AddDays(-30) - WallClockEnvelope, TimeSpan.FromMinutes(1));
    }

    /// <summary>A resume point inside the limit is the nearer bound, so the limit leaves it alone.</summary>
    [Fact]
    public async Task SyncDataAsync_WithAHistoryLimit_StillResumesFromANewerWatermark()
    {
        var watermark = DateTime.UtcNow.AddDays(-2);
        var handler = new CollectingHandler();
        var service = NewService(handler, watermarks: watermark);

        await service.SyncDataAsync(
            new SyncRequest { From = null, To = null, DataTypes = [SyncDataType.DeviceStatus] },
            NewConfig(historyDays: 30),
            CancellationToken.None);

        handler.CrawlOf("devicestatus").Should().Contain(LowerBound(watermark - CatchUpOverlap));
    }

    /// <summary>
    /// The limit shortens the first backfill, not a catch-up. A connector that was off for longer
    /// than the limit resumes from its newest stored record, or the days between that record and
    /// the limit are skipped with nothing recording them as owed.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WithAHistoryLimit_CatchesUpFromAWatermarkOlderThanTheLimit()
    {
        var watermark = DateTime.UtcNow.AddDays(-45);
        var handler = new CollectingHandler();
        var service = NewService(handler, watermarks: watermark);

        await service.SyncDataAsync(
            new SyncRequest { From = null, To = null, DataTypes = [SyncDataType.DeviceStatus] },
            NewConfig(historyDays: 30),
            CancellationToken.None);

        handler.CrawlOf("devicestatus").Should().Contain(LowerBound(watermark - CatchUpOverlap));
    }

    /// <summary>A saved value outside the choices offered is no limit, not a date the calendar cannot hold.</summary>
    [Fact]
    public async Task SyncDataAsync_WithAHistoryLimitNoChoiceOffers_ReadsTheWholeHistory()
    {
        var handler = new CollectingHandler();
        var service = NewService(handler, watermarks: null);

        await service.SyncDataAsync(
            new SyncRequest { From = null, To = null, DataTypes = [SyncDataType.DeviceStatus] },
            NewConfig(historyDays: 3_000_000),
            CancellationToken.None);

        handler.CrawlOf("devicestatus").Should().NotContain("$gte");
    }

    /// <summary>A range named at both ends is a deliberate re-import, so it reaches past the limit.</summary>
    [Fact]
    public async Task SyncDataAsync_WithAHistoryLimit_AnswersAnExplicitRangeAsGiven()
    {
        var askedFrom = DateTime.UtcNow.AddDays(-400);
        var handler = new CollectingHandler();
        var service = NewService(handler, watermarks: null);

        await service.SyncDataAsync(
            new SyncRequest { From = askedFrom, To = askedFrom.AddDays(7), DataTypes = [SyncDataType.DeviceStatus] },
            NewConfig(historyDays: 30),
            CancellationToken.None);

        handler.CrawlOf("devicestatus").Should().Contain(LowerBound(askedFrom));
    }

    private static DateTime LowerBoundOf(string request)
    {
        const string marker = "find[created_at][$gte]=";
        var value = request[(request.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..].Split('&')[0];
        return DateTime.Parse(
            value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
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

    private static NightscoutConnectorConfiguration NewConfig(
        int historyDays = NightscoutConnectorConfiguration.WholeHistory) => new()
    {
        Url = "https://ns.example",
        ApiSecret = "secret",
        HistoryDays = historyDays,
    };

    private static NightscoutConnectorService NewService(
        CollectingHandler handler,
        DateTime? watermarks,
        DateTime? latestGlucose = null)
    {
        var glucose = new Mock<IGlucosePublisher>();
        glucose
            .Setup(p => p.GetLatestEntryTimestampAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(latestGlucose);

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

        device
            .Setup(p => p.PublishDeviceStatusAsync(
                It.IsAny<IEnumerable<DeviceStatus>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        treatments
            .Setup(p => p.PublishTreatmentsAsync(
                It.IsAny<IEnumerable<Treatment>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var metadata = new Mock<IMetadataPublisher>();
        metadata
            .Setup(p => p.GetLatestActivityTimestampAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(watermarks);
        metadata
            .Setup(p => p.PublishActivityAsync(
                It.IsAny<IEnumerable<Activity>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var publisher = new Mock<IConnectorPublisher>();
        publisher.Setup(p => p.IsAvailable).Returns(true);
        publisher.Setup(p => p.Glucose).Returns(glucose.Object);
        publisher.Setup(p => p.Treatments).Returns(treatments.Object);
        publisher.Setup(p => p.Device).Returns(device.Object);
        publisher.Setup(p => p.Metadata).Returns(metadata.Object);
        publisher.Setup(p => p.State).Returns(new InMemoryConnectorStateStore());

        var registration = new Mock<IConnectorRegistration<NightscoutConnectorConfiguration>>();
        registration.Setup(r => r.Defaults).Returns(new NightscoutConnectorConfiguration());

        return new NightscoutConnectorService(
            new HttpClient(handler),
            Mock.Of<IConnectorServerResolver<NightscoutConnectorConfiguration>>(),
            NullLogger<NightscoutConnectorService>.Instance,
            Mock.Of<IRetryDelayStrategy>(),
            registration.Object,
            new NightscoutLookups(),
            MockTenantAccessor.Create().Object,
            publisher.Object);
    }

    /// <summary>
    /// Records every request and answers each with an empty collection, so a crawl asks once per
    /// collection and the range it asked for is what the test reads. A seeded collection answers its
    /// first request with one record instead.
    /// </summary>
    private sealed class CollectingHandler : HttpMessageHandler
    {
        private readonly List<string> _requests = [];
        private readonly Dictionary<string, DateTime> _seeded = [];

        internal CollectingHandler Seed(string collection, DateTime createdAt)
        {
            _seeded[collection] = createdAt;
            return this;
        }

        internal void Clear() => _requests.Clear();

        /// <summary>
        /// The crawl request for <paramref name="collection"/>, as the source reads it. A treatments
        /// catch-up follows it with a re-read of the recent window, which is not the crawl.
        /// </summary>
        internal string CrawlOf(string collection) =>
            _requests.First(u => u.Contains($"/api/v1/{collection}.json", StringComparison.Ordinal));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // The bracket syntax Nightscout filters by is escaped on the wire.
            _requests.Add(Uri.UnescapeDataString(request.RequestUri!.ToString()));

            var body = "[]";
            foreach (var (collection, createdAt) in _seeded)
            {
                if (!request.RequestUri.AbsolutePath.EndsWith($"/{collection}.json", StringComparison.Ordinal))
                    continue;
                body = $$"""[{"_id":"seeded-{{collection}}","created_at":"{{createdAt:yyyy-MM-dd'T'HH:mm:ss.fff'Z'}}"}]""";
                _seeded.Remove(collection);
                break;
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
