using System.Globalization;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services;
using Nocturne.Core.Models;
using Xunit;
using Nocturne.Core.Contracts.V4;
using SyncDataType = Nocturne.Connectors.Core.Models.SyncDataType;
using SyncRequest = Nocturne.Connectors.Core.Models.SyncRequest;

namespace Nocturne.API.Tests.Services.Connectors;

public class NightscoutConnectorPaginationTests
{
    private const int MaxCount = 10;

    private static readonly DateTimeOffset BaseTime =
        new(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private static NightscoutConnectorService CreateService(
        HttpMessageHandler handler,
        NightscoutConnectorConfiguration? config = null,
        bool withPublisher = false,
        List<Treatment>? publishedTreatments = null,
        List<Entry>? publishedEntries = null)
    {
        config ??= new NightscoutConnectorConfiguration
        {
            Url = "https://nightscout.example.com",
            ApiSecret = "test-secret",
            MaxCount = MaxCount,
        };

        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(config.Url),
        };

        IConnectorPublisher? publisher = null;
        if (withPublisher)
        {
            var glucoseMock = new Mock<IGlucosePublisher>();
            glucoseMock.Setup(p => p.PublishEntriesAsync(
                    It.IsAny<IEnumerable<Entry>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
                .Callback<IEnumerable<Entry>, string, WriteOrigin, CancellationToken>(
                    (batch, _, _, _) => publishedEntries?.AddRange(batch))
                .ReturnsAsync(true);

            var treatmentMock = new Mock<ITreatmentPublisher>();
            treatmentMock.Setup(p => p.PublishTreatmentsAsync(
                    It.IsAny<IEnumerable<Treatment>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
                .Callback<IEnumerable<Treatment>, string, WriteOrigin, CancellationToken>(
                    (batch, _, _, _) => publishedTreatments?.AddRange(batch))
                .ReturnsAsync(true);

            var mock = new Mock<IConnectorPublisher>();
            mock.Setup(p => p.IsAvailable).Returns(true);
            mock.Setup(p => p.Glucose).Returns(glucoseMock.Object);
            mock.Setup(p => p.Treatments).Returns(treatmentMock.Object);
            mock.Setup(p => p.Metadata).Returns(Mock.Of<IMetadataPublisher>());
            publisher = mock.Object;
        }

        return new NightscoutConnectorService(
            httpClient,
            new ConnectorServerResolver<NightscoutConnectorConfiguration>(null, null, null),
            Mock.Of<ILogger<NightscoutConnectorService>>(),
            Mock.Of<IRetryDelayStrategy>(),
            Mock.Of<IRateLimitingStrategy>(),
            new ConnectorRegistration<NightscoutConnectorConfiguration>(config, "Nightscout"),
            publisher);
    }

    private static Entry[] CreateEntries(int count, DateTimeOffset startTime)
    {
        // Entries ordered newest-first (like Nightscout returns them),
        // each 5 minutes apart going backwards from startTime.
        return Enumerable.Range(0, count)
            .Select(i =>
            {
                var ms = startTime.AddMinutes(-5 * i).ToUnixTimeMilliseconds();
                return new Entry { Mills = ms, Sgv = 100 + i, Type = "sgv" };
            })
            .ToArray();
    }

    private static Treatment[] CreateTreatments(int count, DateTimeOffset startTime)
    {
        return Enumerable.Range(0, count)
            .Select(i =>
            {
                var time = startTime.AddMinutes(-5 * i);
                return new Treatment
                {
                    Created_at = time.UtcDateTime.ToString("o"),
                    EventType = "Correction Bolus",
                    Insulin = 1.0 + i,
                };
            })
            .ToArray();
    }

    private static HttpResponseMessage JsonResponse<T>(T data) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(data),
                System.Text.Encoding.UTF8,
                "application/json"),
        };

    #region Glucose pagination tests

    // An open-ended glucose sync (no From, no To) is the shape the crawl anchors to "now",
    // so these drive the paging loop over the same bounds a background catch-up uses. The
    // connector probes /entries.json?count=1 to authenticate before any page is fetched, so
    // every handler here answers that probe first and request index 0 is never a data page.
    private static SyncRequest OpenEndedGlucoseSync() =>
        new()
        {
            From = null,
            To = null,
            DataTypes = [SyncDataType.Glucose],
        };

    private static NightscoutConnectorConfiguration GlucoseConfig(int maxCount = MaxCount) =>
        new()
        {
            Url = "https://nightscout.example.com",
            ApiSecret = "test-secret",
            MaxCount = maxCount,
        };

    [Fact]
    public async Task SyncGlucose_SinglePage_SyncsAllEntries()
    {
        // Arrange: fewer entries than MaxCount → no pagination needed
        var handler = new SequentialMockHandler();
        handler.Enqueue(JsonResponse(Array.Empty<Entry>())); // auth check
        handler.Enqueue(JsonResponse(CreateEntries(5, BaseTime)));

        var config = GlucoseConfig();
        var service = CreateService(handler, config, withPublisher: true);

        // Act
        var result = await service.SyncDataAsync(OpenEndedGlucoseSync(), config, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.ItemsSynced[SyncDataType.Glucose].Should().Be(5);
        handler.RequestUrls.Should().HaveCount(2);
    }

    [Fact]
    public async Task SyncGlucose_ExactlyMaxCount_MakesSecondRequest()
    {
        // Arrange: exactly MaxCount entries triggers a second request to check for more
        var handler = new SequentialMockHandler();
        handler.Enqueue(JsonResponse(Array.Empty<Entry>())); // auth check
        handler.Enqueue(JsonResponse(CreateEntries(MaxCount, BaseTime)));
        handler.Enqueue(JsonResponse(Array.Empty<Entry>())); // second page empty

        var config = GlucoseConfig();
        var service = CreateService(handler, config, withPublisher: true);

        // Act
        var result = await service.SyncDataAsync(OpenEndedGlucoseSync(), config, CancellationToken.None);

        // Assert
        result.ItemsSynced[SyncDataType.Glucose].Should().Be(MaxCount);
        handler.RequestUrls.Should().HaveCount(3, "a full page should trigger a follow-up request");
    }

    [Fact]
    public async Task SyncGlucose_TwoFullPages_SyncsAllEntries()
    {
        // Arrange: two full pages followed by a partial page
        var page1 = CreateEntries(MaxCount, BaseTime);
        var oldestPage1Ms = page1.Min(e => e.Mills);
        var page2Start = DateTimeOffset.FromUnixTimeMilliseconds(oldestPage1Ms).AddMilliseconds(-1);
        var page2 = CreateEntries(7, page2Start);

        var handler = new SequentialMockHandler();
        handler.Enqueue(JsonResponse(Array.Empty<Entry>())); // auth check
        handler.Enqueue(JsonResponse(page1));
        handler.Enqueue(JsonResponse(page2));

        var config = GlucoseConfig();
        var service = CreateService(handler, config, withPublisher: true);

        // Act
        var result = await service.SyncDataAsync(OpenEndedGlucoseSync(), config, CancellationToken.None);

        // Assert
        result.ItemsSynced[SyncDataType.Glucose].Should().Be(MaxCount + 7);
        handler.RequestUrls.Should().HaveCount(3);
    }

    [Fact]
    public async Task SyncGlucose_ThreePages_SyncsAllEntries()
    {
        // Arrange: three pages of data (regression: without pagination only the first page is synced)
        var page1 = CreateEntries(MaxCount, BaseTime);
        var oldestPage1Ms = page1.Min(e => e.Mills);
        var page2Start = DateTimeOffset.FromUnixTimeMilliseconds(oldestPage1Ms).AddMilliseconds(-1);
        var page2 = CreateEntries(MaxCount, page2Start);
        var oldestPage2Ms = page2.Min(e => e.Mills);
        var page3Start = DateTimeOffset.FromUnixTimeMilliseconds(oldestPage2Ms).AddMilliseconds(-1);
        var page3 = CreateEntries(3, page3Start);

        var handler = new SequentialMockHandler();
        handler.Enqueue(JsonResponse(Array.Empty<Entry>())); // auth check
        handler.Enqueue(JsonResponse(page1));
        handler.Enqueue(JsonResponse(page2));
        handler.Enqueue(JsonResponse(page3));

        var config = GlucoseConfig();
        var service = CreateService(handler, config, withPublisher: true);

        // Act
        var result = await service.SyncDataAsync(OpenEndedGlucoseSync(), config, CancellationToken.None);

        // Assert
        result.ItemsSynced[SyncDataType.Glucose].Should().Be(MaxCount + MaxCount + 3,
            "pagination must retrieve entries across all pages, not just the first");
        handler.RequestUrls.Should().HaveCount(4);
    }

    [Fact]
    public async Task SyncGlucose_EmptyResponse_SyncsNothing()
    {
        var handler = new SequentialMockHandler();
        handler.Enqueue(JsonResponse(Array.Empty<Entry>())); // auth check
        handler.Enqueue(JsonResponse(Array.Empty<Entry>()));

        var config = GlucoseConfig();
        var service = CreateService(handler, config, withPublisher: true);

        var result = await service.SyncDataAsync(OpenEndedGlucoseSync(), config, CancellationToken.None);

        result.ItemsSynced[SyncDataType.Glucose].Should().Be(0);
        handler.RequestUrls.Should().HaveCount(2);
    }

    [Fact]
    public async Task SyncGlucose_PaginationUsesOldestEntryDate()
    {
        // Arrange: verify that the second page's $lte parameter corresponds to the
        // oldest entry's date minus 1ms from the first page
        var page1 = CreateEntries(MaxCount, BaseTime);
        var oldestMs = page1.Min(e => e.Mills);

        var handler = new SequentialMockHandler();
        handler.Enqueue(JsonResponse(Array.Empty<Entry>())); // auth check
        handler.Enqueue(JsonResponse(page1));
        handler.Enqueue(JsonResponse(Array.Empty<Entry>()));

        var config = GlucoseConfig();
        var service = CreateService(handler, config, withPublisher: true);

        // Act
        await service.SyncDataAsync(OpenEndedGlucoseSync(), config, CancellationToken.None);

        // Assert: the second page's URL should contain $lte with oldestMs - 1
        var secondPageUrl = handler.RequestUrls[2];
        var expectedLte = (oldestMs - 1).ToString();
        secondPageUrl.Should().Contain($"find[date][$lte]={expectedLte}",
            "pagination should request entries older than the oldest seen entry");
    }

    [Fact]
    public async Task SyncGlucose_UnboundedFetch_AnchorsFirstPageToNow()
    {
        // Regression: a query with no find[date] bound at all makes Nightscout apply an
        // implicit recency window (~4 days), so an unbounded full-history backfill got a
        // truncated first page that the short-page check read as end-of-history.
        var handler = new SequentialMockHandler();
        handler.Enqueue(JsonResponse(Array.Empty<Entry>())); // auth check
        handler.Enqueue(JsonResponse(CreateEntries(5, BaseTime)));

        var config = GlucoseConfig();
        var service = CreateService(handler, config, withPublisher: true);

        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await service.SyncDataAsync(OpenEndedGlucoseSync(), config, CancellationToken.None);
        var after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var firstPageUrl = handler.RequestUrls[1];
        firstPageUrl.Should().Contain("find[date][$lte]=",
            "an unbounded fetch must carry an explicit upper bound so the source cannot window it");

        var lte = long.Parse(System.Text.RegularExpressions.Regex
            .Match(firstPageUrl, @"find\[date\]\[\$lte\]=(\d+)").Groups[1].Value);
        lte.Should().BeInRange(before, after, "the anchor should be the moment of the fetch");
    }

    [Fact]
    public async Task SyncGlucose_CatchUpWithSince_DoesNotAddUpperBound()
    {
        // A catch-up fetch (From set, no upper bound) must stay unbounded at the top so
        // future-dated readings from a fast device clock are still picked up immediately.
        var handler = new SequentialMockHandler();
        handler.Enqueue(JsonResponse(Array.Empty<Entry>())); // auth check
        handler.Enqueue(JsonResponse(CreateEntries(3, BaseTime)));

        var config = GlucoseConfig();
        var service = CreateService(handler, config, withPublisher: true);

        var request = new SyncRequest
        {
            From = BaseTime.AddHours(-1).UtcDateTime,
            To = null,
            DataTypes = [SyncDataType.Glucose],
        };

        await service.SyncDataAsync(request, config, CancellationToken.None);

        handler.RequestUrls[1].Should().Contain("find[date][$gte]=")
            .And.NotContain("find[date][$lte]=");
    }

    [Fact]
    public async Task SyncGlucose_SetsDataSourceOnAllEntries()
    {
        var page1 = CreateEntries(MaxCount, BaseTime);
        var oldestPage1Ms = page1.Min(e => e.Mills);
        var page2Start = DateTimeOffset.FromUnixTimeMilliseconds(oldestPage1Ms).AddMilliseconds(-1);
        var page2 = CreateEntries(3, page2Start);

        var handler = new SequentialMockHandler();
        handler.Enqueue(JsonResponse(Array.Empty<Entry>())); // auth check
        handler.Enqueue(JsonResponse(page1));
        handler.Enqueue(JsonResponse(page2));

        var publishedEntries = new List<Entry>();
        var config = GlucoseConfig();
        var service = CreateService(handler, config, withPublisher: true, publishedEntries: publishedEntries);

        await service.SyncDataAsync(OpenEndedGlucoseSync(), config, CancellationToken.None);

        publishedEntries.Should().HaveCount(MaxCount + 3);
        publishedEntries.Should().OnlyContain(e => !string.IsNullOrEmpty(e.DataSource),
            "every entry across all pages should have DataSource set");
    }

    #endregion

    #region Treatment pagination tests

    [Fact]
    public async Task FetchTreatments_SinglePage_ReturnsAll()
    {
        var treatments = CreateTreatments(5, BaseTime);

        // Auth response, then treatments page
        var handler = new SequentialMockHandler();
        handler.Enqueue(JsonResponse(Array.Empty<Entry>())); // auth check
        handler.Enqueue(JsonResponse(treatments));

        var config = new NightscoutConnectorConfiguration
        {
            Url = "https://nightscout.example.com",
            ApiSecret = "test-secret",
            MaxCount = MaxCount,
        };
        var service = CreateService(handler, config, withPublisher: true);

        var request = new Nocturne.Connectors.Core.Models.SyncRequest
        {
            From = BaseTime.AddHours(-2).UtcDateTime,
            To = BaseTime.UtcDateTime,
            DataTypes = [Nocturne.Connectors.Core.Models.SyncDataType.Boluses],
        };

        var result = await service.SyncDataAsync(request, config, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ItemsSynced[Nocturne.Connectors.Core.Models.SyncDataType.Boluses].Should().Be(5);
    }

    [Fact]
    public async Task FetchTreatments_MultiplePages_ReturnsAll()
    {
        var page1 = CreateTreatments(MaxCount, BaseTime);
        var page2Start = page1
            .Select(t => DateTimeOffset.Parse(t.CreatedAt!, CultureInfo.InvariantCulture))
            .Min()
            .AddMilliseconds(-1);
        var page2 = CreateTreatments(4, page2Start);

        var handler = new SequentialMockHandler();
        handler.Enqueue(JsonResponse(Array.Empty<Entry>())); // auth check
        handler.Enqueue(JsonResponse(page1));
        handler.Enqueue(JsonResponse(page2));

        var config = new NightscoutConnectorConfiguration
        {
            Url = "https://nightscout.example.com",
            ApiSecret = "test-secret",
            MaxCount = MaxCount,
        };
        var service = CreateService(handler, config, withPublisher: true);

        var request = new Nocturne.Connectors.Core.Models.SyncRequest
        {
            From = BaseTime.AddHours(-6).UtcDateTime,
            To = BaseTime.UtcDateTime,
            DataTypes = [Nocturne.Connectors.Core.Models.SyncDataType.Boluses],
        };

        var result = await service.SyncDataAsync(request, config, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ItemsSynced[Nocturne.Connectors.Core.Models.SyncDataType.Boluses]
            .Should().Be(MaxCount + 4,
                "pagination must retrieve treatments across all pages");
    }

    [Fact]
    public async Task SyncData_PublishesEachPageBeforeFetchingTheNext()
    {
        // Regression: the sync used to fetch the ENTIRE range into one list before
        // publishing anything — a multi-year backfill of a high-volume collection OOMed
        // the process mid-publish. Streaming means each page is published before the next
        // is fetched, and a failing publish stops the crawl — the persisted low-water mark
        // is what carries the un-crawled remainder to the next sync.
        var page1 = CreateEntries(MaxCount, BaseTime);
        var oldestPage1Ms = page1.Min(e => e.Mills);
        var page2Start = DateTimeOffset.FromUnixTimeMilliseconds(oldestPage1Ms).AddMilliseconds(-1);
        var page2 = CreateEntries(3, page2Start);

        var handler = new SequentialMockHandler();
        handler.Enqueue(JsonResponse(Array.Empty<Entry>())); // auth check
        handler.Enqueue(JsonResponse(page1));
        handler.Enqueue(JsonResponse(page2));

        var config = new NightscoutConnectorConfiguration
        {
            Url = "https://nightscout.example.com",
            ApiSecret = "test-secret",
            MaxCount = MaxCount,
            BatchSize = MaxCount, // one publish call per page
        };

        // Snapshot how many HTTP requests had been made at the moment of each publish call —
        // this is what pins the interleaving: the first page must be published while only the
        // auth probe and page 1 have been fetched.
        var requestsAtPublish = new List<int>();
        var glucoseMock = new Mock<IGlucosePublisher>();
        glucoseMock.Setup(p => p.PublishEntriesAsync(
                It.IsAny<IEnumerable<Entry>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback(() => requestsAtPublish.Add(handler.RequestUrls.Count))
            .ReturnsAsync(false); // every publish fails

        var publisherMock = new Mock<IConnectorPublisher>();
        publisherMock.Setup(p => p.IsAvailable).Returns(true);
        publisherMock.Setup(p => p.Glucose).Returns(glucoseMock.Object);
        publisherMock.Setup(p => p.Metadata).Returns(Mock.Of<IMetadataPublisher>());

        var service = new NightscoutConnectorService(
            new HttpClient(handler) { BaseAddress = new Uri(config.Url) },
            new ConnectorServerResolver<NightscoutConnectorConfiguration>(null, null, null),
            Mock.Of<ILogger<NightscoutConnectorService>>(),
            Mock.Of<IRetryDelayStrategy>(),
            Mock.Of<IRateLimitingStrategy>(),
            new ConnectorRegistration<NightscoutConnectorConfiguration>(config, "Nightscout"),
            publisherMock.Object);

        var request = new Nocturne.Connectors.Core.Models.SyncRequest
        {
            From = BaseTime.AddDays(-30).UtcDateTime,
            To = BaseTime.UtcDateTime,
            DataTypes = [Nocturne.Connectors.Core.Models.SyncDataType.Glucose],
        };

        var result = await service.SyncDataAsync(request, config, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Errors.Should().Contain("Glucose publish failed");
        // The first page was published before page 2 was fetched (auth + page 1 = 2 requests)…
        requestsAtPublish[0].Should().Be(2,
            "the first page must be published before the next page is fetched");
        // …and its failure stopped the crawl there.
        requestsAtPublish.Should().HaveCount(1);
        handler.RequestUrls.Count(u => u.Contains("entries.json")).Should().Be(2);
    }

    [Fact]
    public async Task FetchTreatments_OpenEndedInitialSync_AnchorsFirstPageToNow()
    {
        // Same implicit-window regression as glucose: an open-ended first-ever treatments
        // sync (no watermark, no floor) must not issue a fully unbounded query.
        var handler = new SequentialMockHandler();
        handler.Enqueue(JsonResponse(Array.Empty<Entry>())); // auth check
        handler.Enqueue(JsonResponse(CreateTreatments(3, BaseTime)));

        var config = new NightscoutConnectorConfiguration
        {
            Url = "https://nightscout.example.com",
            ApiSecret = "test-secret",
            MaxCount = MaxCount,
        };
        var service = CreateService(handler, config, withPublisher: true);

        var request = new Nocturne.Connectors.Core.Models.SyncRequest
        {
            From = null,
            To = null,
            DataTypes = [Nocturne.Connectors.Core.Models.SyncDataType.Boluses],
        };

        var result = await service.SyncDataAsync(request, config, CancellationToken.None);

        result.Success.Should().BeTrue();
        var treatmentsUrl = handler.RequestUrls.First(u => u.Contains("treatments.json"));
        treatmentsUrl.Should().Contain("find[created_at][$lte]=",
            "an unbounded fetch must carry an explicit upper bound so the source cannot window it");
    }

    #endregion

    #region Offset-formatted created_at

    private const string InWindowOffset = "2025-06-15T21:30:00+10:00";      // 11:30Z
    private const string AfterWindowOffset = "2025-06-15T23:00:00+10:00";   // 13:00Z
    private const string InWindowUtc = "2025-06-15T11:00:00.000Z";

    private static Treatment OffsetTreatment(string createdAt) =>
        new() { Created_at = createdAt, EventType = "Correction Bolus", Insulin = 1.0 };

    private static async Task<(Nocturne.Connectors.Core.Models.SyncResult Result, List<Treatment> Published)> SyncTreatmentsAsync(
        LegacyTreatmentsHandler handler,
        DateTimeOffset? from,
        DateTimeOffset? to)
    {
        var config = new NightscoutConnectorConfiguration
        {
            Url = "https://nightscout.example.com",
            ApiSecret = "test-secret",
            MaxCount = MaxCount,
        };

        var published = new List<Treatment>();
        var service = CreateService(handler, config, withPublisher: true, publishedTreatments: published);

        var request = new Nocturne.Connectors.Core.Models.SyncRequest
        {
            From = from?.UtcDateTime,
            To = to?.UtcDateTime,
            DataTypes = [Nocturne.Connectors.Core.Models.SyncDataType.Boluses],
        };

        return (await service.SyncDataAsync(request, config, CancellationToken.None), published);
    }

    [Fact]
    public async Task Treatments_OffsetFormattedCreatedAtInsideWindow_IsImported()
    {
        // Legacy Nightscout compares created_at as a string, so a record an old uploader wrote
        // with a local offset sorts by its wall clock: "2025-06-15T21:30:00+10:00" is the same
        // instant as 11:30Z but sorts ABOVE a 12:00Z upper bound, and used to fall outside every
        // page of the crawl.
        var handler = new LegacyTreatmentsHandler(
            OffsetTreatment(InWindowOffset),
            OffsetTreatment(InWindowUtc));

        var (result, published) = await SyncTreatmentsAsync(handler, BaseTime.AddHours(-2), BaseTime);

        result.Success.Should().BeTrue();
        handler.ServedCreatedAt.Should().Contain(InWindowOffset,
            "the requested window must be wide enough for the source's string comparison to return it");
        published.Select(t => t.CreatedAt).Should().BeEquivalentTo([InWindowOffset, InWindowUtc]);
    }

    [Fact]
    public async Task Treatments_OffsetFormattedCreatedAtOutsideWindow_IsFetchedButDropped()
    {
        // The widened request pulls in records either side of the true window; the client-side
        // filter on the parsed instant is what keeps the window honest.
        var handler = new LegacyTreatmentsHandler(
            OffsetTreatment(AfterWindowOffset),
            OffsetTreatment(InWindowUtc));

        var (result, published) = await SyncTreatmentsAsync(handler, BaseTime.AddHours(-2), BaseTime);

        result.Success.Should().BeTrue();
        handler.ServedCreatedAt.Should().Contain(AfterWindowOffset,
            "the widening must actually return the out-of-window record, or the filter is untested");
        published.Select(t => t.CreatedAt).Should().Equal(InWindowUtc);
    }

    [Fact]
    public async Task Treatments_OffsetFormattedCreatedAtAcrossPages_PaginatesToTheEnd()
    {
        // The page cursor has to step in the source's wall-clock space: a cursor in instant space
        // steps the next bound past records the source has not served yet, and the crawl ends
        // after the first page.
        var stored = Enumerable.Range(0, MaxCount + 4)
            .Select(i => OffsetTreatment(
                BaseTime.AddMinutes(-5 * i).AddHours(10).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff") + "+10:00"))
            .ToArray();

        var handler = new LegacyTreatmentsHandler(stored);

        var (result, published) = await SyncTreatmentsAsync(handler, BaseTime.AddHours(-6), BaseTime);

        result.Success.Should().BeTrue();
        published.Select(t => t.CreatedAt)
            .Should().BeEquivalentTo(stored.Select(t => t.CreatedAt),
                "every offset-formatted record in the window must be crawled, not just the first page");
        handler.RequestUrls.Count(u => u.Contains("treatments.json")).Should().Be(2);
    }

    [Fact]
    public async Task Treatments_UnboundedBackfill_DoesNotImportFutureDatedRecords()
    {
        // An unbounded backfill anchors its upper bound to "now", and the widening lifts the
        // fetch bound 14h past that. The client-side filter has to keep the original anchor as
        // its ceiling, or a device with a fast clock starts landing in "latest" displays.
        var future = DateTimeOffset.UtcNow.AddHours(3).UtcDateTime.ToString("o");
        var past = DateTimeOffset.UtcNow.AddHours(-1).UtcDateTime.ToString("o");

        var handler = new LegacyTreatmentsHandler(
            OffsetTreatment(future),
            OffsetTreatment(past));

        var (result, published) = await SyncTreatmentsAsync(handler, from: null, to: null);

        result.Success.Should().BeTrue();
        handler.ServedCreatedAt.Should().Contain(future,
            "the widened fetch bound reaches 14h past the anchor, so the source does return it");
        published.Select(t => t.CreatedAt).Should().Equal(past);
    }

    /// <summary>
    /// Stands in for legacy Nightscout's treatments collection, where created_at is a plain
    /// string: the find bounds and the newest-first sort are ordinal string comparisons.
    /// </summary>
    private sealed class LegacyTreatmentsHandler(params Treatment[] stored) : HttpMessageHandler
    {
        public List<string> RequestUrls { get; } = [];
        public List<string> ServedCreatedAt { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = Uri.UnescapeDataString(request.RequestUri?.PathAndQuery ?? "");
            RequestUrls.Add(url);

            if (!url.Contains("treatments.json"))
                return Task.FromResult(JsonResponse(Array.Empty<Treatment>()));

            var gte = Bound(url, "gte");
            var lte = Bound(url, "lte");
            var count = int.Parse(System.Text.RegularExpressions.Regex.Match(url, @"count=(\d+)").Groups[1].Value);

            var page = stored
                .Where(t => (gte is null || string.CompareOrdinal(t.CreatedAt, gte) >= 0)
                            && (lte is null || string.CompareOrdinal(t.CreatedAt, lte) <= 0))
                .OrderByDescending(t => t.CreatedAt, StringComparer.Ordinal)
                .Take(count)
                .ToArray();

            ServedCreatedAt.AddRange(page.Select(t => t.CreatedAt!));
            return Task.FromResult(JsonResponse(page));
        }

        private static string? Bound(string url, string op)
        {
            var match = System.Text.RegularExpressions.Regex.Match(url, $@"\[\${op}\]=([^&]+)");
            return match.Success ? match.Groups[1].Value : null;
        }
    }

    #endregion
}
