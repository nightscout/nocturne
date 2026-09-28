using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Connectors.Nightscout.Tests.Services;

/// <summary>
/// Glucose and device status resume from the newest stored time, so a record the source receives
/// after a newer one (CGM readings backfilled once the signal returns) sits below the cursor. The
/// catch-up hands what its read returned below the cursor to the publisher's recent path, which
/// writes only what is not already stored.
/// </summary>
public class NightscoutBackfillReconcileTests
{
    private static readonly DateTime Now = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime From = Now.AddMinutes(-30);

    [Fact]
    public async Task A_cgm_reading_backfilled_below_the_cursor_goes_to_the_recent_path()
    {
        var harness = new Harness { Entries = [Sgv("sgv-new", Now.AddMinutes(-2)), Sgv("sgv-backfilled", Now.AddHours(-3))] };

        var result = await harness.SyncAsync(SyncDataType.Glucose);

        result.Success.Should().BeTrue();
        harness.Crawled.Select(e => e.Id).Should().Equal("sgv-new");
        harness.RecentEntries.Select(e => e.Id).Should().Equal("sgv-backfilled");
        harness.RecentEntries.Should().OnlyContain(e => e.DataSource == "nightscout-connector");
        result.ItemsSynced[SyncDataType.Glucose].Should().Be(2);
    }

    [Fact]
    public async Task The_entries_read_reaches_the_reconcile_window_below_the_cursor()
    {
        var harness = new Harness { Entries = [Sgv("sgv-new", Now.AddMinutes(-2))] };

        await harness.SyncAsync(SyncDataType.Glucose);

        harness.LowerBounds("entries").Should().Contain(
            new DateTimeOffset(From.AddHours(-13)).ToUnixTimeMilliseconds().ToString(),
            "entries are read on their own time, so the read has to reach 12 h plus the margin below the cursor itself");
    }

