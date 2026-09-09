using System.Globalization;
using FluentAssertions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Connectors.Glooko.Configurations;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Services;

/// <summary>
/// The scheduled run is the connector's own to bound: the base hands it the glucose watermark, which
/// most Glooko accounts never have, and then the six-month floor — fourteen chunks and twenty-eight
/// requests, every five minutes, for ever. Every test here drives the scheduled entry point, the
/// shape <c>ConnectorBackgroundService</c> actually sends, and asserts the date windows Glooko is
/// asked for.
/// </summary>
public class GlookoConnectorServiceBackgroundWindowTests
{
    private static readonly string Completed = GlookoConstants.FullWalkCursorResource;
    private static readonly string Attempted = GlookoConstants.FullWalkAttemptCursorResource;

    /// <summary>
    /// The harness serves the state-span, temp-basal, device-event and profile feeds; the rest of
    /// the connector's types are switched off so the scheduled run reports on what it fetched.
    /// </summary>
    private static GlookoConnectorConfiguration ScheduledConfig(bool useV3Api = true)
    {
        var config = GlookoSyncHarness.Config(useV3Api);
        config.SyncGlucose = false;
        config.SyncManualBG = false;
        config.SyncBoluses = false;
        config.SyncBasalInjections = false;
        config.SyncCarbIntake = false;
        config.SyncBolusCalculations = false;
        config.SyncNotes = false;
        config.SyncFood = false;
        config.SyncActivity = false;
        return config;
    }

    private static ConnectorSyncCursor StampAgo(TimeSpan ago) =>
        new(FullWalkSchedule.Stamp(DateTimeOffset.UtcNow - ago), null);

    private static FakeCursorStore StoreWith(params (string Resource, ConnectorSyncCursor Cursor)[] stamps)
    {
        var store = new FakeCursorStore();
        foreach (var (resource, cursor) in stamps)
            store.Saved[resource] = cursor;
        return store;
    }

    private static Task<SyncResult> Scheduled(
        RecordingGlookoConnectorService service, GlookoConnectorConfiguration config) =>
        service.SyncDataAsync(config, CancellationToken.None);

