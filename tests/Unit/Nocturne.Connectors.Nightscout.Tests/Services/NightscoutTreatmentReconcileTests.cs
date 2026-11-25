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
using Nocturne.Tests.Shared.Connectors;
using Nocturne.Tests.Shared.Mocks;
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
    private const string MongoIdB = "bbbbbbbbbbbbbbbbbbbbbbb2";

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

        harness.Lookups.Should().Equal($"find[id]={TrioKept}", $"find[identifier][$in]={TrioKept},{MongoObjectId.Coerce(TrioKept)}");
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
    public async Task Another_source_with_a_later_clock_does_not_forget_this_sources_lookups()
    {
        var memory = new NightscoutLookups();
        var harness = new Harness
        {
            LookupMemory = memory,
            Upstream = [Trio(MongoIdA, TrioKept, Now.AddMinutes(-30))],
            Stored = [TrioKept, TrioGone],
            AlsoUpstream = [TrioGone],
        };
        var later = Now.AddDays(7);
        var elsewhere = new Harness
        {
            LookupMemory = memory,
            At = later,
            LatestStored = later.AddMinutes(-10),
            Upstream = [Trio(MongoIdB, TrioKept, later.AddMinutes(-30))],
            Stored = [TrioKept, LoopGone],
            AlsoUpstream = [LoopGone],
        };

        await harness.SyncAsync();
        var firstLookups = harness.Lookups.Count;
        await elsewhere.SyncAsync();
        await harness.SyncAsync();

        elsewhere.Lookups.Should().Contain(l => l.EndsWith(LoopGone), "the later source looked its own treatment up");
        harness.Lookups.Skip(firstLookups).Should().NotContain($"find[id]={TrioGone}");
    }

    [Fact]
    public async Task Another_tenants_lookup_on_the_same_site_does_not_stand_for_this_tenants()
    {
        var memory = new NightscoutLookups();
        const string site = "https://shared.ns.example";
        var first = new Harness
        {
            LookupMemory = memory,
            Url = site,
            Tenant = Guid.NewGuid(),
            Upstream = [Trio(MongoIdA, TrioKept, Now.AddMinutes(-30))],
            Stored = [TrioKept, TrioGone],
            AlsoUpstream = [TrioGone],
        };
        var second = new Harness
        {
            LookupMemory = memory,
            Url = site,
            Tenant = Guid.NewGuid(),
            Upstream = [Trio(MongoIdA, TrioKept, Now.AddMinutes(-30))],
            Stored = [TrioKept, TrioGone],
        };

        await first.SyncAsync();
        await second.SyncAsync();

        first.Deleted.Should().BeEmpty();
        second.Lookups.Should().Contain($"find[id]={TrioGone}", "the first tenant's lookup was of its own copy");
        second.Deleted.Should().ContainSingle().Which.Should().BeEquivalentTo([TrioGone]);
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
        harness.CrawledIds.Should().Equal(LoopKept);
        harness.RepublishedIds.Should().BeEquivalentTo([MongoIdA, LoopGone]);
        harness.Republished.Should().OnlyContain(t => t.DataSource == Source);
    }

    [Fact]
    public async Task Carb_equivalents_sharing_trios_id_are_each_published_under_their_own_object_id()
    {
        var harness = new Harness
        {
            Upstream = [Trio(MongoIdA, TrioKept, Now.AddMinutes(-40)), Trio(MongoIdB, TrioKept, Now.AddMinutes(-30))],
            Stored = [TrioKept],
        };

        await harness.SyncAsync();

        harness.RepublishedIds.Should().BeEquivalentTo([MongoIdA, MongoIdB]);
        harness.Republished.Should().OnlyContain(t => TreatmentClientId.Of(t) == TrioKept);
        harness.Lookups.Should().BeEmpty("a row stored under the id the read carries is not missing");
        harness.Deleted.Should().BeEmpty();
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

    private static readonly List<SyncDataType> EveryTreatmentType =
    [
        SyncDataType.Boluses, SyncDataType.CarbIntake, SyncDataType.ManualBG,
        SyncDataType.BolusCalculations, SyncDataType.Notes, SyncDataType.DeviceEvents,
    ];

    /// <summary>
    /// A reset of every treatment type forgets the last full reconcile, so the catch-up after it
    /// reconciles in full however recent the last one was. The reset run itself is bounded and
    /// reconciles nothing.
    /// </summary>
    [Fact]
    public async Task The_catch_up_after_a_reset_reconciles_in_full_whenever_the_last_reconcile_was()
    {
        var harness = new Harness
        {
            LastFullReconcile = Now.UtcDateTime.AddMinutes(-20),
            Upstream = [Trio(MongoIdA, TrioKept, Now.AddMinutes(-30))],
        };

        await harness.SyncAsync(SyncRequest.Reset(Now.UtcDateTime.AddMinutes(-5), Now.UtcDateTime, EveryTreatmentType));
        harness.FullReconcileRecorded.Should().Equal([(DateTime?)null], "the reset removed the stamp");
        await harness.SyncAsync();

        harness.FullReconcileRecorded.Should().Equal(null, Now.UtcDateTime);
    }

    /// <summary>
    /// The reconcile stamp schedules every treatment type, so a reset of only some of them keeps it
    /// and the catch-up after it reconciles as it would have.
    /// </summary>
    [Fact]
    public async Task A_reset_of_one_treatment_type_keeps_the_last_full_reconcile()
    {
        var harness = new Harness
        {
            LastFullReconcile = Now.UtcDateTime.AddMinutes(-20),
            Upstream = [Trio(MongoIdA, TrioKept, Now.AddMinutes(-30))],
        };

        await harness.SyncAsync(SyncRequest.Reset(Now.UtcDateTime.AddMinutes(-5), Now.UtcDateTime, [SyncDataType.CarbIntake]));
        await harness.SyncAsync();

        harness.FullReconcileRecorded.Should().BeEmpty("the reset kept the stamp and the catch-up ran no full reconcile");
        harness.CrawlLowerBound.Should().Be(Now.UtcDateTime.AddMinutes(-5), "a full reconcile reads back 25 hours");
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
        harness.RepublishedIds.Should().Equal(MongoIdA);
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

    /// <summary>
    /// Nocturne stores a treatment it wrote back under its own key, and Nightscout 15.0.7+ returns
    /// the copy under a <c>_id</c> it minted, with that key as <c>identifier</c>. The read therefore
    /// counts the identifier, and the lookup falls back to it, or the treatment would be deleted as
    /// gone from the source.
    /// </summary>
    [Fact]
    public async Task A_treatment_nocturne_wrote_back_is_recognised_by_its_identifier()
    {
        const string ownKey = "0198c2a4-1f3b-7c2d-9e55-6a1b2c3d4e5f";
        const string missingKey = "0198c2a4-1f3b-7c2d-9e55-000000000000";
        var harness = new Harness
        {
            Upstream =
            [
                Trio(MongoIdB, TrioKept, Now.AddMinutes(-20)),
                WrittenBack(MongoIdA, ownKey, Now.AddMinutes(-30)),
            ],
            Stored = [ownKey, TrioKept, missingKey],
            AlsoUpstreamByIdentifier = [missingKey],
        };

        await harness.SyncAsync();

        harness.Deleted.Should().BeEmpty();
        harness.Lookups.Should().ContainInOrder(
            $"find[id]={missingKey}", $"find[identifier][$in]={missingKey},{MongoObjectId.Coerce(missingKey)}");
        harness.Lookups.Should().NotContain(l => l.Contains(ownKey), "the read served it as an identifier, so it is neither missing nor the canary");
    }

    /// <summary>
    /// A row written before write-back coerced its key is relabelled to the connector under that
    /// key, while its copy upstream sits under the key's coerced form as <c>identifier</c>, the uuid
    /// prefix or the hash. Missing from the read, it is looked up under both forms at once, and kept.
    /// </summary>
    [Theory]
    [InlineData("0198c2a4-1f3b-7c2d-9e55-0000000000aa")]
    [InlineData("pump-history-42")]
    public async Task A_relabelled_row_whose_copy_sits_under_its_coerced_key_is_kept(string storedKey)
    {
        var coerced = MongoObjectId.Coerce(storedKey)!;
        var harness = new Harness
        {
            Upstream = [Trio(MongoIdB, TrioKept, Now.AddMinutes(-20))],
            Stored = [TrioKept, storedKey],
            AlsoUpstreamByIdentifier = [coerced],
        };

        await harness.SyncAsync();

        harness.Deleted.Should().BeEmpty();
        harness.Lookups.Should().Contain($"find[identifier][$in]={storedKey},{coerced}");
        harness.Lookups.Count(l => l.StartsWith("find[identifier]", StringComparison.Ordinal)).Should().Be(1,
            "the canary was found by id, and the stored key's forms share one query");
    }

    private static string WrittenBack(string mongoId, string identifier, DateTimeOffset at) =>
        $$"""{"_id":"{{mongoId}}","identifier":"{{identifier}}","enteredBy":"nocturne","eventType":"Carb Correction","carbs":20,"created_at":"{{at.UtcDateTime:o}}"}""";

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
        /// <summary>What the connector remembers of its lookups; give two harnesses one to share it.</summary>
        public NightscoutLookups LookupMemory { get; init; } = new();

        public Guid Tenant { get; init; } = MockTenantAccessor.DefaultTenantId;

        public string Url { get; init; } = $"https://{Guid.NewGuid():N}.ns.example";

        public DateTimeOffset At { get; init; } = Now;
        public DateTimeOffset LatestStored { get; init; } = Now.AddMinutes(-10);
        public List<string> Upstream { get; init; } = [];
        public HashSet<string> AlsoUpstream { get; init; } = [];
        public HashSet<string> AlsoUpstreamByIdentifier { get; init; } = [];
        public HashSet<string> Stored { get; init; } = [];
        public bool ReadFails { get; init; }
        public bool LookupFails { get; init; }
        public bool LookupFindsNothing { get; init; }
        private const string FullReconcileKey = "TreatmentsFullReconcile";

        public DateTime? LastFullReconcile { get; init; }
        public int? Written { get; init; }

        public List<Treatment> Republished { get; } = [];

        /// <summary>The ids the crawl offered the store, as the source served them.</summary>
        public List<string?> CrawledIds { get; } = [];

        /// <summary>The ids the reconcile offered the store, as the source served them.</summary>
        public List<string?> RepublishedIds { get; } = [];
        public List<IReadOnlySet<string>> Deleted { get; } = [];
        public List<string> DeletedFrom { get; } = [];
        public List<string> StoredAskedOf { get; } = [];
        public List<string> Lookups { get; } = [];
        public List<string> LookupUrls { get; } = [];
        public List<DateTime?> FullReconcileRecorded { get; } = [];

        /// <summary>The connector's state, kept across this harness's runs; seeded on first use.</summary>
        private InMemoryConnectorStateStore State => _state ??= NewState();

        private InMemoryConnectorStateStore? _state;

        private InMemoryConnectorStateStore NewState()
        {
            InMemoryConnectorStateStore state = null!;
            state = new InMemoryConnectorStateStore
            {
                OnWrite = key =>
                {
                    if (key == FullReconcileKey)
                        FullReconcileRecorded.Add(state.Resume.TryGetValue(key, out var at) ? at : null);
                },
            };
            if (LastFullReconcile is { } last)
                state.Resume[FullReconcileKey] = last;
            return state;
        }
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
            var byIdentifier = System.Text.RegularExpressions.Regex.Matches(url, @"&find\[identifier\]\[\$in\]\[\d+\]=([^&]+)");
            if (lookup.Success || byIdentifier.Count > 0)
            {
                var (field, ids) = lookup.Success
                    ? (lookup.Groups[1].Value, new[] { lookup.Groups[2].Value })
                    : ("identifier", byIdentifier.Select(m => m.Groups[1].Value).ToArray());
                Lookups.Add(lookup.Success ? $"find[{field}]={ids[0]}" : $"find[identifier][$in]={string.Join(',', ids)}");
                LookupUrls.Add(url);
                if (LookupFails)
                    return Task.FromResult(Failure());

                var found = !LookupFindsNothing && ids.Any(id =>
                    (field == "identifier" ? AlsoUpstreamByIdentifier : AlsoUpstream).Contains(id)
                    || Upstream.Any(doc => doc.Contains($"\"{field}\":\"{id}\"", StringComparison.Ordinal)));
                return Task.FromResult(Json(found ? [Upstream.FirstOrDefault() ?? "{}"] : []));
            }

            TreatmentReads++;
            var gte = System.Text.RegularExpressions.Regex.Match(url, @"find\[created_at\]\[\$gte\]=([^&]+)");
            if (gte.Success && CrawlLowerBound is null)
                CrawlLowerBound = DateTime.Parse(gte.Groups[1].Value, null,
                    System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime().AddHours(14);

            return Task.FromResult(ReadFails ? Failure() : Json(Upstream));
        }

        /// <summary>The store names each treatment it keeps by an id of its own, which the source does not know.</summary>
        private static void RenameAsStored(IEnumerable<Treatment> published)
        {
            foreach (var treatment in published)
                treatment.Id = Guid.CreateVersion7().ToString();
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
                .Callback<IEnumerable<Treatment>, string, WriteOrigin, CancellationToken>((batch, _, _, _) =>
                {
                    var list = batch.ToList();
                    CrawledIds.AddRange(list.Select(t => t.Id));
                    RenameAsStored(list);
                })
                .ReturnsAsync(true);
            treatments
                .Setup(p => p.PublishRecentTreatmentsAsync(
                    It.IsAny<IEnumerable<Treatment>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
                .Callback<IEnumerable<Treatment>, string, WriteOrigin, CancellationToken>((batch, _, _, _) =>
                {
                    var list = batch.ToList();
                    Republished.AddRange(list);
                    RepublishedIds.AddRange(list.Select(t => t.Id));
                    RenameAsStored(list);
                })
                .ReturnsAsync((IEnumerable<Treatment> batch, string _, WriteOrigin _, CancellationToken _) =>
                    Written ?? batch.Count());
            treatments
                .Setup(p => p.GetStoredTreatmentIdsAsync(
                    It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .Callback<string, DateTime, DateTime, CancellationToken>((source, _, _, _) => StoredAskedOf.Add(source))
                .ReturnsAsync(() => Stored.ToDictionary(id => id, _ => StoredAt));
            treatments
                .Setup(p => p.GetOpenSpanTreatmentsAsync(
                    It.IsAny<string>(), It.IsAny<StoredTreatmentKey?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
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
            publisher.Setup(p => p.Metadata).Returns(Mock.Of<IMetadataPublisher>());
            publisher.Setup(p => p.State).Returns(State);

            var registration = new Mock<IConnectorRegistration<NightscoutConnectorConfiguration>>();
            registration.Setup(r => r.Defaults).Returns(new NightscoutConnectorConfiguration());

            return new NightscoutConnectorService(
                new HttpClient(this),
                Mock.Of<IConnectorServerResolver<NightscoutConnectorConfiguration>>(),
                NullLogger<NightscoutConnectorService>.Instance,
                Mock.Of<IRetryDelayStrategy>(),
                registration.Object,
                LookupMemory,
                MockTenantAccessor.Create(Tenant).Object,
                publisher.Object,
                new FakeTimeProvider(At));
        }
    }
}
