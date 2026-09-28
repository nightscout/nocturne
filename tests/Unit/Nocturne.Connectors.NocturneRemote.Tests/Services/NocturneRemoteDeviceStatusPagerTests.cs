using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.NocturneRemote.Configurations;
using Nocturne.Connectors.NocturneRemote.Services;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Connectors.NocturneRemote.Tests.Services;

/// <summary>
/// The v1 device-status crawl pages back by the reading's time, and uploaders write several
/// statuses at one millisecond, so a page can end partway through one. The crawl has to return the rest on
/// the next page without repeating what it already returned.
/// </summary>
public class NocturneRemoteDeviceStatusPagerTests
{
    private const int PageSize = 10;

    private static readonly DateTime Crowded = new(2026, 3, 1, 11, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task More_statuses_at_one_millisecond_than_a_page_holds_are_all_crawled_once()
    {
        var source = new Source();
        source.Add(25, Crowded);
        source.Add(5, Crowded.AddMinutes(-30), step: TimeSpan.FromMinutes(-1));
        var harness = new Harness(source);

        var result = await harness.SyncAsync();

        result.Success.Should().BeTrue();
        harness.Published.Should().BeEquivalentTo(source.Ids);
    }

    [Fact]
    public async Task A_millisecond_the_remote_will_not_widen_past_is_logged_and_stepped_over()
    {
        var source = new Source { CountCap = PageSize };
        source.Add(15, Crowded);
        source.Add(5, Crowded.AddMinutes(-30), step: TimeSpan.FromMinutes(-1));
        var older = source.Ids.TakeLast(5).ToList();
        var harness = new Harness(source);

        var result = await harness.SyncAsync();

        result.Success.Should().BeTrue();
        harness.Published.Should().Contain(older, "the crawl carries on below the crowded millisecond");
        harness.Published.Should().OnlyHaveUniqueItems();
        harness.Logger.Verify(l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) =>
                    state.ToString()!.Contains("devicestatus") && state.ToString()!.Contains($"{Crowded:o}")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once());
    }

    /// <summary>
    /// Stands in for a remote Nocturne's v1 device-status route, which parses the created_at bound
    /// into a time and serves the statuses whose Mills are at or before it, newest first by Mills,
    /// ties in insertion order. Like an older remote, it writes created_at from the save time, and
    /// every status was saved in one bulk load after the newest reading.
    /// </summary>
    private sealed class Source : HttpMessageHandler
    {
        private static readonly DateTime SavedAt = Crowded.AddDays(1);

        private readonly List<(string Id, DateTime At)> _records = [];

        public List<string> Ids => _records.Select(r => r.Id).ToList();

        /// <summary>The most records one reply holds, whatever count was asked for.</summary>
        public int CountCap { get; init; } = int.MaxValue;

        public void Add(int count, DateTime at, TimeSpan step = default)
        {
            for (var i = 0; i < count; i++)
                _records.Add(((_records.Count + 1).ToString("x24", CultureInfo.InvariantCulture), at + step * i));
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == NocturneRemoteConstants.SensorGlucose)
                return Task.FromResult(Json("""{"data":[],"pagination":{"limit":1,"offset":0,"total":0}}"""));

            if (uri.AbsolutePath != "/api/v1/devicestatus.json")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            var url = Uri.UnescapeDataString(uri.Query);
            var count = Math.Min(
                int.Parse(Regex.Match(url, @"count=(\d+)").Groups[1].Value, CultureInfo.InvariantCulture), CountCap);
            var gte = Bound(url, "gte");
            var lte = Bound(url, "lte");

            var page = _records
                .Where(r => (gte is null || r.At >= gte) && (lte is null || r.At <= lte))
                .OrderByDescending(r => r.At)
                .Take(count)
                .Select(r => $$"""{"_id":"{{r.Id}}","mills":{{new DateTimeOffset(r.At).ToUnixTimeMilliseconds()}},"created_at":"{{SavedAt:yyyy-MM-dd'T'HH:mm:ss.fff'Z'}}"}""");

            return Task.FromResult(Json($"[{string.Join(',', page)}]"));
        }

        private static DateTime? Bound(string url, string op) =>
            Regex.Match(url, $@"\[\${op}\]=([^&]+)") is { Success: true } match
                ? DateTime.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)
                : null;

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class Harness(Source source)
    {
        public List<string> Published { get; } = [];

        public Mock<ILogger<NocturneRemoteConnectorService>> Logger { get; } = new();

        public Task<SyncResult> SyncAsync()
        {
            var device = new Mock<IDevicePublisher>();
            device
                .Setup(p => p.PublishDeviceStatusAsync(
                    It.IsAny<IEnumerable<DeviceStatus>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
                .Callback<IEnumerable<DeviceStatus>, string, WriteOrigin, CancellationToken>(
                    (batch, _, _, _) => Published.AddRange(batch.Select(s => s.Id!)))
                .ReturnsAsync(true);

            var publisher = new Mock<IConnectorPublisher>();
            publisher.Setup(p => p.IsAvailable).Returns(true);
            publisher.Setup(p => p.Glucose).Returns(Mock.Of<IGlucosePublisher>());
            publisher.Setup(p => p.Treatments).Returns(Mock.Of<ITreatmentPublisher>());
            publisher.Setup(p => p.Device).Returns(device.Object);
            publisher.Setup(p => p.Metadata).Returns(Mock.Of<IMetadataPublisher>());

            var service = new NocturneRemoteConnectorService(
                new HttpClient(source),
                Mock.Of<IConnectorServerResolver<NocturneRemoteConnectorConfiguration>>(),
                Logger.Object,
                Mock.Of<IRetryDelayStrategy>(),
                publisher.Object);

            var config = new NocturneRemoteConnectorConfiguration
            {
                Url = "https://remote.example",
                AccessToken = "direct-grant-token",
                MaxCount = PageSize,
                MaxRetryAttempts = 1,
            };
            var request = new SyncRequest
            {
                From = Crowded.AddHours(-2),
                To = Crowded.AddHours(1),
                DataTypes = [SyncDataType.DeviceStatus],
            };
            return service.SyncDataAsync(request, config, CancellationToken.None);
        }
    }
}
