using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services;
using Nocturne.Core.Models;
using Xunit;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.API.Tests.Services.Connectors;

/// <summary>
/// Covers the persisted backfill low-water mark: pages stream newest-first, so a crawl killed
/// mid-run (publish failure, fetch failure, process restart) used to strand everything older
/// than its last published page below the resume cursor forever. The mark carries "history
/// below X is still missing" across syncs; the next sync resumes the crawl below it and clears
/// it on reaching the source's beginning.
/// </summary>
public class NightscoutBackfillResumeTests
{
    private const int MaxCount = 10;

    private static readonly DateTimeOffset BaseTime =
        new(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>In-memory stand-in for the persisted marks on connector_configurations.</summary>
    private sealed class FakeMarkStore
    {
        public readonly Dictionary<string, DateTime> Marks = [];

        public Mock<IMetadataPublisher> BuildMetadataMock()
        {
            // Bound to the exact connector source: a wrong source string must read as "no
            // mark store" in these tests, not silently work.
            var mock = new Mock<IMetadataPublisher>();
            mock.Setup(m => m.GetBackfillLowWaterMarkAsync(
                    "nightscout-connector", It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string _, string collection, CancellationToken _) =>
                    Marks.TryGetValue(collection, out var mark) ? mark : null);
            mock.Setup(m => m.SetBackfillLowWaterMarkAsync(
                    "nightscout-connector", It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
                .Returns((string _, string collection, DateTime? mark, CancellationToken _) =>
                {
                    if (mark is null) Marks.Remove(collection);
                    else Marks[collection] = mark.Value;
                    return Task.CompletedTask;
                });
            return mock;
        }
    }

    private static Entry[] CreateEntries(int count, DateTimeOffset startTime) =>
        Enumerable.Range(0, count)
            .Select(i => new Entry
            {
                Mills = startTime.AddMinutes(-5 * i).ToUnixTimeMilliseconds(),
                Sgv = 100 + i,
                Type = "sgv",
            })
            .ToArray();

    private static HttpResponseMessage JsonResponse<T>(T data) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(data), System.Text.Encoding.UTF8, "application/json"),
        };

    private static (NightscoutConnectorService Service, SequentialMockHandler Handler, FakeMarkStore Marks, Mock<IGlucosePublisher> Glucose)
        CreateService(bool publishSucceeds = true)
    {
        var config = new NightscoutConnectorConfiguration
        {
            Url = "https://nightscout.example.com",
            ApiSecret = "test-secret",
            MaxCount = MaxCount,
            BatchSize = MaxCount, // one publish call per page
        };

        var handler = new SequentialMockHandler();
        var marks = new FakeMarkStore();

        var glucoseMock = new Mock<IGlucosePublisher>();
        glucoseMock.Setup(p => p.PublishEntriesAsync(
                It.IsAny<IEnumerable<Entry>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(publishSucceeds);

        var publisherMock = new Mock<IConnectorPublisher>();
        publisherMock.Setup(p => p.IsAvailable).Returns(true);
        publisherMock.Setup(p => p.Glucose).Returns(glucoseMock.Object);
        publisherMock.Setup(p => p.Metadata).Returns(marks.BuildMetadataMock().Object);

        var service = new NightscoutConnectorService(
            new HttpClient(handler) { BaseAddress = new Uri(config.Url) },
            new ConnectorServerResolver<NightscoutConnectorConfiguration>(null, null, null),
            Mock.Of<ILogger<NightscoutConnectorService>>(),
            Mock.Of<IRetryDelayStrategy>(),
            Mock.Of<IRateLimitingStrategy>(),
            new ConnectorRegistration<NightscoutConnectorConfiguration>(config, "Nightscout"),
            publisherMock.Object);

        return (service, handler, marks, glucoseMock);
    }

    private static Nocturne.Connectors.Core.Models.SyncRequest GlucoseRequest(DateTime? from, DateTime? to) => new()
    {
        From = from,
        To = to,
        DataTypes = [SyncDataType.Glucose],
    };

    private static NightscoutConnectorConfiguration Config() => new()
    {
        Url = "https://nightscout.example.com",
        ApiSecret = "test-secret",
        MaxCount = MaxCount,
        BatchSize = MaxCount,
    };

    [Fact]
    public async Task PublishFailure_RecordsTheMark_AtTheBottomOfTheLastPublishedPage()
    {
        var (service, handler, marks, glucose) = CreateService();

        var page1 = CreateEntries(MaxCount, BaseTime);
        var page1Bottom = DateTimeOffset.FromUnixTimeMilliseconds(page1.Min(e => e.Mills)).UtcDateTime;
        var page2 = CreateEntries(MaxCount, DateTimeOffset.FromUnixTimeMilliseconds(page1.Min(e => e.Mills)).AddMilliseconds(-1));

        handler.Enqueue(JsonResponse(Array.Empty<Entry>())); // auth check
        handler.Enqueue(JsonResponse(page1));
        handler.Enqueue(JsonResponse(page2));

        // Page 1 publishes, page 2 fails.
        var calls = 0;
        glucose.Setup(p => p.PublishEntriesAsync(
                It.IsAny<IEnumerable<Entry>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++calls == 1);

        var result = await service.SyncDataAsync(
            GlucoseRequest(from: null, to: BaseTime.UtcDateTime), Config(), CancellationToken.None);

        result.Success.Should().BeFalse();
        marks.Marks.Should().ContainKey("Glucose")
            .WhoseValue.Should().Be(page1Bottom, "the gap starts below the last page that stored");
    }

    [Fact]
    public async Task FetchFailure_MidCrawl_RecordsTheMark_AndFailsTheSync()
    {
        var (service, handler, marks, _) = CreateService();

        var page1 = CreateEntries(MaxCount, BaseTime);
        var page1Bottom = DateTimeOffset.FromUnixTimeMilliseconds(page1.Min(e => e.Mills)).UtcDateTime;

        handler.Enqueue(JsonResponse(Array.Empty<Entry>())); // auth check
        handler.Enqueue(JsonResponse(page1));
        // Enough failures to exhaust every retry attempt for the second page.
        for (var i = 0; i < 6; i++)
        {
            handler.Enqueue(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("boom"),
            });
        }

        var result = await service.SyncDataAsync(
            GlucoseRequest(from: null, to: BaseTime.UtcDateTime), Config(), CancellationToken.None);

        // Regression: a fetch failure used to read as a clean end-of-range — a "successful"
        // partial sync that would have wrongly cleared the mark.
        result.Success.Should().BeFalse();
        marks.Marks.Should().ContainKey("Glucose").WhoseValue.Should().Be(page1Bottom);
    }

    [Fact]
    public async Task CompletedFullCrawl_ClearsTheMark()
    {
        var (service, handler, marks, _) = CreateService();
        marks.Marks["Glucose"] = BaseTime.UtcDateTime.AddDays(-30); // stale mark from an earlier death

        handler.Enqueue(JsonResponse(Array.Empty<Entry>())); // auth check
        handler.Enqueue(JsonResponse(CreateEntries(3, BaseTime))); // short page = source's beginning

        var result = await service.SyncDataAsync(
            GlucoseRequest(from: null, to: BaseTime.UtcDateTime), Config(), CancellationToken.None);

        result.Success.Should().BeTrue();
        marks.Marks.Should().NotContainKey("Glucose", "the crawl reached the source's beginning");
    }

    [Fact]
    public async Task ExistingMark_TriggersAResumeCrawlBelowIt_AfterTheCatchUp()
    {
        var (service, handler, marks, _) = CreateService();
        var mark = BaseTime.UtcDateTime.AddDays(-10);
        marks.Marks["Glucose"] = mark;

        handler.Enqueue(JsonResponse(Array.Empty<Entry>())); // auth check
        handler.Enqueue(JsonResponse(CreateEntries(2, BaseTime))); // catch-up page (short)
        handler.Enqueue(JsonResponse(CreateEntries(3, new DateTimeOffset(mark).AddMinutes(-5)))); // resume page (short)

        var result = await service.SyncDataAsync(
            GlucoseRequest(from: BaseTime.UtcDateTime.AddHours(-1), to: BaseTime.UtcDateTime),
            Config(), CancellationToken.None);

        result.Success.Should().BeTrue();

        // The second data request is the resume crawl, bounded just below the mark.
        var dataRequests = handler.RequestUrls.Where(u => u.Contains($"count={MaxCount}")).ToList();
        dataRequests.Should().HaveCount(2);
        var expectedLte = new DateTimeOffset(mark).AddMilliseconds(-1).ToUnixTimeMilliseconds();
        dataRequests[1].Should().Contain($"find[date][$lte]={expectedLte}")
            .And.NotContain("$gte", "the resume crawl reaches for the source's beginning");

        // Both regions counted; the completed resume clears the mark.
        result.ItemsSynced[SyncDataType.Glucose].Should().Be(5);
        marks.Marks.Should().NotContainKey("Glucose");
    }

    [Fact]
    public async Task FailedPrimaryCrawl_SkipsTheResume_AndPreservesTheMark()
    {
        // A store that is failing right now must not be hammered with the deep history too;
        // the mark survives untouched for the next healthy cycle.
        var (service, handler, marks, glucose) = CreateService();
        var mark = BaseTime.UtcDateTime.AddDays(-10);
        marks.Marks["Glucose"] = mark;

        handler.Enqueue(JsonResponse(Array.Empty<Entry>())); // auth check
        handler.Enqueue(JsonResponse(CreateEntries(2, BaseTime))); // catch-up page

        glucose.Setup(p => p.PublishEntriesAsync(
                It.IsAny<IEnumerable<Entry>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await service.SyncDataAsync(
            GlucoseRequest(from: BaseTime.UtcDateTime.AddHours(-1), to: BaseTime.UtcDateTime),
            Config(), CancellationToken.None);

        result.Success.Should().BeFalse();
        handler.RequestUrls.Count(u => u.Contains($"count={MaxCount}")).Should().Be(1,
            "no resume crawl may run while the store is failing");
        marks.Marks["Glucose"].Should().Be(mark);
    }

    [Fact]
    public async Task BoundedCrawlFailure_NeverLowersADeeperMark()
    {
        // An admin re-pull of an old window failing must not pull an existing higher mark
        // down: the higher mark's unbounded resume already covers everything below it.
        var (service, handler, marks, glucose) = CreateService();
        var highMark = BaseTime.UtcDateTime;
        marks.Marks["Glucose"] = highMark;

        var windowTop = new DateTimeOffset(BaseTime.UtcDateTime.AddDays(-50));
        var page1 = CreateEntries(MaxCount, windowTop);
        var page2 = CreateEntries(MaxCount, DateTimeOffset.FromUnixTimeMilliseconds(page1.Min(e => e.Mills)).AddMilliseconds(-1));

        handler.Enqueue(JsonResponse(Array.Empty<Entry>())); // auth check
        handler.Enqueue(JsonResponse(page1));
        handler.Enqueue(JsonResponse(page2));

        var calls = 0;
        glucose.Setup(p => p.PublishEntriesAsync(
                It.IsAny<IEnumerable<Entry>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++calls == 1); // page 1 stores, page 2 fails

        var result = await service.SyncDataAsync(
            GlucoseRequest(from: windowTop.UtcDateTime.AddDays(-10), to: windowTop.UtcDateTime),
            Config(), CancellationToken.None);

        result.Success.Should().BeFalse();
        marks.Marks["Glucose"].Should().Be(highMark);
    }

    [Fact]
    public async Task NoMark_MeansNoResumeCrawl()
    {
        var (service, handler, marks, _) = CreateService();

        handler.Enqueue(JsonResponse(Array.Empty<Entry>())); // auth check
        handler.Enqueue(JsonResponse(CreateEntries(2, BaseTime)));

        var result = await service.SyncDataAsync(
            GlucoseRequest(from: BaseTime.UtcDateTime.AddHours(-1), to: BaseTime.UtcDateTime),
            Config(), CancellationToken.None);

        result.Success.Should().BeTrue();
        handler.RequestUrls.Count(u => u.Contains($"count={MaxCount}")).Should().Be(1);
        marks.Marks.Should().BeEmpty();
    }
}
