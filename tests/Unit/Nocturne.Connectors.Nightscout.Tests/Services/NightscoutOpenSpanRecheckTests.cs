using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services;
using Nocturne.Connectors.Nightscout.Tests.TestSupport;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Tests.Shared.Connectors;
using Xunit;
using Nocturne.Tests.Shared.Mocks;

namespace Nocturne.Connectors.Nightscout.Tests.Services;

/// <summary>
/// Each catch-up reads again, by id, the overrides and temporary targets the connector holds as open
/// or indefinite, and publishes those the source has since given a duration as the crawl publishes.
/// </summary>
public class NightscoutOpenSpanRecheckTests
{
    private const string PositionKey = "OpenSpansRecheckedThrough";
    private const int PerSync = NightscoutConnectorServiceBase<NightscoutConnectorConfiguration>.OpenSpansPerSync;

    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset DaysAgo = Now.AddDays(-6);

    private const string Override1 = "aaaaaaaaaaaaaaaaaaaaaaa1";
    private const string Override2 = "aaaaaaaaaaaaaaaaaaaaaaa2";

    private static void Indefinite(JsonObject t)
    {
        t["eventType"] = "Temporary Override";
        t["enteredBy"] = "Loop";
        t["durationType"] = "indefinite";
    }

    private static void Ended(JsonObject t)
    {
        t.Remove("durationType");
        t["duration"] = 4320;
    }

    [Theory]
    [InlineData("15.0.8", 1)]
    [InlineData("15.0.6", 2)]
    public async Task An_override_ended_upstream_is_read_again_by_id_and_published(string version, int idReads)
    {
        var harness = new Harness(version);
        harness.Upstream.Add(Override1, DaysAgo, Indefinite);
        harness.Open.Add(new StoredTreatmentKey(Override1, DaysAgo.UtcDateTime));
        harness.Upstream.Edit(Override1, Ended);

        var result = await harness.SyncAsync();

        result.Success.Should().BeTrue();
        harness.Upstream.IdReads.Should().HaveCount(idReads,
            "15.0.6 matches an ObjectId only through a plain find[_id], so the batch read is followed by one");
        var published = harness.Published.SelectMany(b => b).Should().ContainSingle().Subject;
        published.Id.Should().Be(Override1);
        published.Duration.Should().Be(4320);
        published.DataSource.Should().Be(Harness.Source);
        harness.Republished.Should().BeEmpty("a span has no fingerprint for the recent rule to compare");
    }

    /// <summary>Loop's own id, kept as a string <c>_id</c> by releases before 15.0.8.</summary>
    [Fact]
    public async Task An_override_under_a_string_id_is_found_by_the_batch_read()
    {
        const string loopId = "0198c2a4-1f3b-7c2d-9e55-6a1b2c3d4e5f";
        var harness = new Harness("15.0.6");
        harness.Upstream.Add(loopId, DaysAgo, t => { Indefinite(t); Ended(t); }, asObjectId: false);
        harness.Open.Add(new StoredTreatmentKey(loopId, DaysAgo.UtcDateTime));

        await harness.SyncAsync();

        harness.Upstream.IdReads.Should().ContainSingle().Which.Should().Contain($"find[_id][$in][0]={loopId}");
        harness.Published.SelectMany(b => b).Should().ContainSingle().Which.Duration.Should().Be(4320);
    }

