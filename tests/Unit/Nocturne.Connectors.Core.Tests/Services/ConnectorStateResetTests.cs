using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Tests.Shared.Connectors;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Services;

/// <summary>
///     What a cursor reset forgets, and when: once the reset run has signed in, the stamps whose
///     types it re-pulls, then each crawled collection's reach mark as the crawl reaches it. Owed
///     crawl marks stay, and a run that is not a reset clears nothing.
/// </summary>
public class ConnectorStateResetTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTime Earlier = Now.UtcDateTime.AddDays(-2);

    public class TestConfig : BaseConnectorConfiguration;

    private sealed class ProbeConnector(IConnectorPublisher publisher, bool clearsVendor = false)
        : BaseConnectorService<TestConfig>(
            new HttpClient(),
            new ConnectorServerResolver<TestConfig>(null, null, null),
            NullLogger<ProbeConnector>.Instance,
            new RecordingRetryDelayStrategy(),
            publisher,
            new FakeTimeProvider(Now))
    {
        public bool SignsIn { get; init; } = true;
        public bool SaysItSignedIn { get; init; } = true;
        public bool PageFails { get; init; }
        public DateTime? StampSeenByTheRun { get; private set; }
        public IReadOnlyDictionary<string, DateTime>? ResumeAtFirstPage { get; private set; }

        protected override string ConnectorSource => "probe-connector";
        public override string ServiceName => "Probe";
        public override List<SyncDataType> SupportedDataTypes => [SyncDataType.Glucose, SyncDataType.Boluses];
        protected override bool ResetClearsVendorState => clearsVendor;

        protected override IReadOnlyDictionary<string, IReadOnlyCollection<SyncDataType>> ResumeStamps { get; } =
            new Dictionary<string, IReadOnlyCollection<SyncDataType>>
            {
                ["full-walk"] = [SyncDataType.Glucose, SyncDataType.Boluses],
                ["boluses-reconcile"] = [SyncDataType.Boluses],
            };

        protected override Task<bool> EnsureAuthenticatedAsync(TestConfig config, CancellationToken cancellationToken) =>
            Task.FromResult(SignsIn);

        protected override bool SignsInBeforeSync => SaysItSignedIn;

        protected override async Task<SyncResult> PerformSyncInternalAsync(
            SyncRequest request, TestConfig config, CancellationToken cancellationToken)
        {
            StampSeenByTheRun = await ResumeStampAsync("full-walk", cancellationToken);
            if (!ResolveActiveTypes(request, config).Contains(SyncDataType.Glucose))
                return new SyncResult { Success = true };

            await CrawlAndPublishAsync<DateTime>(
                "Glucose", request.From, request.To,
                (_, _) => PagesAsync(),
                timeOf: at => at,
                publishAsync: _ => Task.FromResult(true),
                pageSize: 10,
                cancellationToken);
            return new SyncResult { Success = true };
        }

        private async IAsyncEnumerable<DateTime[]> PagesAsync()
        {
            ResumeAtFirstPage ??= await Publisher!.State.ResumeAsync(ConnectorSource);
            if (PageFails)
                throw new HttpRequestException("the source failed");
            yield break;
        }
    }

    private static InMemoryConnectorStateStore SeededStore()
    {
        var store = new InMemoryConnectorStateStore();
        store.Resume["full-walk"] = Earlier;
        store.Resume["boluses-reconcile"] = Earlier;
        store.Resume["undeclared"] = Earlier;
        store.Resume["Glucose"] = Earlier.AddDays(-30);
        store.Resume["Glucose.GapFrom"] = Earlier.AddDays(-10);
        store.Resume["Glucose.GapTo"] = Earlier.AddDays(-5);
        store.Resume["Glucose.Through"] = Earlier;
        store.Resume["Boluses"] = Earlier.AddDays(-30);
        store.Resume["Boluses.Through"] = Earlier;
        store.Vendor["/api/v2/cgm/egvs"] = JsonNode.Parse("""{"LastUpdatedAt":"2026-11-20","LastGuid":"g"}""")!;
        return store;
    }

    public static TheoryData<int, bool, bool> ResetRule => new()
    {
        { (int)BaseConnectorService<TestConfig>.ResumeKeyKind.OwedMark, true, false },
        { (int)BaseConnectorService<TestConfig>.ResumeKeyKind.OwedMark, false, false },
        { (int)BaseConnectorService<TestConfig>.ResumeKeyKind.ReachMark, true, true },
        { (int)BaseConnectorService<TestConfig>.ResumeKeyKind.ReachMark, false, false },
        { (int)BaseConnectorService<TestConfig>.ResumeKeyKind.Stamp, true, true },
        { (int)BaseConnectorService<TestConfig>.ResumeKeyKind.Stamp, false, false },
    };

    [Theory]
    [MemberData(nameof(ResetRule))]
    public void A_reset_keeps_owed_crawl_marks_and_clears_reach_marks_and_stamps_only_for_what_it_re_pulls(
        int kind, bool repulled, bool cleared)
    {
        BaseConnectorService<TestConfig>.ResetClears((BaseConnectorService<TestConfig>.ResumeKeyKind)kind, repulled)
            .Should().Be(cleared);
    }

    [Fact]
    public async Task A_reset_whose_sign_in_fails_leaves_the_state_exactly_as_it_was()
    {
        var store = SeededStore();
        var before = new Dictionary<string, DateTime>(store.Resume);
        var probe = new ProbeConnector(new StoringConnectorPublisher(store), clearsVendor: true) { SignsIn = false };

        var result = await probe.SyncDataAsync(
            SyncRequest.Reset(null, Now.UtcDateTime, null), new TestConfig(), CancellationToken.None);

        result.Success.Should().BeFalse("the sign-in failed");
        store.Resume.Should().BeEquivalentTo(before);
        store.Vendor.Keys.Should().Equal("/api/v2/cgm/egvs");
    }

    [Fact]
    public async Task A_reset_of_every_type_clears_the_declared_stamps_and_the_crawled_reach_mark_and_keeps_the_owed_marks()
    {
        var store = SeededStore();
        var probe = new ProbeConnector(new StoringConnectorPublisher(store));

        (await probe.SyncDataAsync(SyncRequest.Reset(Earlier, Now.UtcDateTime, null), new TestConfig(), CancellationToken.None))
            .Success.Should().BeTrue();

        probe.StampSeenByTheRun.Should().BeNull("the run reads no stamp from before the reset");
        probe.ResumeAtFirstPage!.Keys.Should().BeEquivalentTo(
            ["undeclared", "Glucose", "Glucose.GapFrom", "Glucose.GapTo", "Boluses", "Boluses.Through"],
            "the crawl cleared its collection's reach mark before it fetched, and kept what it owes");
        store.Resume.Should().ContainKeys("undeclared", "Boluses", "Boluses.Through")
            .And.NotContainKeys("full-walk", "boluses-reconcile", "Glucose.Through");
        store.Vendor.Keys.Should().Equal("/api/v2/cgm/egvs");
    }

    [Fact]
    public async Task A_reset_of_some_types_clears_only_the_stamps_those_types_cover_and_the_marks_of_what_it_crawls()
    {
        var store = SeededStore();
        var probe = new ProbeConnector(new StoringConnectorPublisher(store), clearsVendor: true);

        (await probe.SyncDataAsync(
                SyncRequest.Reset(Earlier, Now.UtcDateTime, [SyncDataType.Boluses]), new TestConfig(), CancellationToken.None))
            .Success.Should().BeTrue();

        store.Resume.Keys.Should().BeEquivalentTo(
            ["full-walk", "undeclared", "Glucose", "Glucose.GapFrom", "Glucose.GapTo", "Glucose.Through",
             "Boluses", "Boluses.Through"],
            "the walk stamp also governs glucose, and the run crawled no collection");
        store.Vendor.Should().NotBeEmpty("the reset does not re-pull every type");
    }

    [Fact]
    public async Task A_reset_whose_first_page_fails_keeps_the_owed_marks()
    {
        var store = SeededStore();
        var probe = new ProbeConnector(new StoringConnectorPublisher(store)) { PageFails = true };

        await probe.Invoking(p => p.SyncDataAsync(
                SyncRequest.Reset(null, Now.UtcDateTime, [SyncDataType.Glucose]), new TestConfig(), CancellationToken.None))
            .Should().ThrowAsync<HttpRequestException>();

        store.Resume.Should().ContainKeys("Glucose", "Glucose.GapFrom", "Glucose.GapTo", "full-walk", "boluses-reconcile")
            .And.NotContainKey("Glucose.Through");
    }

    [Fact]
    public async Task A_reset_of_every_type_clears_the_vendor_state_when_the_connector_declares_so()
    {
        var store = SeededStore();
        var probe = new ProbeConnector(new StoringConnectorPublisher(store), clearsVendor: true);

        await probe.SyncDataAsync(SyncRequest.Reset(null, Now.UtcDateTime, null), new TestConfig(), CancellationToken.None);

        store.Vendor.Should().BeEmpty();
    }

    [Fact]
    public async Task A_requested_run_that_is_not_a_reset_clears_nothing()
    {
        var store = SeededStore();
        var probe = new ProbeConnector(new StoringConnectorPublisher(store), clearsVendor: true);

        await probe.SyncDataAsync(
            new SyncRequest { From = Earlier, To = Now.UtcDateTime, DataTypes = [SyncDataType.Boluses] },
            new TestConfig(), CancellationToken.None);

        store.Resume.Should().HaveCount(9);
        store.Vendor.Should().HaveCount(1);
        probe.StampSeenByTheRun.Should().Be(Earlier);
    }

    [Fact]
    public void A_request_body_cannot_ask_for_a_reset()
    {
        var request = JsonSerializer.Deserialize<SyncRequest>("""{"isReset":true,"IsReset":true}""")!;

        request.IsReset.Should().BeFalse();
    }

    [Fact]
    public async Task A_reset_of_a_connector_whose_sync_signs_in_clears_nothing_until_the_sync_says_it_signed_in()
    {
        var store = SeededStore();
        var before = Snapshot(store);
        var probe = new ProbeConnector(new StoringConnectorPublisher(store), clearsVendor: true) { SaysItSignedIn = false };

        await probe.SyncDataAsync(
            SyncRequest.Reset(Earlier, Now.UtcDateTime, [SyncDataType.Boluses]), new TestConfig(), CancellationToken.None);

        Snapshot(store).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task A_reset_of_a_connector_with_every_type_switched_off_clears_nothing()
    {
        var store = SeededStore();
        var before = Snapshot(store);
        var probe = new ProbeConnector(new StoringConnectorPublisher(store), clearsVendor: true);

        await probe.SyncDataAsync(
            SyncRequest.Reset(null, Now.UtcDateTime, null),
            new TestConfig { SyncGlucose = false, SyncBoluses = false },
            CancellationToken.None);

        Snapshot(store).Should().BeEquivalentTo(before);
    }

    public static TheoryData<string?, bool> RefusedTokenSignIns => new()
    {
        { null, true },
        { "token", false },
    };

    [Theory]
    [MemberData(nameof(RefusedTokenSignIns))]
    public async Task A_token_connector_reset_whose_sign_in_is_refused_leaves_the_state_exactly_as_it_was(
        string? token, bool accountFound)
    {
        var store = SeededStore();
        var before = Snapshot(store);
        var probe = new TokenProbe(new StoringConnectorPublisher(store), new ScriptedTokenProvider(token))
        {
            AccountFound = accountFound,
        };

        var result = await probe.SyncDataAsync(
            SyncRequest.Reset(null, Now.UtcDateTime, null), new TestConfig(), CancellationToken.None);

        result.Success.Should().BeFalse("the sign-in was refused");
        probe.SyncRan.Should().BeFalse();
        Snapshot(store).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task A_token_connector_reset_clears_its_stamps_and_declared_vendor_state_once_signed_in()
    {
        var store = SeededStore();
        var probe = new TokenProbe(new StoringConnectorPublisher(store), new ScriptedTokenProvider("token"));

        (await probe.SyncDataAsync(SyncRequest.Reset(null, Now.UtcDateTime, null), new TestConfig(), CancellationToken.None))
            .Success.Should().BeTrue();

        probe.StampSeenByTheSync.Should().BeNull("the reset cleared the stamp before the sync ran");
        store.Resume.Should().NotContainKeys("full-walk", "boluses-reconcile");
        store.Vendor.Should().BeEmpty();
    }

    [Fact]
    public async Task The_scheduled_catch_up_after_a_reset_sets_the_reach_mark_again()
    {
        var stale = Now.UtcDateTime.AddDays(-365);
        DateTime[] source = [Now.UtcDateTime.AddHours(-30), Now.UtcDateTime.AddHours(-2), Now.UtcDateTime.AddMinutes(-10)];
        var store = new InMemoryConnectorStateStore();
        store.Resume["DeviceStatus.Through"] = Now.UtcDateTime.AddHours(-1);
        var probe = new CatchUpProbe(new StoringConnectorPublisher(store), stale, source);

        await probe.SyncDataAsync(
            SyncRequest.Reset(Now.UtcDateTime.AddDays(-1), Now.UtcDateTime, null), new TestConfig(), CancellationToken.None);
        store.Resume.Should().NotContainKey("DeviceStatus.Through", "the reset cleared it and its run is ranged");

        await probe.SyncDataAsync(new TestConfig(), CancellationToken.None);
        await probe.SyncDataAsync(new TestConfig(), CancellationToken.None);

        probe.FromSeen[1].Should().Be(stale, "with no reach mark the first scheduled run starts at the stored watermark");
        store.Resume["DeviceStatus.Through"].Should().Be(source[^1]);
        probe.FromSeen[2].Should().Be(source[^1].AddMinutes(-5), "the next one resumes from what was published");
    }

    /// <summary>Hourly records, newest ten minutes ago, newest first.</summary>
    private static DateTime[] HourlySource(int count) =>
        Enumerable.Range(0, count).Select(hour => Now.UtcDateTime.AddMinutes(-10).AddHours(-hour)).ToArray();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_catch_up_cancelled_after_its_first_page_records_how_far_it_reached_with_what_it_owes(bool afterAReset)
    {
        var stale = Now.UtcDateTime.AddDays(-365);
        var source = HourlySource(25);
        var store = new InMemoryConnectorStateStore();
        var probe = new CatchUpProbe(new StoringConnectorPublisher(store), stale, source) { PageSize = 10 };
        if (afterAReset)
        {
            store.Resume["DeviceStatus.Through"] = Now.UtcDateTime.AddHours(-1);
            await probe.SyncDataAsync(
                SyncRequest.Reset(Now.UtcDateTime.AddDays(-1), Now.UtcDateTime, null), new TestConfig(), CancellationToken.None);
            store.Resume.Should().BeEmpty("the reset cleared the reach mark, and its run is ranged");
        }

        using var run = new CancellationTokenSource();
        probe.CancelsAt = (2, run);
        var cancelled = () => probe.SyncDataAsync(new TestConfig(), run.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        var afterCancel = new Dictionary<string, DateTime>(store.Resume);
        var cancelledFrom = probe.FromSeen[^1];

        probe.CancelsAt = null;
        probe.BackfillBudget = TimeSpan.Zero;
        await probe.SyncDataAsync(new TestConfig(), CancellationToken.None);

        using (new AssertionScope())
        {
            cancelledFrom.Should().Be(stale, "with no reach mark the catch-up starts at the stored watermark");
            afterCancel.Should().Equal(new Dictionary<string, DateTime>
            {
                ["DeviceStatus.Through"] = source[0],
                ["DeviceStatus.GapFrom"] = stale,
                ["DeviceStatus.GapTo"] = source[9],
            }, "the first page is published and everything below it down to the floor is owed");
            probe.FromSeen[^1].Should().Be(source[0].AddMinutes(-5), "the next run resumes from what was published");
            store.Resume.Should().Equal(new Dictionary<string, DateTime>
            {
                ["DeviceStatus.Through"] = source[0],
                ["DeviceStatus.GapFrom"] = stale,
                ["DeviceStatus.GapTo"] = source[18],
            }, "the owed range is worked through the budgeted crawl, one page on a spent budget");
        }

        probe.BackfillBudget = TimeSpan.FromMinutes(1);
        await probe.SyncDataAsync(new TestConfig(), CancellationToken.None);
        store.Resume.Should().Equal(new Dictionary<string, DateTime> { ["DeviceStatus.Through"] = source[0] });
        probe.Stored.Should().BeEquivalentTo(source, "every record is stored, once");

        await probe.SyncDataAsync(new TestConfig(), CancellationToken.None);
        probe.Stored.Should().HaveCount(source.Length, "a further run stores nothing again");
        probe.FromSeen[^1].Should().Be(source[0].AddMinutes(-5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_catch_up_cancelled_before_its_first_page_publishes_leaves_the_state_exactly_as_it_was(bool held)
    {
        var store = new InMemoryConnectorStateStore();
        if (held)
        {
            store.Resume["DeviceStatus.Through"] = Now.UtcDateTime.AddHours(-30);
            store.Resume["DeviceStatus.GapFrom"] = Now.UtcDateTime.AddDays(-40);
            store.Resume["DeviceStatus.GapTo"] = Now.UtcDateTime.AddDays(-35);
        }

        var before = Snapshot(store);
        var probe = new CatchUpProbe(new StoringConnectorPublisher(store), Now.UtcDateTime.AddDays(-365), HourlySource(25))
        {
            PageSize = 10,
        };
        using var run = new CancellationTokenSource();
        probe.CancelsAt = (1, run);

        var cancelled = () => probe.SyncDataAsync(new TestConfig(), run.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();

        Snapshot(store).Should().BeEquivalentTo(before);
        probe.Stored.Should().BeEmpty();
    }

    [Fact]
    public async Task An_open_ended_catch_up_from_above_what_the_crawls_covered_sets_no_reach_mark()
    {
        var store = new InMemoryConnectorStateStore();
        DateTime[] source = [Now.UtcDateTime.AddHours(-2), Now.UtcDateTime.AddMinutes(-10)];
        var probe = new CatchUpProbe(new StoringConnectorPublisher(store), Now.UtcDateTime.AddDays(-30), source)
        {
            ReadsTheResumePoint = false,
        };

        await probe.SyncDataAsync(
            new SyncRequest { From = Now.UtcDateTime.AddHours(-3) }, new TestConfig(), CancellationToken.None);

        store.Resume.Should().NotContainKey("DeviceStatus.Through",
            "the range between the stored watermark and the run's floor was never read");
    }

    private static (Dictionary<string, DateTime> Resume, Dictionary<string, string> Vendor) Snapshot(
        InMemoryConnectorStateStore store) =>
        (new Dictionary<string, DateTime>(store.Resume),
            store.Vendor.ToDictionary(entry => entry.Key, entry => entry.Value.ToJsonString()));

    /// <summary>Signs in through the token base, with a second step that can refuse the account.</summary>
    private sealed class TokenProbe(IConnectorPublisher publisher, ScriptedTokenProvider provider)
        : TokenConnectorService<TestConfig, ScriptedTokenProvider>(
            new HttpClient(),
            new ConnectorServerResolver<TestConfig>(null, null, null),
            NullLogger<TokenProbe>.Instance,
            new RecordingRetryDelayStrategy(),
            provider,
            publisher,
            new FakeTimeProvider(Now))
    {
        public bool AccountFound { get; init; } = true;
        public bool SyncRan { get; private set; }
        public DateTime? StampSeenByTheSync { get; private set; }

        protected override string ConnectorSource => "probe-connector";
        public override string ServiceName => "Probe";
        public override List<SyncDataType> SupportedDataTypes => [SyncDataType.Glucose, SyncDataType.Boluses];
        protected override bool ResetClearsVendorState => true;

        protected override IReadOnlyDictionary<string, IReadOnlyCollection<SyncDataType>> ResumeStamps { get; } =
            new Dictionary<string, IReadOnlyCollection<SyncDataType>>
            {
                ["full-walk"] = [SyncDataType.Glucose, SyncDataType.Boluses],
                ["boluses-reconcile"] = [SyncDataType.Boluses],
            };

        protected override Task<bool> CompleteSignInAsync(
            TestConfig config, TokenSession session, CancellationToken cancellationToken)
        {
            if (!AccountFound)
                TrackFailedAuthentication("The source did not say which account the sign-in belongs to.");
            return Task.FromResult(AccountFound);
        }

        protected override async Task<SyncResult> SyncWithTokenAsync(
            SyncRequest request, TestConfig config, TokenSession session, CancellationToken cancellationToken)
        {
            SyncRan = true;
            StampSeenByTheSync = await ResumeStampAsync("full-walk", cancellationToken);
            return new SyncResult { Success = true };
        }
    }

    /// <summary>Hands out <paramref name="token"/>, and records a refused sign-in when it is null.</summary>
    private sealed class ScriptedTokenProvider(string? token)
        : AuthTokenProviderBase<TestConfig>(
            new HttpClient(),
            new ConnectorTokenCache(),
            new ConnectorServerResolver<TestConfig>(null, null, null),
            ResolvedTenant.Accessor(),
            NullLogger<ScriptedTokenProvider>.Instance,
            new RecordingRetryDelayStrategy())
    {
        protected override string ConnectorName => "Scripted";

        protected override Task<(string? Token, DateTime ExpiresAt, IReadOnlyDictionary<string, string>? Metadata)> AcquireTokenAsync(
            TestConfig config, CancellationToken cancellationToken)
        {
            if (token is null)
                RecordSignInRefused();

            return Task.FromResult<(string?, DateTime, IReadOnlyDictionary<string, string>?)>(
                (token, DateTime.UtcNow.AddHours(1), null));
        }
    }

    /// <summary>
    ///     Crawls one family whose stored watermark stays at <paramref name="storedWatermark"/>
    ///     however much it publishes, as device statuses that write no snapshot do. A record
    ///     published twice is stored once, as the publish's deduplication does.
    /// </summary>
    private sealed class CatchUpProbe(IConnectorPublisher publisher, DateTime storedWatermark, DateTime[] source)
        : BaseConnectorService<TestConfig>(
            new HttpClient(),
            new ConnectorServerResolver<TestConfig>(null, null, null),
            NullLogger<CatchUpProbe>.Instance,
            new RecordingRetryDelayStrategy(),
            publisher,
            new FakeTimeProvider(Now))
    {
        public bool ReadsTheResumePoint { get; init; } = true;
        public int PageSize { get; init; } = 100;
        public List<DateTime?> FromSeen { get; } = [];
        public List<DateTime> Stored { get; } = [];

        /// <summary>Cancels the run as the page with this 1-based number of each crawl is fetched.</summary>
        public (int Page, CancellationTokenSource Run)? CancelsAt { get; set; }

        protected override string ConnectorSource => "probe-connector";
        public override string ServiceName => "Probe";
        public override List<SyncDataType> SupportedDataTypes => [SyncDataType.DeviceStatus];
        protected override bool SignsInBeforeSync => true;

        protected override async Task<SyncResult> PerformSyncInternalAsync(
            SyncRequest request, TestConfig config, CancellationToken cancellationToken)
        {
            var from = request.To is null && ReadsTheResumePoint
                ? ResumeFrom(request.From, await ResumePointAsync("DeviceStatus", storedWatermark, cancellationToken))
                : request.From;
            FromSeen.Add(from);

            await CrawlAndPublishAsync<DateTime>(
                "DeviceStatus", from, request.To,
                (pageFrom, pageTo) => PagesAsync(pageFrom, pageTo, cancellationToken),
                timeOf: at => at,
                publishAsync: page =>
                {
                    Stored.AddRange(page.Except(Stored));
                    return Task.FromResult(true);
                },
                PageSize,
                cancellationToken);
            return new SyncResult { Success = true };
        }

        private async IAsyncEnumerable<DateTime[]> PagesAsync(
            DateTime? from, DateTime? to, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            var pages = source
                .Where(at => (from is null || at >= from) && (to is null || at <= to))
                .OrderByDescending(at => at)
                .Chunk(PageSize);
            var number = 0;
            foreach (var page in pages)
            {
                if (CancelsAt is { } cancel && ++number == cancel.Page)
                    await cancel.Run.CancelAsync();
                cancellationToken.ThrowIfCancellationRequested();
                yield return page;
            }
        }
    }
}