    [Fact]
    public async Task A_reading_below_the_reconcile_window_is_left_alone()
    {
        var harness = new Harness { Entries = [Sgv("sgv-new", Now.AddMinutes(-2)), Sgv("sgv-old", From.AddHours(-14))] };

        await harness.SyncAsync(SyncDataType.Glucose);

        harness.Crawled.Select(e => e.Id).Should().Equal("sgv-new");
        harness.RecentEntries.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_recent_write_fails_the_glucose_sync()
    {
        var harness = new Harness
        {
            Entries = [Sgv("sgv-new", Now.AddMinutes(-2)), Sgv("sgv-backfilled", Now.AddHours(-3))],
            RecentWriteFails = true,
        };

        var result = await harness.SyncAsync(SyncDataType.Glucose);

        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task A_ranged_sync_reads_only_its_range_and_reconciles_nothing()
    {
        var harness = new Harness { Entries = [Sgv("sgv-new", Now.AddMinutes(-2))] };

        await harness.SyncAsync(SyncDataType.Glucose, to: Now);

        harness.LowerBounds("entries").Should().Equal(new DateTimeOffset(From).ToUnixTimeMilliseconds().ToString());
        harness.RecentEntries.Should().BeEmpty();
    }

    [Fact]
    public async Task A_device_status_uploaded_late_below_the_cursor_goes_to_the_recent_path()
    {
        var harness = new Harness
        {
            DeviceStatuses = [Status("ds-new", Now.AddMinutes(-2)), Status("ds-late", Now.AddHours(-3))],
        };

        var result = await harness.SyncAsync(SyncDataType.DeviceStatus);

        result.Success.Should().BeTrue();
        harness.CrawledStatuses.Select(d => d.Id).Should().Equal("ds-new");
        harness.RecentStatuses.Select(d => d.Id).Should().Equal("ds-late");
        harness.LowerBounds("devicestatus").Should().ContainSingle().Which.Should().Be($"{From.AddHours(-14):o}",
            "the created_at read already reaches past the reconcile window, so it is not extended");
    }

    private static string Sgv(string id, DateTime at) =>
        $$"""{"_id":"{{id}}","type":"sgv","sgv":120,"date":{{new DateTimeOffset(at).ToUnixTimeMilliseconds()}},"mills":{{new DateTimeOffset(at).ToUnixTimeMilliseconds()}}}""";

    private static string Status(string id, DateTime at) =>
        $$"""{"_id":"{{id}}","device":"loop://iPhone","created_at":"{{at:yyyy-MM-dd'T'HH:mm:ss.fff'Z'}}"}""";

    private sealed class Harness : HttpMessageHandler
    {
        private const string Url = "https://nightscout.example.com";

        public string[] Entries { get; init; } = [];
        public string[] DeviceStatuses { get; init; } = [];
        public bool RecentWriteFails { get; init; }

        public List<Entry> Crawled { get; } = [];
        public List<Entry> RecentEntries { get; } = [];
        public List<DeviceStatus> CrawledStatuses { get; } = [];
        public List<DeviceStatus> RecentStatuses { get; } = [];
        private List<string> Urls { get; } = [];

        public IEnumerable<string> LowerBounds(string collection) => Urls
            .Where(u => u.Contains($"/api/v1/{collection}.json?count=1000", StringComparison.Ordinal))
            .Select(u => Regex.Match(u, @"find\[(?:date|created_at)\]\[\$gte\]=([^&]+)"))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value);

        public Task<SyncResult> SyncAsync(SyncDataType type, DateTime? to = null) =>
            NewService().SyncDataAsync(
                new SyncRequest { From = From, To = to, DataTypes = [type] },
                new NightscoutConnectorConfiguration { Url = Url, ApiSecret = "secret" },
                CancellationToken.None);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = Uri.UnescapeDataString(request.RequestUri!.ToString());
            Urls.Add(url);
            var docs = url.Contains("/api/v1/entries.json?count=1000", StringComparison.Ordinal) ? Entries
                : url.Contains("/api/v1/devicestatus.json", StringComparison.Ordinal) ? DeviceStatuses
                : [];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"[{string.Join(",", docs)}]", Encoding.UTF8, "application/json"),
            });
        }

        private NightscoutConnectorService NewService()
        {
            var glucose = new Mock<IGlucosePublisher>();
            glucose
                .Setup(p => p.GetLatestEntryTimestampAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(From);
            glucose
                .Setup(p => p.PublishEntriesAsync(
                    It.IsAny<IEnumerable<Entry>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
                .Callback<IEnumerable<Entry>, string, WriteOrigin, CancellationToken>((batch, _, _, _) => Crawled.AddRange(batch))
                .ReturnsAsync(true);
            glucose
                .Setup(p => p.PublishRecentEntriesAsync(
                    It.IsAny<IEnumerable<Entry>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
                .Callback<IEnumerable<Entry>, string, WriteOrigin, CancellationToken>((batch, _, _, _) => RecentEntries.AddRange(batch))
                .ReturnsAsync((IEnumerable<Entry> batch, string _, WriteOrigin _, CancellationToken _) =>
                    RecentWriteFails ? (int?)null : batch.Count());

            var device = new Mock<IDevicePublisher>();
            device
                .Setup(p => p.GetLatestDeviceStatusTimestampAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Now);
            device
                .Setup(p => p.PublishDeviceStatusAsync(
                    It.IsAny<IEnumerable<DeviceStatus>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
                .Callback<IEnumerable<DeviceStatus>, string, WriteOrigin, CancellationToken>((batch, _, _, _) => CrawledStatuses.AddRange(batch))
                .ReturnsAsync(true);
            device
                .Setup(p => p.PublishRecentDeviceStatusAsync(
                    It.IsAny<IEnumerable<DeviceStatus>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
                .Callback<IEnumerable<DeviceStatus>, string, WriteOrigin, CancellationToken>((batch, _, _, _) => RecentStatuses.AddRange(batch))
                .ReturnsAsync((IEnumerable<DeviceStatus> batch, string _, WriteOrigin _, CancellationToken _) =>
                    RecentWriteFails ? (int?)null : batch.Count());

            var publisher = new Mock<IConnectorPublisher>();
            publisher.Setup(p => p.IsAvailable).Returns(true);
            publisher.Setup(p => p.Glucose).Returns(glucose.Object);
            publisher.Setup(p => p.Device).Returns(device.Object);
            publisher.Setup(p => p.Treatments).Returns(Mock.Of<ITreatmentPublisher>());
            publisher.Setup(p => p.Metadata).Returns(Mock.Of<IMetadataPublisher>());

            var registration = new Mock<IConnectorRegistration<NightscoutConnectorConfiguration>>();
            registration.Setup(r => r.Defaults).Returns(new NightscoutConnectorConfiguration());

            return new NightscoutConnectorService(
                new HttpClient(this),
                Mock.Of<IConnectorServerResolver<NightscoutConnectorConfiguration>>(),
                NullLogger<NightscoutConnectorService>.Instance,
                Mock.Of<IRetryDelayStrategy>(),
                Mock.Of<IRateLimitingStrategy>(),
                registration.Object,
                publisher.Object);
        }
    }
}