    [Fact]
    public async Task An_override_still_open_upstream_is_not_written_again()
    {
        var harness = new Harness("15.0.8");
        harness.Upstream.Add(Override1, DaysAgo, Indefinite);
        harness.Open.Add(new StoredTreatmentKey(Override1, DaysAgo.UtcDateTime));

        var result = await harness.SyncAsync();

        result.Success.Should().BeTrue();
        harness.Upstream.IdReads.Should().ContainSingle();
        harness.Published.Should().BeEmpty();
        harness.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task An_override_the_source_marked_invalid_is_deleted_not_published()
    {
        var harness = new Harness("15.0.8");
        harness.Upstream.Add(Override1, DaysAgo, t => { Indefinite(t); Ended(t); t["isValid"] = false; });
        harness.Upstream.Add(Override2, DaysAgo.AddHours(1), Indefinite);
        harness.Open.Add(new StoredTreatmentKey(Override1, DaysAgo.UtcDateTime));
        harness.Open.Add(new StoredTreatmentKey(Override2, DaysAgo.AddHours(1).UtcDateTime));

        await harness.SyncAsync();

        harness.Deleted.Should().ContainSingle().Which.Should().BeEquivalentTo([Override1]);
        harness.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task An_override_gone_from_the_source_is_deleted_once_one_it_holds_is_found()
    {
        var harness = new Harness("15.0.8");
        harness.Upstream.Add(Override2, DaysAgo.AddHours(1), Indefinite);
        harness.Open.Add(new StoredTreatmentKey(Override1, DaysAgo.UtcDateTime));
        harness.Open.Add(new StoredTreatmentKey(Override2, DaysAgo.AddHours(1).UtcDateTime));

        await harness.SyncAsync();

        harness.Deleted.Should().ContainSingle().Which.Should().BeEquivalentTo([Override1]);
    }

    [Fact]
    public async Task A_missing_override_is_kept_while_nothing_vouches_for_the_lookup()
    {
        var harness = new Harness("15.0.8");
        harness.Open.Add(new StoredTreatmentKey(Override1, DaysAgo.UtcDateTime));

        var result = await harness.SyncAsync();

        result.Success.Should().BeTrue();
        harness.Deleted.Should().BeEmpty();
    }

    /// <summary>The crawl's own read names a treatment the source holds, which the lookup must find first.</summary>
    [Fact]
    public async Task A_missing_override_is_deleted_once_a_treatment_the_crawl_read_is_found()
    {
        var harness = new Harness("15.0.8");
        harness.Upstream.Add("ccccccccccccccccccccccc1", Now.AddHours(-1), t =>
        {
            t["eventType"] = "Carb Correction";
            t["carbs"] = 10;
        });
        harness.Open.Add(new StoredTreatmentKey(Override1, DaysAgo.UtcDateTime));

        await harness.SyncAsync();

        harness.Deleted.Should().ContainSingle().Which.Should().BeEquivalentTo([Override1]);
    }

    /// <summary>
    /// A span stored under an uploader's own <c>id</c> is not an ObjectId, so the canary that vouches
    /// for the lookup must be a served key of that kind: the <c>id</c> of a treatment the crawl read.
    /// </summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task A_missing_override_under_a_client_id_is_confirmed_gone_by_a_served_client_id_canary(
        bool copyStillUpstream, bool deleted)
    {
        const string servedClientId = "11111111-1111-4111-8111-111111111111";
        const string spanId = "0198c2a4-1f3b-7c2d-9e55-6a1b2c3d4e5f";
        var harness = new Harness("15.0.8");
        harness.Upstream.Add("ccccccccccccccccccccccc1", Now.AddHours(-1), t =>
        {
            t["eventType"] = "Carb Correction";
            t["carbs"] = 10;
            t["id"] = servedClientId;
        });
        if (copyStillUpstream)
            harness.Upstream.Add(spanId.ToUpperInvariant(), DaysAgo, Indefinite, asObjectId: false);
        harness.Open.Add(new StoredTreatmentKey(spanId, DaysAgo.UtcDateTime));

        await harness.SyncAsync();

        harness.Upstream.IdReads.Should().Contain(q => q.Contains($"find[id]={servedClientId}"), "the canary is looked up by its id");
        harness.Deleted.Should().HaveCount(deleted ? 1 : 0);
        if (deleted)
            harness.Deleted.Single().Should().BeEquivalentTo([spanId]);
    }

    [Fact]
    public async Task More_than_a_few_missing_overrides_delete_none()
    {
        var harness = new Harness("15.0.8");
        harness.Upstream.Add(Override1, DaysAgo, Indefinite);
        harness.Open.Add(new StoredTreatmentKey(Override1, DaysAgo.UtcDateTime));
        for (var i = 0; i < 4; i++)
            harness.Open.Add(new StoredTreatmentKey($"bbbbbbbbbbbbbbbbbbbbbbb{i}", DaysAgo.AddHours(i + 1).UtcDateTime));

        await harness.SyncAsync();

        harness.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task A_backlog_is_read_a_few_at_a_time_oldest_first_and_then_again_from_the_start()
    {
        var harness = new Harness("15.0.8");
        for (var i = 0; i < PerSync * 2 + 5; i++)
        {
            var id = $"dddddddddddddddddddd{i:x4}";
            harness.Upstream.Add(id, DaysAgo.AddMinutes(i), Indefinite);
            harness.Open.Add(new StoredTreatmentKey(id, DaysAgo.AddMinutes(i).UtcDateTime));
        }

        await harness.SyncAsync();
        harness.State.Vendor[PositionKey].Should().NotBeNull();
        await harness.SyncAsync();
        await harness.SyncAsync();
        harness.State.Vendor.Should().NotContainKey(PositionKey, "the newest were read, so the next sync starts over");
        harness.Clock.Advance(TimeSpan.FromHours(7));
        await harness.SyncAsync();

        harness.Asked.Select(a => a?.LegacyId).Should().Equal(
            null, harness.Open[PerSync - 1].LegacyId, harness.Open[PerSync * 2 - 1].LegacyId, null);
        harness.Upstream.IdReads.Should().HaveCount(4);
        harness.Upstream.IdReads.Last().Should().Contain($"find[_id][$in][0]={harness.Open[0].LegacyId}");
    }

    [Fact]
    public async Task A_failed_publish_fails_the_sync_and_reads_the_same_spans_again()
    {
        var harness = new Harness("15.0.8") { FailPublish = true };
        for (var i = 0; i < PerSync + 1; i++)
        {
            var id = $"dddddddddddddddddddd{i:x4}";
            harness.Upstream.Add(id, DaysAgo.AddMinutes(i), t => { Indefinite(t); Ended(t); });
            harness.Open.Add(new StoredTreatmentKey(id, DaysAgo.AddMinutes(i).UtcDateTime));
        }

        var failed = await harness.SyncAsync();
        harness.FailPublish = false;
        var retried = await harness.SyncAsync();

        failed.Success.Should().BeFalse();
        retried.Success.Should().BeTrue();
        harness.Asked.Should().Equal([null, null], "the position does not move past spans that were not stored");
    }

    [Fact]
    public async Task A_failed_read_keeps_the_position_and_does_not_fail_the_sync()
    {
        var harness = new Harness("15.0.8");
        harness.Upstream.IdReadAnswer = HttpStatusCode.BadGateway;
        harness.Open.Add(new StoredTreatmentKey(Override1, DaysAgo.UtcDateTime));
        harness.State.Vendor[PositionKey] = System.Text.Json.JsonSerializer.SerializeToNode(new StoredTreatmentKey("x", DaysAgo.UtcDateTime.AddDays(-1)))!;

        var result = await harness.SyncAsync();

        result.Success.Should().BeTrue();
        harness.Published.Should().BeEmpty();
        harness.Deleted.Should().BeEmpty();
        harness.State.Vendor[PositionKey]!["LegacyId"]!.GetValue<string>().Should().Be("x");
    }

    /// <summary>
    /// A copy write-back left upstream under a <c>_id</c> the source minted, with the record's key as
    /// <c>identifier</c>, is published with that identifier, by which the store recognises its echo.
    /// </summary>
    [Fact]
    public async Task A_written_back_copy_is_published_with_its_identifier()
    {
        const string ownKey = "0198c2a4-1f3b-7c2d-9e55-6a1b2c3d4e5f";
        var harness = new Harness("15.0.8");
        harness.Upstream.Add("eeeeeeeeeeeeeeeeeeeeeee1", DaysAgo, t => { Indefinite(t); Ended(t); t["identifier"] = ownKey; });
        harness.Open.Add(new StoredTreatmentKey(ownKey, DaysAgo.UtcDateTime));

        await harness.SyncAsync();

        var published = harness.Published.SelectMany(b => b).Should().ContainSingle().Subject;
        published.UpstreamIdentifier.Should().Be(ownKey);
        harness.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task A_ranged_re_import_does_not_read_open_spans()
    {
        var harness = new Harness("15.0.8");
        harness.Open.Add(new StoredTreatmentKey(Override1, DaysAgo.UtcDateTime));

        await harness.SyncAsync(new SyncRequest
        {
            From = Now.UtcDateTime.AddDays(-2),
            To = Now.UtcDateTime.AddDays(-1),
            DataTypes = [SyncDataType.CarbIntake],
        });

        harness.Asked.Should().BeEmpty();
        harness.Upstream.IdReads.Should().BeEmpty();
    }

    /// <summary>
    /// A page of spans the source no longer has costs the batch read and one <c>find[_id]</c> each, then
    /// nothing more: too many are missing to look further or delete. One the source still holds open
    /// costs the batch read where <c>$in</c> casts an ObjectId, and a <c>find[_id]</c> where it does not.
    /// Either way the page is not read again inside the period.
    /// </summary>
    [Theory]
    [InlineData("15.0.6", false, 1 + PerSync)]
    [InlineData("15.0.8", false, 1 + PerSync)]
    [InlineData("15.0.6", true, 1 + PerSync)]
    [InlineData("15.0.8", true, 1)]
    public async Task A_page_of_open_spans_costs_a_bounded_number_of_reads_and_none_again_inside_the_period(
        string version, bool stillOpen, int idReads)
    {
        var crawlOnly = new Harness(version);
        await crawlOnly.SyncAsync();
        var crawlReads = crawlOnly.Upstream.Reads.Count;

        var harness = new Harness(version);
        for (var i = 0; i < PerSync; i++)
        {
            var id = $"dddddddddddddddddddd{i:x4}";
            if (stillOpen)
                harness.Upstream.Add(id, DaysAgo.AddMinutes(i), Indefinite);
            harness.Open.Add(new StoredTreatmentKey(id, DaysAgo.AddMinutes(i).UtcDateTime));
        }

        await harness.SyncAsync();
        harness.Upstream.IdReads.Should().HaveCount(idReads);
        harness.Upstream.Reads.Should().HaveCount(crawlReads + idReads, "no range read confirms a span gone");

        harness.Upstream.Reads.Clear();
        harness.Clock.Advance(TimeSpan.FromMinutes(30));
        await harness.SyncAsync();

        harness.Upstream.IdReads.Should().BeEmpty();
        harness.Upstream.Reads.Should().HaveCount(crawlReads);
        harness.Deleted.Should().BeEmpty();
        harness.Published.Should().BeEmpty();
    }

    /// <summary>
    /// The batch read finds one override, but the source answers no find by a single id, so the
    /// lookup that found the other missing proves nothing.
    /// </summary>
    [Fact]
    public async Task A_missing_override_is_kept_when_the_lookup_cannot_find_one_the_source_holds()
    {
        var harness = new Harness("15.0.8");
        harness.Upstream.Add(Override2, DaysAgo.AddHours(1), Indefinite);
        harness.Open.Add(new StoredTreatmentKey(Override1, DaysAgo.UtcDateTime));
        harness.Open.Add(new StoredTreatmentKey(Override2, DaysAgo.AddHours(1).UtcDateTime));
        harness.Upstream.FindsNothing = query =>
            query.Contains("find[_id]=", StringComparison.Ordinal)
            || query.Contains("find[id]", StringComparison.Ordinal)
            || query.Contains("find[identifier]", StringComparison.Ordinal);

        var result = await harness.SyncAsync();

        result.Success.Should().BeTrue();
        harness.Upstream.IdReads.Should().Contain(q => q.Contains($"find[_id]={Override2}"), "the canary is looked up");
        harness.Deleted.Should().BeEmpty();
    }

    /// <summary>
    /// 15.0.6 kept a 24-hex <c>_id</c> a client sent as a string. 15.0.8 casts it in every find by
    /// <c>_id</c>, so only a read of its <c>created_at</c> range finds it.
    /// </summary>
    [Fact]
    public async Task An_override_under_a_string_24_hex_id_is_not_deleted_on_a_release_that_casts_it()
    {
        var harness = new Harness("15.0.8");
        harness.Upstream.Add(Override1, DaysAgo, t => { Indefinite(t); Ended(t); }, asObjectId: false);
        harness.Upstream.Add(Override2, DaysAgo.AddHours(1), Indefinite);
        harness.Open.Add(new StoredTreatmentKey(Override1, DaysAgo.UtcDateTime));
        harness.Open.Add(new StoredTreatmentKey(Override2, DaysAgo.AddHours(1).UtcDateTime));

        await harness.SyncAsync();

        harness.Upstream.IdReads.Should().Contain(q => q.Contains($"find[_id]={Override1}"), "a find by _id is tried first");
        harness.Deleted.Should().BeEmpty();
        harness.Published.SelectMany(b => b).Should().ContainSingle().Which.Id.Should().Be(Override1);
    }

    /// <summary>
    /// The override running now is read again after an hour, so its end reaches the store that soon.
    /// One that started days ago, or a span already closed, waits out the longer period.
    /// </summary>
    [Fact]
    public async Task A_recent_running_override_is_read_again_after_an_hour_and_an_older_or_closed_one_is_not()
    {
        const string running = "aaaaaaaaaaaaaaaaaaaaaab1";
        const string olderRunning = "aaaaaaaaaaaaaaaaaaaaaab2";
        const string closed = "aaaaaaaaaaaaaaaaaaaaaab3";
        var harness = new Harness("15.0.8");
        harness.Upstream.Add(olderRunning, DaysAgo, Indefinite);
        harness.Upstream.Add(closed, Now.AddHours(-3), Indefinite);
        harness.Upstream.Add(running, Now.AddHours(-2), Indefinite);
        harness.Open.Add(new StoredTreatmentKey(olderRunning, DaysAgo.UtcDateTime, Running: true));
        harness.Open.Add(new StoredTreatmentKey(closed, Now.AddHours(-3).UtcDateTime, Running: false));
        harness.Open.Add(new StoredTreatmentKey(running, Now.AddHours(-2).UtcDateTime, Running: true));

        await harness.SyncAsync();
        harness.Published.Should().BeEmpty();
        var firstSync = harness.Upstream.IdReads.Count();

        harness.Upstream.Edit(running, Ended);
        harness.Clock.Advance(TimeSpan.FromMinutes(61));
        await harness.SyncAsync();

        harness.Upstream.IdReads.Skip(firstSync).Should().ContainSingle()
            .Which.Should().Contain(running).And.NotContain(olderRunning).And.NotContain(closed);
        var published = harness.Published.SelectMany(b => b).Should().ContainSingle().Subject;
        published.Id.Should().Be(running);
        published.Duration.Should().Be(4320);
    }

    /// <summary>
    /// A publish that leaves the span open, as one the store applies to another record would, does not
    /// make the span cost reads every sync.
    /// </summary>
    [Fact]
    public async Task An_override_published_as_ended_is_not_read_again_inside_the_period_while_its_span_stays_open()
    {
        var harness = new Harness("15.0.8");
        harness.Upstream.Add(Override1, DaysAgo, t => { Indefinite(t); Ended(t); });
        harness.Open.Add(new StoredTreatmentKey(Override1, DaysAgo.UtcDateTime));

        await harness.SyncAsync();
        harness.Published.Should().ContainSingle();
        var firstSync = harness.Upstream.IdReads.Count();

        harness.Clock.Advance(TimeSpan.FromMinutes(30));
        await harness.SyncAsync();

        harness.Upstream.IdReads.Should().HaveCount(firstSync);
        harness.Published.Should().ContainSingle();
    }

    /// <summary>
    /// The canary is found, but the read of the missing override's <c>created_at</c> range fails, so
    /// nothing confirms it gone. The spans are read again next sync.
    /// </summary>
    [Fact]
    public async Task A_failed_range_read_deletes_nothing_marks_nothing_and_does_not_fail_the_sync()
    {
        var harness = new Harness("15.0.8");
        harness.Upstream.Add(Override2, DaysAgo.AddHours(1), Indefinite);
        harness.Open.Add(new StoredTreatmentKey(Override1, DaysAgo.UtcDateTime));
        harness.Open.Add(new StoredTreatmentKey(Override2, DaysAgo.AddHours(1).UtcDateTime));
        Func<string, bool> rangeRead = query =>
            query.Contains("[$gte]=2026-02-22T21:30", StringComparison.Ordinal)
            && !query.Contains("find[_id]", StringComparison.Ordinal)
            && !query.Contains("find[id]", StringComparison.Ordinal)
            && !query.Contains("find[identifier]", StringComparison.Ordinal);
        harness.Upstream.FailsWithBadGateway = rangeRead;

        var result = await harness.SyncAsync();

        result.Success.Should().BeTrue();
        harness.Upstream.IdReads.Should().Contain(q => q.Contains($"find[_id]={Override2}"), "the canary is found first");
        harness.Upstream.Reads.Should().Contain(uri => rangeRead(Uri.UnescapeDataString(uri.Query)),
            "the range read is made");
        harness.Deleted.Should().BeEmpty();

        harness.Upstream.FailsWithBadGateway = null;
        var firstSync = harness.Upstream.IdReads.Count();
        harness.Clock.Advance(TimeSpan.FromMinutes(30));
        await harness.SyncAsync();

        harness.Upstream.IdReads.Skip(firstSync).First().Should()
            .Contain($"={Override1}").And.Contain($"={Override2}", "neither span was marked");
    }

    private sealed class Harness(string version)
    {
        public const string Source = "nightscout-connector";

        /// <summary>Its own, as what was looked up is remembered per source URL for every service.</summary>
        private string Url { get; } = $"https://{Guid.NewGuid():N}.ns.example";

        public FakeNightscoutTreatments Upstream { get; } = new(version) { Now = Now };
        public FakeTimeProvider Clock { get; } = new(Now);

        private readonly NightscoutLookups _lookups = new();
        public InMemoryConnectorStateStore State { get; } = NewState();

        /// <summary>The spans the store holds as open, oldest first.</summary>
        public List<StoredTreatmentKey> Open { get; } = [];

        public bool FailPublish { get; set; }

        /// <summary>The position each query for open spans started after.</summary>
        public List<StoredTreatmentKey?> Asked { get; } = [];
        public List<List<Treatment>> Published { get; } = [];
        public List<Treatment> Republished { get; } = [];
        public List<IReadOnlySet<string>> Deleted { get; } = [];

        private static InMemoryConnectorStateStore NewState()
        {
            var state = new InMemoryConnectorStateStore();
            state.Resume["TreatmentsFullReconcile"] = Now.UtcDateTime.AddMinutes(-20);
            return state;
        }

        public Task<SyncResult> SyncAsync(SyncRequest? request = null) =>
            NewService().SyncDataAsync(
                request ?? new SyncRequest { From = Clock.GetUtcNow().UtcDateTime.AddMinutes(-5), DataTypes = [SyncDataType.CarbIntake] },
                new NightscoutConnectorConfiguration { Url = Url, ApiSecret = "secret" },
                CancellationToken.None);

        private IReadOnlyList<StoredTreatmentKey> OpenAfter(StoredTreatmentKey? after, int limit)
        {
            Asked.Add(after);
            return Open
                .Where(o => after is null || o.At > after.At
                            || (o.At == after.At && string.CompareOrdinal(o.LegacyId, after.LegacyId) > 0))
                .Take(limit)
                .ToList();
        }

        private NightscoutConnectorService NewService()
        {
            var treatments = new Mock<ITreatmentPublisher>();
            treatments
                .Setup(p => p.GetLatestTreatmentTimestampAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Clock.GetUtcNow().UtcDateTime.AddMinutes(-5));
            treatments
                .Setup(p => p.GetOpenSpanTreatmentsAsync(
                    Source, It.IsAny<StoredTreatmentKey?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string _, StoredTreatmentKey? after, int limit, CancellationToken _) => OpenAfter(after, limit));
            treatments
                .Setup(p => p.PublishTreatmentsAsync(
                    It.IsAny<IEnumerable<Treatment>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IEnumerable<Treatment> batch, string _, WriteOrigin _, CancellationToken _) =>
                {
                    if (FailPublish)
                        return false;
                    Published.Add(batch.ToList());
                    return true;
                });
            treatments
                .Setup(p => p.PublishRecentTreatmentsAsync(
                    It.IsAny<IEnumerable<Treatment>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
                .Callback<IEnumerable<Treatment>, string, WriteOrigin, CancellationToken>((batch, _, _, _) => Republished.AddRange(batch))
                .ReturnsAsync((IEnumerable<Treatment> batch, string _, WriteOrigin _, CancellationToken _) => batch.Count());
            treatments
                .Setup(p => p.GetStoredTreatmentIdsAsync(
                    It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Dictionary<string, DateTime>());
            treatments
                .Setup(p => p.DeleteTreatmentsAsync(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>>(), It.IsAny<CancellationToken>()))
                .Callback<string, IReadOnlySet<string>, CancellationToken>((_, ids, _) => Deleted.Add(ids))
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
                new HttpClient(Upstream),
                Mock.Of<IConnectorServerResolver<NightscoutConnectorConfiguration>>(),
                NullLogger<NightscoutConnectorService>.Instance,
                Mock.Of<IRetryDelayStrategy>(),
                registration.Object,
                _lookups,
                MockTenantAccessor.Create().Object,
                publisher.Object,
                Clock);
        }
    }
}
