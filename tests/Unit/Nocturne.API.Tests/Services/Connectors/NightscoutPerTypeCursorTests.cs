using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
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
/// Verifies that each data type resolves its own catch-up cursor on open-ended (background)
/// syncs, rather than reusing the glucose-derived <see cref="SyncRequest.From"/>. Regression
/// guard for the bug where treatments (boluses/carbs) were permanently stranded behind the
/// glucose cursor: once glucose was current, treatments only ever fetched the last few minutes
/// and historical treatments never backfilled.
/// </summary>
public class NightscoutPerTypeCursorTests
{
    private const int MaxCount = 100;

    private static NightscoutConnectorService CreateService(
        HttpMessageHandler handler,
        NightscoutConnectorConfiguration config,
        DateTime? latestEntry = null,
        DateTime? latestTreatment = null,
        DateTime? latestDeviceStatus = null,
        DateTime? latestActivity = null)
    {
        var glucose = new Mock<IGlucosePublisher>();
        glucose.Setup(p => p.GetLatestEntryTimestampAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(latestEntry);
        glucose.Setup(p => p.PublishEntriesAsync(It.IsAny<IEnumerable<Entry>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var treatments = new Mock<ITreatmentPublisher>();
        treatments.Setup(p => p.GetLatestTreatmentTimestampAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(latestTreatment);
        treatments.Setup(p => p.PublishTreatmentsAsync(It.IsAny<IEnumerable<Treatment>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var device = new Mock<IDevicePublisher>();
        device.Setup(p => p.GetLatestDeviceStatusTimestampAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(latestDeviceStatus);
        device.Setup(p => p.PublishDeviceStatusAsync(It.IsAny<IEnumerable<DeviceStatus>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var metadata = new Mock<IMetadataPublisher>();
        metadata.Setup(p => p.GetLatestActivityTimestampAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(latestActivity);

        var publisher = new Mock<IConnectorPublisher>();
        publisher.Setup(p => p.IsAvailable).Returns(true);
        publisher.Setup(p => p.Glucose).Returns(glucose.Object);
        publisher.Setup(p => p.Treatments).Returns(treatments.Object);
        publisher.Setup(p => p.Device).Returns(device.Object);
        publisher.Setup(p => p.Metadata).Returns(metadata.Object);

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(config.Url) };

        return new NightscoutConnectorService(
            httpClient,
            new ConnectorServerResolver<NightscoutConnectorConfiguration>(null, null, null),
            Mock.Of<ILogger<NightscoutConnectorService>>(),
            Mock.Of<IRetryDelayStrategy>(),
            Mock.Of<IRateLimitingStrategy>(),
            new ConnectorRegistration<NightscoutConnectorConfiguration>(config, "Nightscout"),
            publisher.Object);
    }

    private static NightscoutConnectorConfiguration Config() => new()
    {
        Url = "https://nightscout.example.com",
        ApiSecret = "test-secret",
        MaxCount = MaxCount,
    };

    /// <summary>Extracts the <c>find[created_at][$gte]</c> lower bound from a recorded request URL.</summary>
    private static DateTime ExtractGte(string url)
    {
        var decoded = Uri.UnescapeDataString(url);
        var match = Regex.Match(decoded, @"\$gte\]=([^&]+)");
        match.Success.Should().BeTrue($"request URL should carry a created_at lower bound: {decoded}");
        return DateTime.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            .ToUniversalTime();
    }

    private static string TreatmentsUrl(SequentialMockHandler handler) =>
        handler.RequestUrls.Single(u => u.Contains("treatments.json"));

    private static string DeviceStatusUrl(SequentialMockHandler handler) =>
        handler.RequestUrls.Single(u => u.Contains("devicestatus.json"));

    [Fact]
    public async Task Treatments_OpenEndedSync_NoPriorTreatments_ImportsFullHistory_NotGlucoseCursor()
    {
        // Glucose is fully caught up (latest entry ~now), but no treatments exist yet. With no
        // prior treatments the initial backfill must import the source's full history (no lower
        // bound) and must NOT inherit the recent glucose cursor.
        var now = DateTime.UtcNow;
        var handler = new SequentialMockHandler();
        handler.Enqueue(JsonResponse(Array.Empty<Entry>()));      // auth check
        handler.Enqueue(JsonResponse(Array.Empty<Treatment>()));  // treatments page (empty)

        var config = Config();
        var service = CreateService(handler, config, latestEntry: now, latestTreatment: null);

        // Open-ended background sync: From is the glucose-derived cursor (~now), To is null.
        var request = new SyncRequest
        {
            From = now.AddMinutes(-5),
            To = null,
            DataTypes = [SyncDataType.Boluses],
        };

        var result = await service.SyncDataAsync(request, config, CancellationToken.None);

        result.Success.Should().BeTrue();
        Uri.UnescapeDataString(TreatmentsUrl(handler)).Should().NotContain("$gte",
            "with no treatments yet, the initial backfill imports the full history (no lower bound), " +
            "not the recent glucose cursor");
    }

    [Fact]
    public async Task Glucose_InitialSync_NoPriorData_ImportsFullHistory_NoLowerBound()
    {
        // No glucose entries exist yet → the initial background sync imports the source's full
        // history (no lower bound), not a capped window.
        var handler = new SequentialMockHandler();

        var config = Config();
        var service = CreateService(handler, config, latestEntry: null);

        // Background entry point (no explicit since): the framework derives From/To.
        var result = await service.SyncDataAsync(config, CancellationToken.None);

        result.Success.Should().BeTrue();
        var entriesPage = handler.RequestUrls.Single(u =>
            u.Contains("entries.json") && u.Contains($"count={MaxCount}"));
        Uri.UnescapeDataString(entriesPage).Should().NotContain("$gte",
            "with no prior glucose data the initial sync imports the full history (no lower bound)");
    }

    [Fact]
    public async Task Treatments_OpenEndedSync_WithExistingTreatments_CatchUpFromOwnLatest()
    {
        var now = DateTime.UtcNow;
        var treatmentLatest = now.AddDays(-2);
        var handler = new SequentialMockHandler();
        handler.Enqueue(JsonResponse(Array.Empty<Entry>()));
        handler.Enqueue(JsonResponse(Array.Empty<Treatment>()));

        var config = Config();
        var service = CreateService(handler, config, latestEntry: now, latestTreatment: treatmentLatest);

        var request = new SyncRequest { From = now.AddMinutes(-5), To = null, DataTypes = [SyncDataType.Boluses] };

        await service.SyncDataAsync(request, config, CancellationToken.None);

        var gte = ExtractGte(TreatmentsUrl(handler));
        // Catch-up resumes from the latest treatment minus a small overlap, independent of glucose.
        gte.Should().BeCloseTo(treatmentLatest.AddMinutes(-5), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Treatments_ExplicitRange_HonoursRequestFrom()
    {
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var handler = new SequentialMockHandler();
        handler.Enqueue(JsonResponse(Array.Empty<Entry>()));
        handler.Enqueue(JsonResponse(Array.Empty<Treatment>()));

        var config = Config();
        var service = CreateService(handler, config, latestTreatment: null);

        // Explicit range (To set) — e.g. a manual re-import — must be honoured verbatim.
        var request = new SyncRequest
        {
            From = from,
            To = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            DataTypes = [SyncDataType.Boluses],
        };

        await service.SyncDataAsync(request, config, CancellationToken.None);

        var gte = ExtractGte(TreatmentsUrl(handler));
        gte.Should().BeCloseTo(from, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task DeviceStatus_OpenEndedSync_NoWatermark_FallsBackToRequestFrom()
    {
        // No device-status watermark available → must fall back to request.From (current behaviour),
        // never re-fetching the full initial window of high-volume telemetry every sync.
        var from = DateTime.UtcNow.AddHours(-1);
        var handler = new SequentialMockHandler();
        handler.Enqueue(JsonResponse(Array.Empty<Entry>()));
        handler.Enqueue(JsonResponse(Array.Empty<DeviceStatus>()));

        var config = Config();
        var service = CreateService(handler, config, latestDeviceStatus: null);

        var request = new SyncRequest { From = from, To = null, DataTypes = [SyncDataType.DeviceStatus] };

        await service.SyncDataAsync(request, config, CancellationToken.None);

        var gte = ExtractGte(DeviceStatusUrl(handler));
        gte.Should().BeCloseTo(from, TimeSpan.FromSeconds(1));
    }

    private static HttpResponseMessage JsonResponse<T>(T data) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(data), System.Text.Encoding.UTF8, "application/json"),
        };

    private sealed class SequentialMockHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();
        public List<string> RequestUrls { get; } = [];

        public void Enqueue(HttpResponseMessage response) => _responses.Enqueue(response);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUrls.Add(request.RequestUri?.PathAndQuery ?? "");
            return Task.FromResult(_responses.Count == 0
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json"),
                }
                : _responses.Dequeue());
        }
    }
}
