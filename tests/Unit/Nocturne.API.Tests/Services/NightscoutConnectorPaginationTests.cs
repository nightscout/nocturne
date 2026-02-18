using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services;

public class NightscoutConnectorPaginationTests
{
    private const int MaxCount = 10;

    private static readonly DateTimeOffset BaseTime =
        new(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private static NightscoutConnectorService CreateService(
        HttpMessageHandler handler,
        NightscoutConnectorConfiguration? config = null,
        bool withPublisher = false)
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
            var mock = new Mock<IConnectorPublisher>();
            mock.Setup(p => p.IsAvailable).Returns(true);
            mock.Setup(p => p.PublishEntriesAsync(
                    It.IsAny<IEnumerable<Entry>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            mock.Setup(p => p.PublishTreatmentsAsync(
                    It.IsAny<IEnumerable<Treatment>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            publisher = mock.Object;
        }

        return new NightscoutConnectorService(
            httpClient,
            Mock.Of<ILogger<NightscoutConnectorService>>(),
            Mock.Of<IRetryDelayStrategy>(),
            Mock.Of<IRateLimitingStrategy>(),
            config,
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

    [Fact]
    public async Task FetchGlucoseData_SinglePage_ReturnsAllEntries()
    {
        // Arrange: fewer entries than MaxCount → no pagination needed
        var entries = CreateEntries(5, BaseTime);

        var handler = new SequentialMockHandler();
        handler.Enqueue(JsonResponse(entries));

        var service = CreateService(handler);

        // Act
        var result = (await service.FetchGlucoseDataAsync()).ToList();

        // Assert
        result.Should().HaveCount(5);
        handler.RequestUrls.Should().HaveCount(1);
    }

    [Fact]
    public async Task FetchGlucoseData_ExactlyMaxCount_MakesSecondRequest()
    {
        // Arrange: exactly MaxCount entries triggers a second request to check for more
        var entries = CreateEntries(MaxCount, BaseTime);

        var handler = new SequentialMockHandler();
        handler.Enqueue(JsonResponse(entries));
        handler.Enqueue(JsonResponse(Array.Empty<Entry>())); // second page empty

        var service = CreateService(handler);

        // Act
        var result = (await service.FetchGlucoseDataAsync()).ToList();

        // Assert
        result.Should().HaveCount(MaxCount);
        handler.RequestUrls.Should().HaveCount(2, "a full page should trigger a follow-up request");
    }

    [Fact]
    public async Task FetchGlucoseData_TwoFullPages_ReturnsAllEntries()
    {
        // Arrange: two full pages followed by a partial page
        var page1 = CreateEntries(MaxCount, BaseTime);
        var oldestPage1Ms = page1.Min(e => e.Mills);
        var page2Start = DateTimeOffset.FromUnixTimeMilliseconds(oldestPage1Ms).AddMilliseconds(-1);
        var page2 = CreateEntries(7, page2Start);

        var handler = new SequentialMockHandler();
        handler.Enqueue(JsonResponse(page1));
        handler.Enqueue(JsonResponse(page2));

        var service = CreateService(handler);

        // Act
        var result = (await service.FetchGlucoseDataAsync()).ToList();

        // Assert
        result.Should().HaveCount(MaxCount + 7);
        handler.RequestUrls.Should().HaveCount(2);
    }

    [Fact]
    public async Task FetchGlucoseData_ThreePages_ReturnsAllEntries()
    {
        // Arrange: three pages of data (regression: without pagination only the first page is returned)
        var page1 = CreateEntries(MaxCount, BaseTime);
        var oldestPage1Ms = page1.Min(e => e.Mills);
        var page2Start = DateTimeOffset.FromUnixTimeMilliseconds(oldestPage1Ms).AddMilliseconds(-1);
        var page2 = CreateEntries(MaxCount, page2Start);
        var oldestPage2Ms = page2.Min(e => e.Mills);
        var page3Start = DateTimeOffset.FromUnixTimeMilliseconds(oldestPage2Ms).AddMilliseconds(-1);
        var page3 = CreateEntries(3, page3Start);

        var handler = new SequentialMockHandler();
        handler.Enqueue(JsonResponse(page1));
        handler.Enqueue(JsonResponse(page2));
        handler.Enqueue(JsonResponse(page3));

        var service = CreateService(handler);

        // Act
        var result = (await service.FetchGlucoseDataAsync()).ToList();

        // Assert
        result.Should().HaveCount(MaxCount + MaxCount + 3,
            "pagination must retrieve entries across all pages, not just the first");
        handler.RequestUrls.Should().HaveCount(3);
    }

    [Fact]
    public async Task FetchGlucoseData_EmptyResponse_ReturnsEmpty()
    {
        var handler = new SequentialMockHandler();
        handler.Enqueue(JsonResponse(Array.Empty<Entry>()));

        var service = CreateService(handler);

        var result = (await service.FetchGlucoseDataAsync()).ToList();

        result.Should().BeEmpty();
        handler.RequestUrls.Should().HaveCount(1);
    }

    [Fact]
    public async Task FetchGlucoseData_PaginationUsesOldestEntryDate()
    {
        // Arrange: verify that the second request's $lte parameter corresponds to the
        // oldest entry's date minus 1ms from the first page
        var page1 = CreateEntries(MaxCount, BaseTime);
        var oldestMs = page1.Min(e => e.Mills);

        var handler = new SequentialMockHandler();
        handler.Enqueue(JsonResponse(page1));
        handler.Enqueue(JsonResponse(Array.Empty<Entry>()));

        var service = CreateService(handler);

        // Act
        await service.FetchGlucoseDataAsync();

        // Assert: second URL should contain $lte with oldestMs - 1
        var secondUrl = handler.RequestUrls[1];
        var expectedLte = (oldestMs - 1).ToString();
        secondUrl.Should().Contain($"find[date][$lte]={expectedLte}",
            "pagination should request entries older than the oldest seen entry");
    }

    [Fact]
    public async Task FetchGlucoseData_SetsDataSourceOnAllEntries()
    {
        var page1 = CreateEntries(MaxCount, BaseTime);
        var oldestPage1Ms = page1.Min(e => e.Mills);
        var page2Start = DateTimeOffset.FromUnixTimeMilliseconds(oldestPage1Ms).AddMilliseconds(-1);
        var page2 = CreateEntries(3, page2Start);

        var handler = new SequentialMockHandler();
        handler.Enqueue(JsonResponse(page1));
        handler.Enqueue(JsonResponse(page2));

        var service = CreateService(handler);

        var result = (await service.FetchGlucoseDataAsync()).ToList();

        result.Should().OnlyContain(e => !string.IsNullOrEmpty(e.DataSource),
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
            SyncTreatments = true,
        };
        var service = CreateService(handler, config, withPublisher: true);

        var request = new Nocturne.Connectors.Core.Models.SyncRequest
        {
            From = BaseTime.AddHours(-2).UtcDateTime,
            To = BaseTime.UtcDateTime,
            DataTypes = [Nocturne.Connectors.Core.Models.SyncDataType.Treatments],
        };

        var result = await service.SyncDataAsync(request, config, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ItemsSynced[Nocturne.Connectors.Core.Models.SyncDataType.Treatments].Should().Be(5);
    }

    [Fact]
    public async Task FetchTreatments_MultiplePages_ReturnsAll()
    {
        var page1 = CreateTreatments(MaxCount, BaseTime);
        var oldestDate = page1
            .Select(t => DateTime.Parse(t.CreatedAt!))
            .Min();
        var page2Start = new DateTimeOffset(DateTime.SpecifyKind(oldestDate, DateTimeKind.Utc))
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
            SyncTreatments = true,
        };
        var service = CreateService(handler, config, withPublisher: true);

        var request = new Nocturne.Connectors.Core.Models.SyncRequest
        {
            From = BaseTime.AddHours(-6).UtcDateTime,
            To = BaseTime.UtcDateTime,
            DataTypes = [Nocturne.Connectors.Core.Models.SyncDataType.Treatments],
        };

        var result = await service.SyncDataAsync(request, config, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ItemsSynced[Nocturne.Connectors.Core.Models.SyncDataType.Treatments]
            .Should().Be(MaxCount + 4,
                "pagination must retrieve treatments across all pages");
    }

    #endregion

    /// <summary>
    /// Mock handler that returns pre-queued responses in order and records request URLs.
    /// </summary>
    private class SequentialMockHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();

        public List<string> RequestUrls { get; } = [];

        public void Enqueue(HttpResponseMessage response) =>
            _responses.Enqueue(response);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUrls.Add(request.RequestUri?.PathAndQuery ?? "");

            if (_responses.Count == 0)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json"),
                });

            return Task.FromResult(_responses.Dequeue());
        }
    }
}
