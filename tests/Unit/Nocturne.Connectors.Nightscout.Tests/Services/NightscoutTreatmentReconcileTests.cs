using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
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
/// The catch-up's reconcile of its recent treatments: it publishes again what the event-time
/// cursor cannot see, and deletes this connector's rows the source no longer has, but only on a
/// lookup the source has shown it can answer.
/// </summary>
public class NightscoutTreatmentReconcileTests
{
    private const string Source = "nightscout-connector";

    // Synthetic ids in the shapes uploaders write: Trio sends its own UUID `id` alongside Mongo's `_id`.
    private const string TrioKept = "11111111-1111-4111-8111-111111111111";
    private const string TrioGone = "22222222-2222-4222-8222-222222222222";
    private const string LoopKept = "aaaaaaaaaaaaaaaaaaaaaaa1";
    private const string LoopGone = "aaaaaaaaaaaaaaaaaaaaaaa2";
    private const string MongoIdA = "bbbbbbbbbbbbbbbbbbbbbbb1";

    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 30, 0, TimeSpan.Zero);
    private static readonly DateTime StoredAt = Now.UtcDateTime.AddMinutes(-30);

    [Fact]
    public async Task A_trio_treatment_the_source_deleted_is_looked_up_by_its_uuid_and_deleted()
    {
        var harness = new Harness
        {
            Upstream = [Trio(MongoIdA, TrioKept, Now.AddMinutes(-30))],
            Stored = [TrioKept, TrioGone],
        };

        var result = await harness.SyncAsync();

        result.Success.Should().BeTrue();
        harness.Deleted.Should().ContainSingle().Which.Should().BeEquivalentTo([TrioGone]);
        harness.Lookups.Should().Contain($"find[id]={TrioGone}");
        harness.Lookups.Should().NotContain(l => l.StartsWith("find[_id]"),
            "a UUID is not an ObjectId, and asking for one by _id fails on older Nightscout");
        harness.LookupUrls.Should().Contain(u => u.EndsWith($"find[id]={TrioGone}")
            && u.Contains($"find[created_at][$gte]={StoredAt.AddHours(-15):o}")
            && u.Contains($"find[created_at][$lte]={StoredAt.AddHours(15):yyyy-MM-dd'T'HH:mm:ss'Z'}"),
            "neither id field is indexed, so the lookup is bounded to where the treatment can sit");
    }

    [Fact]
    public async Task A_loop_treatment_the_source_deleted_is_looked_up_by_its_object_id()
    {
        var harness = new Harness
        {
            Upstream = [Loop(LoopKept, Now.AddMinutes(-30))],
            Stored = [LoopKept, LoopGone],
        };

        await harness.SyncAsync();

        harness.Lookups.Should().Contain([$"find[_id]={LoopKept}", $"find[_id]={LoopGone}", $"find[id]={LoopGone}"]);
        harness.Deleted.Should().ContainSingle().Which.Should().BeEquivalentTo([LoopGone]);
    }

    [Fact]
    public async Task A_failing_lookup_deletes_nothing_and_does_not_fail_the_sync()
    {
        var harness = new Harness
        {
            Upstream = [Trio(MongoIdA, TrioKept, Now.AddMinutes(-30))],
            Stored = [TrioKept, TrioGone],
            LookupFails = true,
        };

        var result = await harness.SyncAsync();

        result.Success.Should().BeTrue();
        harness.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task A_source_that_cannot_find_what_it_just_returned_deletes_nothing()
    {
        var harness = new Harness
        {
            Upstream = [Trio(MongoIdA, TrioKept, Now.AddMinutes(-30))],
            Stored = [TrioKept, TrioGone],
            LookupFindsNothing = true,
        };

        await harness.SyncAsync();

        harness.Lookups.Should().Equal($"find[id]={TrioKept}");
        harness.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task A_row_the_lookup_still_finds_is_kept_and_not_looked_up_again()
    {
        // Missing from the read, as a record with a created_at outside the window would be, but the
        // source still has it.
        var harness = new Harness
        {
            Upstream = [Trio(MongoIdA, TrioKept, Now.AddMinutes(-30))],
            Stored = [TrioKept, TrioGone],
            AlsoUpstream = [TrioGone],
        };

        await harness.SyncAsync();
        var firstLookups = harness.Lookups.Count;
        await harness.SyncAsync();

        harness.Deleted.Should().BeEmpty();
        harness.Lookups.Skip(firstLookups).Should().NotContain($"find[id]={TrioGone}");
    }

    [Fact]
    public async Task A_source_missing_much_of_the_window_deletes_nothing()
    {
        var missing = Enumerable.Range(0, 5).Select(i => $"cccccccc-cccc-4ccc-8ccc-00000000000{i}").ToList();
        var harness = new Harness
        {
            Upstream = [Trio(MongoIdA, TrioKept, Now.AddMinutes(-30))],
            Stored = [TrioKept, .. missing],
        };

        var result = await harness.SyncAsync();

        result.Success.Should().BeTrue();
        harness.Lookups.Should().BeEmpty();
        harness.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task A_sparse_window_can_still_lose_its_one_deleted_treatment()
    {
        var harness = new Harness
        {
            Upstream = [Trio(MongoIdA, TrioKept, Now.AddMinutes(-30))],
            Stored = [TrioGone, TrioKept],
        };

        await harness.SyncAsync();

        harness.Deleted.Should().ContainSingle();
    }

    [Fact]
    public async Task Treatments_the_crawl_did_not_publish_are_published_again()
    {
        // Both sit below the crawl's resume point: one a late upload, one an in-place edit of a
        // stored treatment. Which of them the store takes is its decision.
        var harness = new Harness
        {
            LatestStored = Now.AddMinutes(-5),
            Upstream =
            [
                Loop(LoopKept, Now.AddMinutes(-3)),
                Trio(MongoIdA, TrioKept, Now.AddMinutes(-40)),
                Loop(LoopGone, Now.AddMinutes(-50)),
            ],
            Stored = [TrioKept, LoopKept],
        };

        var result = await harness.SyncAsync();

        result.Success.Should().BeTrue();
        harness.Crawled.Select(t => t.Id).Should().Equal(LoopKept);
        harness.Republished.Select(t => t.Id).Should().BeEquivalentTo([TrioKept, LoopGone]);
        harness.Republished.Should().OnlyContain(t => t.DataSource == Source);
    }

    [Fact]
    public async Task A_full_reconcile_runs_once_an_hour_has_passed_and_is_recorded()
    {
        var harness = new Harness
        {
            LastFullReconcile = Now.UtcDateTime.AddMinutes(-61),
            Upstream = [Trio(MongoIdA, TrioKept, Now.AddMinutes(-30))],
        };

        await harness.SyncAsync();

        harness.CrawlLowerBound.Should().Be(Now.UtcDateTime.AddHours(-25));
        harness.FullReconcileRecorded.Should().Equal(Now.UtcDateTime);
    }

    [Fact]
    public async Task Between_full_reconciles_the_window_comes_from_the_crawls_own_download()
    {
        // Eleven hours below the resume point: inside the created_at envelope the crawl already
        // downloads, and so reconciled without a request of its own.
        var harness = new Harness
        {
            LastFullReconcile = Now.UtcDateTime.AddMinutes(-20),
            Upstream = [Trio(MongoIdA, TrioKept, Now.AddHours(-11))],
            Stored = [TrioKept],
        };

        await harness.SyncAsync();

        harness.TreatmentReads.Should().Be(1);
        harness.CrawlLowerBound.Should().Be(Now.UtcDateTime.AddMinutes(-15));
        harness.Republished.Select(t => t.Id).Should().Equal(TrioKept);
        harness.FullReconcileRecorded.Should().BeEmpty();
    }

    [Fact]
    public async Task Only_what_the_store_wrote_counts_as_synced()
    {
        var harness = new Harness
        {
            Upstream = [Loop(LoopKept, Now.AddMinutes(-40)), Loop(LoopGone, Now.AddMinutes(-50))],
            Stored = [LoopKept, LoopGone],
            Written = 1,
        };

        var result = await harness.SyncAsync();

        harness.Republished.Should().HaveCount(2);
        result.ItemsSynced[SyncDataType.CarbIntake].Should().Be(1);
    }

    [Fact]
    public async Task A_failed_read_deletes_nothing()
    {
        var harness = new Harness
        {
            ReadFails = true,
            Stored = [TrioKept, TrioGone],
        };

        var result = await harness.SyncAsync();

        result.Success.Should().BeFalse();
        harness.Deleted.Should().BeEmpty();
        harness.Lookups.Should().BeEmpty();
    }

    [Fact]
    public async Task An_empty_read_deletes_nothing()
    {
        var harness = new Harness { Stored = [TrioKept, TrioGone] };

        var result = await harness.SyncAsync();

        result.Success.Should().BeTrue();
        harness.Lookups.Should().BeEmpty();
        harness.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task Only_this_connectors_rows_are_asked_about_or_deleted()
    {
        var harness = new Harness
        {
            Upstream = [Trio(MongoIdA, TrioKept, Now.AddMinutes(-30))],
            Stored = [TrioKept, TrioGone],
        };

        await harness.SyncAsync();

        harness.StoredAskedOf.Should().Equal(Source);
        harness.DeletedFrom.Should().Equal(Source);
    }

    [Fact]
    public async Task A_ranged_re_import_does_not_reconcile()
    {
        var harness = new Harness
        {
            Upstream = [Trio(MongoIdA, TrioKept, Now.AddMinutes(-30))],
            Stored = [TrioKept, TrioGone],
        };

        await harness.SyncAsync(new SyncRequest
        {
            From = Now.UtcDateTime.AddDays(-2),
            To = Now.UtcDateTime.AddDays(-1),
            DataTypes = [SyncDataType.CarbIntake],
        });

        harness.StoredAskedOf.Should().BeEmpty();
        harness.Republished.Should().BeEmpty();
        harness.Lookups.Should().BeEmpty();
    }

    private static string Trio(string mongoId, string trioId, DateTimeOffset at) =>
        $$"""{"_id":"{{mongoId}}","id":"{{trioId}}","enteredBy":"Trio","eventType":"Carb Correction","carbs":20,"created_at":"{{at.UtcDateTime:o}}"}""";

    private static string Loop(string mongoId, DateTimeOffset at) =>
        $$"""{"_id":"{{mongoId}}","syncIdentifier":"loop-sync-{{mongoId}}","enteredBy":"Loop","eventType":"Carb Correction","carbs":20,"created_at":"{{at.UtcDateTime:o}}"}""";

    private static HttpResponseMessage Json(IEnumerable<string> treatments) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent($"[{string.Join(',', treatments)}]", Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage Failure() =>
        new(HttpStatusCode.InternalServerError) { Content = new StringContent("") };

    private sealed class Harness : HttpMessageHandler
    {
        public string Url { get; init; } = $"https://{Guid.NewGuid():N}.ns.example";

        public DateTimeOffset At { get; init; } = Now;
        public DateTimeOffset LatestStored { get; init; } = Now.AddMinutes(-10);
        public List<string> Upstream { get; init; } = [];
        public HashSet<string> AlsoUpstream { get; init; } = [];
        public HashSet<string> Stored { get; init; } = [];
        public bool ReadFails { get; init; }
        public bool LookupFails { get; init; }
        public bool LookupFindsNothing { get; init; }
        public DateTime? LastFullReconcile { get; init; }
        public int? Written { get; init; }

        public List<Treatment> Crawled { get; } = [];
        public List<Treatment> Republished { get; } = [];
        public List<IReadOnlySet<string>> Deleted { get; } = [];
        public List<string> DeletedFrom { get; } = [];
        public List<string> StoredAskedOf { get; } = [];
        public List<string> Lookups { get; } = [];
        public List<string> LookupUrls { get; } = [];
        public List<DateTime?> FullReconcileRecorded { get; } = [];
        public int TreatmentReads { get; private set; }
        public DateTime? CrawlLowerBound { get; private set; }

        public Task<SyncResult> SyncAsync(SyncRequest? request = null)
        {
            var config = new NightscoutConnectorConfiguration { Url = Url, ApiSecret = "secret" };
            return NewService().SyncDataAsync(
                request ?? new SyncRequest { From = At.UtcDateTime.AddMinutes(-5), To = null, DataTypes = [SyncDataType.CarbIntake] },
                config,
                CancellationToken.None);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = Uri.UnescapeDataString(request.RequestUri!.ToString());
            if (!url.Contains("/api/v1/treatments.json", StringComparison.Ordinal))
                return Task.FromResult(Json([]));

            var lookup = System.Text.RegularExpressions.Regex.Match(url, @"find\[(_id|id)\]=(.+)$");
            if (lookup.Success)
            {
                var (field, id) = (lookup.Groups[1].Value, lookup.Groups[2].Value);
                Lookups.Add($"find[{field}]={id}");
                LookupUrls.Add(url);
                if (LookupFails)
                    return Task.FromResult(Failure());

                var found = !LookupFindsNothing && (AlsoUpstream.Contains(id) || Upstream.Any(doc =>
                    doc.Contains(field == "_id" ? $"\"_id\":\"{id}\"" : $"\"id\":\"{id}\"", StringComparison.Ordinal)));
                return Task.FromResult(Json(found ? [Upstream.FirstOrDefault() ?? "{}"] : []));
            }

            TreatmentReads++;
            var gte = System.Text.RegularExpressions.Regex.Match(url, @"find\[created_at\]\[\$gte\]=([^&]+)");
            if (gte.Success && CrawlLowerBound is null)
                CrawlLowerBound = DateTime.Parse(gte.Groups[1].Value, null,
                    System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime().AddHours(14);

            return Task.FromResult(ReadFails ? Failure() : Json(Upstream));
        }

        private NightscoutConnectorService NewService()
        {
            var treatments = new Mock<ITreatmentPublisher>();
            treatments
                .Setup(p => p.GetLatestTreatmentTimestampAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(LatestStored.UtcDateTime);
            treatments
                .Setup(p => p.PublishTreatmentsAsync(
                    It.IsAny<IEnumerable<Treatment>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
                .Callback<IEnumerable<Treatment>, string, WriteOrigin, CancellationToken>((batch, _, _, _) => Crawled.AddRange(batch))
                .ReturnsAsync(true);
            treatments
                .Setup(p => p.PublishRecentTreatmentsAsync(
                    It.IsAny<IEnumerable<Treatment>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
                .Callback<IEnumerable<Treatment>, string, WriteOrigin, CancellationToken>((batch, _, _, _) => Republished.AddRange(batch))
                .ReturnsAsync((IEnumerable<Treatment> batch, string _, WriteOrigin _, CancellationToken _) =>
                    Written ?? batch.Count());
            treatments
                .Setup(p => p.GetStoredTreatmentIdsAsync(
                    It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .Callback<string, DateTime, DateTime, CancellationToken>((source, _, _, _) => StoredAskedOf.Add(source))
                .ReturnsAsync(() => Stored.ToDictionary(id => id, _ => StoredAt));
            treatments
                .Setup(p => p.DeleteTreatmentsAsync(
                    It.IsAny<string>(), It.IsAny<IReadOnlySet<string>>(), It.IsAny<CancellationToken>()))
                .Callback<string, IReadOnlySet<string>, CancellationToken>((source, ids, _) =>
                {
                    DeletedFrom.Add(source);
                    Deleted.Add(ids);
                })
                .ReturnsAsync((string _, IReadOnlySet<string> ids, CancellationToken _) => ids.Count);

            var publisher = new Mock<IConnectorPublisher>();
            publisher.Setup(p => p.IsAvailable).Returns(true);
            publisher.Setup(p => p.Glucose).Returns(Mock.Of<IGlucosePublisher>());
            publisher.Setup(p => p.Treatments).Returns(treatments.Object);
            publisher.Setup(p => p.Device).Returns(Mock.Of<IDevicePublisher>());
            var metadata = new Mock<IMetadataPublisher>();
            metadata
                .Setup(m => m.GetBackfillLowWaterMarkAsync(Source, "TreatmentsFullReconcile", It.IsAny<CancellationToken>()))
                .ReturnsAsync(LastFullReconcile);
            metadata
                .Setup(m => m.SetBackfillLowWaterMarkAsync(
                    Source, "TreatmentsFullReconcile", It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, DateTime?, CancellationToken>((_, _, at, _) => FullReconcileRecorded.Add(at))
                .Returns(Task.CompletedTask);
            publisher.Setup(p => p.Metadata).Returns(metadata.Object);

            var registration = new Mock<IConnectorRegistration<NightscoutConnectorConfiguration>>();
            registration.Setup(r => r.Defaults).Returns(new NightscoutConnectorConfiguration());

            return new NightscoutConnectorService(
                new HttpClient(this),
                Mock.Of<IConnectorServerResolver<NightscoutConnectorConfiguration>>(),
                NullLogger<NightscoutConnectorService>.Instance,
                Mock.Of<IRetryDelayStrategy>(),
                Mock.Of<IRateLimitingStrategy>(),
                registration.Object,
                publisher.Object,
                new FakeTimeProvider(At));
        }
    }
}
