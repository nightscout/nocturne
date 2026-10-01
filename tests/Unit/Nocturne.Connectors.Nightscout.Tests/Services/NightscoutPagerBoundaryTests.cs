using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services;
using Nocturne.Connectors.Nightscout.Tests.TestSupport;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Connectors.Nightscout.Tests.Services;

/// <summary>
/// Uploaders write several records at one millisecond (an SMB and its temp basal, a meal bolus
/// and its carbs), so a page can end partway through a millisecond. The crawl has to return the
/// rest of it on the next page without repeating what it already returned.
/// </summary>
public class NightscoutPagerBoundaryTests
{
    private const int MaxCount = 10;

    private static readonly DateTime Crowded = new(2026, 3, 1, 11, 0, 0, DateTimeKind.Utc);

    private const string WholeSeconds = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    private static readonly SyncRequest Window = new()
    {
        From = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
        To = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc),
    };

    [Theory]
    [InlineData(SyncDataType.Boluses, "treatments")]
    [InlineData(SyncDataType.DeviceStatus, "devicestatus")]
    [InlineData(SyncDataType.Activity, "activity")]
    public async Task More_records_at_one_millisecond_than_a_page_holds_are_all_crawled_once(
        SyncDataType dataType, string collection)
    {
        var source = new Source(collection);
        source.AddCreatedAt(25, Crowded);
        source.AddCreatedAt(5, Crowded.AddMinutes(-30), step: TimeSpan.FromMinutes(-1));
        var harness = new Harness(source);

        var result = await harness.SyncAsync(dataType);

        result.Success.Should().BeTrue();
        harness.Published.Should().BeEquivalentTo(source.Ids);
    }

    [Fact]
    public async Task Records_written_at_whole_seconds_at_a_crowded_boundary_are_all_crawled_once()
    {
        var source = new Source("treatments");
        source.AddCreatedAt(15, Crowded, format: WholeSeconds);
        source.AddCreatedAt(5, Crowded.AddMinutes(-30), step: TimeSpan.FromMinutes(-1));
        var harness = new Harness(source);

        var result = await harness.SyncAsync(SyncDataType.Boluses);

        result.Success.Should().BeTrue();
        harness.Published.Should().BeEquivalentTo(source.Ids);
    }

    [Fact]
    public async Task A_source_capping_the_count_is_stepped_past_with_a_warning_rather_than_read_as_the_end()
    {
        var source = new Source("treatments") { CountCap = MaxCount };
        source.AddCreatedAt(15, Crowded);
        source.AddCreatedAt(5, Crowded.AddMinutes(-30), step: TimeSpan.FromMinutes(-1));
        var older = source.Ids.TakeLast(5).ToList();
        var harness = new Harness(source);

        var result = await harness.SyncAsync(SyncDataType.Boluses);

        result.Success.Should().BeTrue();
        harness.Published.Should().Contain(older, "the records below the capped millisecond are still there");
        harness.Published.Should().OnlyHaveUniqueItems();
        harness.VerifyWarning(Times.Once(), "treatments", $"{Crowded:o}");
    }

    [Fact]
    public async Task A_cap_between_the_page_and_the_widened_count_is_stepped_past_rather_than_read_as_the_end()
    {
        var source = new Source("treatments") { CountCap = MaxCount + 5 };
        source.AddCreatedAt(25, Crowded);
        source.AddCreatedAt(5, Crowded.AddMinutes(-30), step: TimeSpan.FromMinutes(-1));
        var older = source.Ids.TakeLast(5).ToList();
        var harness = new Harness(source);

        var result = await harness.SyncAsync(SyncDataType.Boluses);

        result.Success.Should().BeTrue();
        harness.Published.Should().Contain(older);
        harness.Published.Should().OnlyHaveUniqueItems();
        harness.VerifyWarning(Times.Once(), "treatments", $"{Crowded:o}");
    }

    [Fact]
    public async Task A_capped_widened_page_reaching_below_the_crowded_millisecond_is_not_read_as_the_end()
    {
        var source = new Source("treatments") { CountCap = MaxCount + 5 };
        source.AddCreatedAt(12, Crowded);
        source.AddCreatedAt(20, Crowded.AddMinutes(-30), step: TimeSpan.FromMinutes(-1));
        var harness = new Harness(source);

        var result = await harness.SyncAsync(SyncDataType.Boluses);

        result.Success.Should().BeTrue();
        harness.Published.Should().BeEquivalentTo(source.Ids);
    }

    [Fact]
    public async Task A_second_crowded_across_several_milliseconds_is_crawled_once_through_its_whole_second_bound()
    {
        var source = new Source("treatments");
        source.AddCreatedAt(10, Crowded.AddMilliseconds(990));
        source.AddCreatedAt(10, Crowded.AddMilliseconds(500));
        source.AddCreatedAt(30, Crowded);
        source.AddCreatedAt(5, Crowded.AddMinutes(-30), step: TimeSpan.FromMinutes(-1));
        var harness = new Harness(source);

        var result = await harness.SyncAsync(SyncDataType.Boluses);

        result.Success.Should().BeTrue();
        harness.Published.Should().BeEquivalentTo(source.Ids);
        harness.VerifyWarning(Times.Never());
    }

    [Fact]
    public async Task A_page_the_source_orders_differently_than_it_parses_stops_the_crawl_with_a_warning()
    {
        // A legacy created_at with a space for the T sorts below every T-format bound on its date,
        // so a page of them comes back under a bound they parse later than.
        var legacyDay = Window.To!.Value.AddHours(15);
        var source = new Source("treatments");
        source.AddCreatedAt(MaxCount, legacyDay, step: TimeSpan.FromMinutes(1), format: "yyyy-MM-dd HH:mm:ss");
        source.AddCreatedAt(5, Crowded, step: TimeSpan.FromMinutes(-1));
        var harness = new Harness(source);

        await harness.SyncAsync(SyncDataType.Boluses);

        source.Requests.Should().Be(1);
        harness.VerifyWarning(Times.Once(), "treatments", $"{legacyDay:o}", $"{Window.To.Value.AddHours(14):o}");
    }

    [Fact]
    public async Task A_record_served_on_two_pages_is_published_once()
    {
        var source = new Source("entries");
        source.AddEntries(9, Crowded, step: TimeSpan.FromMinutes(-5));
        source.AddEntries(3, Crowded.AddMinutes(-45));
        source.AddEntries(1, Crowded.AddMinutes(-50));
        var boundary = source.Ids[9];
        var harness = new Harness(source);

        var result = await harness.SyncAsync(SyncDataType.Glucose);

        result.Success.Should().BeTrue();
        source.Served.Count(id => id == boundary).Should().Be(2,
            "the page ending on this millisecond is followed by one starting on it");
        harness.Published.Should().BeEquivalentTo(source.Ids);
    }

    [Fact]
    public async Task A_millisecond_too_crowded_to_widen_past_is_logged_and_stepped_over()
    {
        var source = new Source("treatments");
        source.AddCreatedAt(150, Crowded);
        source.AddCreatedAt(3, Crowded.AddMinutes(-30), step: TimeSpan.FromMinutes(-1));
        var older = source.Ids.TakeLast(3).ToList();
        var harness = new Harness(source);

        var result = await harness.SyncAsync(SyncDataType.Boluses);

        result.Success.Should().BeTrue();
        harness.Published.Should().Contain(older, "the crawl carries on below the crowded millisecond");
        harness.Published.Should().OnlyHaveUniqueItems();
        harness.VerifyWarning(Times.Once(), "treatments", $"{Crowded:o}");
    }

    /// <summary>
    /// Stands in for a Nightscout collection: entries filter and sort on the numeric date, the
    /// created_at collections on created_at as an ordinal string, newest first, ties in insertion
    /// order.
    /// </summary>
    private sealed class Source(string collection)
    {
        private readonly List<(string Id, string Key, string Json)> _records = [];

        public string Collection { get; } = collection;

        public List<string> Ids => _records.Select(r => r.Id).ToList();

        public List<string> Served { get; } = [];

        public int Requests { get; private set; }

        /// <summary>The most records one reply holds, whatever count was asked for.</summary>
        public int CountCap { get; init; } = int.MaxValue;

        public void AddCreatedAt(
            int count, DateTime at, TimeSpan step = default, string format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'")
        {
            for (var i = 0; i < count; i++)
            {
                var id = NextId();
                var createdAt = (at + step * i).ToString(format, CultureInfo.InvariantCulture);
                _records.Add((id, createdAt,
                    $$"""{"_id":"{{id}}","eventType":"Temp Basal","created_at":"{{createdAt}}"}"""));
            }
        }

        public void AddEntries(int count, DateTime at, TimeSpan step = default)
        {
            for (var i = 0; i < count; i++)
            {
                var id = NextId();
                var mills = new DateTimeOffset(at + step * i).ToUnixTimeMilliseconds();
                _records.Add((id, mills.ToString("D20", CultureInfo.InvariantCulture),
                    JsonSerializer.Serialize(new Entry { Id = id, Mills = mills, Sgv = 100, Type = "sgv" })));
            }
        }

        public HttpResponseMessage Answer(Uri uri)
        {
            var url = Uri.UnescapeDataString(uri.ToString());
            if (!url.Contains($"/api/v1/{Collection}.json", StringComparison.Ordinal) || !url.Contains("find["))
                return Json([]);

            Requests++;
            var count = Math.Min(
                int.Parse(Regex.Match(url, @"count=(\d+)").Groups[1].Value, CultureInfo.InvariantCulture), CountCap);
            var gte = Bound(url, "gte");
            var lte = Bound(url, "lte");

            var page = _records
                .Where(r => (gte is null || string.CompareOrdinal(r.Key, gte) >= 0)
                            && (lte is null || string.CompareOrdinal(r.Key, lte) <= 0))
                .OrderByDescending(r => r.Key, StringComparer.Ordinal)
                .Take(count)
                .ToList();

            Served.AddRange(page.Select(r => r.Id));
            return Json(page.Select(r => r.Json));
        }

        private string NextId() => (_records.Count + 1).ToString("x24", CultureInfo.InvariantCulture);

        private string? Bound(string url, string op)
        {
            var match = Regex.Match(url, $@"\[\${op}\]=([^&]+)");
            if (!match.Success)
                return null;

            return Collection == "entries"
                ? long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture).ToString("D20", CultureInfo.InvariantCulture)
                : match.Groups[1].Value;
        }

        private static HttpResponseMessage Json(IEnumerable<string> documents) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent($"[{string.Join(',', documents)}]", Encoding.UTF8, "application/json"),
            };
    }

    private sealed class Harness(Source source)
    {
        public List<string> Published { get; } = [];

        public Mock<ILogger<NightscoutConnectorService>> Logger { get; } = new();

        public void VerifyWarning(Times times, params string[] naming) =>
            Logger.Verify(l => l.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((state, _) => naming.All(n => state.ToString()!.Contains(n))),
                    It.IsAny<Exception?>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                times);

        public Task<SyncResult> SyncAsync(SyncDataType dataType)
        {
            var config = new NightscoutConnectorConfiguration
            {
                Url = "https://ns.example",
                ApiSecret = "secret",
                MaxCount = MaxCount,
            };
            var request = new SyncRequest { From = Window.From, To = Window.To, DataTypes = [dataType] };
            return NewService().SyncDataAsync(request, config, CancellationToken.None);
        }

        private NightscoutConnectorService NewService()
        {
            var glucose = new Mock<IGlucosePublisher>();
            glucose
                .Setup(p => p.PublishEntriesAsync(
                    It.IsAny<IEnumerable<Entry>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
                .Callback<IEnumerable<Entry>, string, WriteOrigin, CancellationToken>((batch, _, _, _) => Record(batch))
                .ReturnsAsync(true);

            var treatments = new Mock<ITreatmentPublisher>();
            treatments
                .Setup(p => p.PublishTreatmentsAsync(
                    It.IsAny<IEnumerable<Treatment>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
                .Callback<IEnumerable<Treatment>, string, WriteOrigin, CancellationToken>((batch, _, _, _) => Record(batch))
                .ReturnsAsync(true);

            var device = new Mock<IDevicePublisher>();
            device
                .Setup(p => p.PublishDeviceStatusAsync(
                    It.IsAny<IEnumerable<DeviceStatus>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
                .Callback<IEnumerable<DeviceStatus>, string, WriteOrigin, CancellationToken>((batch, _, _, _) => Record(batch))
                .ReturnsAsync(true);

            var metadata = new Mock<IMetadataPublisher>();
            metadata
                .Setup(p => p.PublishActivityAsync(
                    It.IsAny<IEnumerable<Activity>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
                .Callback<IEnumerable<Activity>, string, WriteOrigin, CancellationToken>((batch, _, _, _) => Record(batch))
                .ReturnsAsync(true);

            var publisher = new Mock<IConnectorPublisher>();
            publisher.Setup(p => p.IsAvailable).Returns(true);
            publisher.Setup(p => p.Glucose).Returns(glucose.Object);
            publisher.Setup(p => p.Treatments).Returns(treatments.Object);
            publisher.Setup(p => p.Device).Returns(device.Object);
            publisher.Setup(p => p.Metadata).Returns(metadata.Object);

            var registration = new Mock<IConnectorRegistration<NightscoutConnectorConfiguration>>();
            registration.Setup(r => r.Defaults).Returns(new NightscoutConnectorConfiguration());

            var handler = new RecordingHttpMessageHandler { Respond = source.Answer };

            return new NightscoutConnectorService(
                new HttpClient(handler),
                Mock.Of<IConnectorServerResolver<NightscoutConnectorConfiguration>>(),
                Logger.Object,
                Mock.Of<IRetryDelayStrategy>(),
                Mock.Of<IRateLimitingStrategy>(),
                registration.Object,
                publisher.Object);
        }

        private void Record(IEnumerable<ProcessableDocumentBase> batch) => Published.AddRange(batch.Select(r => r.Id!));
    }
}