    [Fact]
    public async Task ScheduledRun_OnTheFirstRun_WalksTheFullHistoryAndRecordsIt()
    {
        var handler = new GlookoEndpointHandler();
        var store = new FakeCursorStore();

        var result = await Scheduled(GlookoSyncHarness.Service(handler, cursorStore: store), ScheduledConfig());

        result.Success.Should().BeTrue();
        handler.WindowCount.Should().Be(FullWalkChunks());
        Parse(handler.Windows[0].Start).Should().BeCloseTo(
            DateTime.UtcNow.AddMonths(-GlookoConstants.FullWalkMonths).AddDays(-1), TimeSpan.FromHours(1));

        store.Saved.Keys.Should().BeEquivalentTo([Completed, Attempted]);
        FullWalkSchedule.IsDue(store.Saved[Completed].LastUpdatedAt, GlookoConstants.FullWalkInterval, DateTimeOffset.UtcNow)
            .Should().BeFalse("the walk that just completed is what the next run stands on");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ScheduledRun_InsideTheInterval_ReachesBackOnlyTheLookback(bool useV3Api)
    {
        var handler = new GlookoEndpointHandler();
        var seeded = StampAgo(TimeSpan.FromMinutes(30));
        var store = StoreWith((Completed, seeded));
        var config = ScheduledConfig(useV3Api);
        var service = GlookoSyncHarness.Service(handler, cursorStore: store);

        var result = await Scheduled(service, config);

        result.Success.Should().BeTrue();
        // The lookback plus the day of padding on each side is one chunk.
        handler.WindowCount.Should().Be(1);
        Parse(handler.Windows[0].Start).Should().BeCloseTo(
            DateTime.UtcNow.AddDays(-config.LookbackDays - 1), TimeSpan.FromHours(1));
        store.Saved.Should().HaveCount(1);
        store.Saved[Completed].Should().BeSameAs(seeded, "an incremental run leaves the stamps alone");
        service.Published.Should().Contain(PublishKind.StateSpans);
    }

    [Fact]
    public async Task ScheduledRun_InsideTheInterval_HonoursAWiderLookback()
    {
        var handler = new GlookoEndpointHandler();
        var config = ScheduledConfig();
        config.LookbackDays = 30;

        await Scheduled(
            GlookoSyncHarness.Service(handler, cursorStore: StoreWith((Completed, StampAgo(TimeSpan.FromMinutes(30))))),
            config);

        handler.WindowCount.Should().Be(3, "30 days plus two of padding is three fortnightly chunks");
        Parse(handler.Windows[0].Start).Should().BeCloseTo(DateTime.UtcNow.AddDays(-31), TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task ScheduledRun_OnceTheIntervalHasElapsed_WalksAgain()
    {
        var handler = new GlookoEndpointHandler();
        var overdue = GlookoConstants.FullWalkInterval + TimeSpan.FromMinutes(1);
        var store = StoreWith((Completed, StampAgo(overdue)), (Attempted, StampAgo(overdue)));

        await Scheduled(GlookoSyncHarness.Service(handler, cursorStore: store), ScheduledConfig());

        handler.WindowCount.Should().Be(FullWalkChunks());
        Parse(store.Saved[Completed].LastUpdatedAt!).Should().BeAfter(
            DateTime.UtcNow - overdue, "the new walk replaces the stale stamp");
    }

    /// <summary>
    /// A chunk that fails stops the pass, so the walk has not covered its history and is not counted
    /// as completed — but it was attempted, and the next runs stay on the lookback until the retry
    /// interval has passed rather than walking every cycle.
    /// </summary>
    [Fact]
    public async Task ScheduledRun_WhenTheWalkFails_RecordsTheAttemptAndBacksOff()
    {
        var failing = new GlookoEndpointHandler(failingPaths: [GlookoConstants.V3GraphDataPath]);
        var requestsWhenAttemptLanded = -1;
        var store = new FakeCursorStore
        {
            OnSet = resource =>
            {
                if (resource == Attempted)
                    requestsWhenAttemptLanded = failing.RequestsFor(GlookoConstants.V3GraphDataPath);
            },
        };

        var result = await Scheduled(GlookoSyncHarness.Service(failing, cursorStore: store), ScheduledConfig());

        result.Success.Should().BeFalse();
        store.Saved.Keys.Should().BeEquivalentTo([Attempted]);
        requestsWhenAttemptLanded.Should().Be(0,
            "the attempt is stamped before the walk starts, so a walk that never finishes still counts");

        var next = new GlookoEndpointHandler();
        await Scheduled(GlookoSyncHarness.Service(next, cursorStore: store), ScheduledConfig());

        next.WindowCount.Should().Be(1, "the retry interval has not elapsed");
        store.Saved.Should().NotContainKey(Completed);
    }

    [Fact]
    public async Task ScheduledRun_OnceTheRetryIntervalHasElapsed_WalksAgainAfterAFailure()
    {
        var handler = new GlookoEndpointHandler();
        var store = StoreWith((Attempted, StampAgo(GlookoConstants.FullWalkRetryInterval + TimeSpan.FromMinutes(1))));

        await Scheduled(GlookoSyncHarness.Service(handler, cursorStore: store), ScheduledConfig());

        handler.WindowCount.Should().Be(FullWalkChunks());
        store.Saved.Should().ContainKey(Completed);
    }

    /// <summary>
    /// A caller that names its own lower bound has already chosen; the schedule neither widens nor
    /// records.
    /// </summary>
    [Fact]
    public async Task ScheduledRun_WithACallerSince_HonoursItAndLeavesTheScheduleAlone()
    {
        var handler = new GlookoEndpointHandler();
        var store = new FakeCursorStore();

        await GlookoSyncHarness.Service(handler, cursorStore: store).SyncDataAsync(
            ScheduledConfig(), CancellationToken.None, since: DateTime.UtcNow.AddDays(-3));

        handler.WindowCount.Should().Be(1);
        Parse(handler.Windows[0].Start).Should().BeCloseTo(DateTime.UtcNow.AddDays(-4), TimeSpan.FromHours(1));
        store.Saved.Should().BeEmpty();
    }

    /// <summary>
    /// The tenant's own sync button sends a request with neither bound. It is not a scheduled run,
    /// so it re-pulls the floor as it always did and never touches the schedule — clicking it does
    /// not spend the day's walk.
    /// </summary>
    [Fact]
    public async Task RequestedRun_WithNoBounds_ReadsTheFloorAndLeavesTheScheduleAlone()
    {
        var handler = new GlookoEndpointHandler();
        var store = new FakeCursorStore();

        var result = await GlookoSyncHarness.Service(handler, cursorStore: store).SyncDataAsync(
            new SyncRequest(), ScheduledConfig(), CancellationToken.None);

        result.Success.Should().BeTrue();
        handler.WindowCount.Should().Be(FullWalkChunks());
        store.Saved.Should().BeEmpty();
    }

    /// <summary>
    /// Nothing can remember a walk without a store, so a detached service (dry-run tooling) takes the
    /// base window, which for an account without Glooko glucose is the floor.
    /// </summary>
    [Fact]
    public async Task ScheduledRun_WithoutACursorStore_TakesTheBaseWindow()
    {
        var handler = new GlookoEndpointHandler();

        await Scheduled(GlookoSyncHarness.Service(handler), ScheduledConfig());

        handler.WindowCount.Should().Be(FullWalkChunks());
    }

    /// <summary>
    /// The chunk count the floor spans, computed the way the sync computes it so a month's length
    /// never decides the test.
    /// </summary>
    private static int FullWalkChunks()
    {
        var now = DateTime.UtcNow;
        return DateChunker.Chunk(
                now.AddMonths(-GlookoConstants.FullWalkMonths).AddDays(-1), now.AddDays(1),
                GlookoConstants.SyncChunkSize)
            .Count();
    }

    private static DateTime Parse(string timestamp) =>
        DateTime.Parse(timestamp, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
}
