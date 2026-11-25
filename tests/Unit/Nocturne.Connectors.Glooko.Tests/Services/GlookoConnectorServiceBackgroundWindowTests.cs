using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Tests.Shared.Connectors;
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
    private static readonly DateTimeOffset Instant = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly FakeTimeProvider Clock = new(Instant);
    private static readonly string Completed = GlookoConstants.FullWalkStamp;
    private static readonly string Attempted = GlookoConstants.FullWalkAttemptStamp;
    private static readonly string LookbackRead = GlookoConstants.LookbackReadStamp;

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

    private static RecordingGlookoConnectorService Service(
        GlookoEndpointHandler handler,
        IConnectorStateStore? state = null,
        IConnectorPublisher? publisher = null) =>
        GlookoSyncHarness.Service(handler, publisher: publisher, state: state, clock: Clock);

    private static DateTime StampAgo(TimeSpan ago) => (Instant - ago).UtcDateTime;

    private static InMemoryConnectorStateStore StoreWith(params (string Key, DateTime At)[] stamps)
    {
        var store = new InMemoryConnectorStateStore();
        foreach (var (key, at) in stamps)
            store.Resume[key] = at;
        return store;
    }

    private static Task<SyncResult> Scheduled(
        RecordingGlookoConnectorService service, GlookoConnectorConfiguration config) =>
        service.SyncDataAsync(config, CancellationToken.None);

    [Fact]
    public async Task ScheduledRun_OnTheFirstRun_WalksTheFullHistoryAndRecordsIt()
    {
        var handler = new GlookoEndpointHandler();
        var store = new InMemoryConnectorStateStore();

        var result = await Scheduled(Service(handler, state: store), ScheduledConfig());

        result.Success.Should().BeTrue();
        handler.WindowCount.Should().Be(FullWalkChunks());
        Parse(handler.Windows[0].Start).Should().Be(
            Instant.UtcDateTime.AddMonths(-GlookoConstants.FullWalkMonths).AddDays(-1));

        store.Resume.Keys.Should().BeEquivalentTo([Completed, Attempted, LookbackRead]);
        store.Resume[LookbackRead].Should().Be(
            Instant.UtcDateTime, "a completed walk covers the lookback too");
        store.Resume[Completed].Should().Be(Instant.UtcDateTime);
        store.Resume[Attempted].Should().Be(Instant.UtcDateTime);
        FullWalkSchedule.IsDue(store.Resume[Completed], GlookoConstants.FullWalkInterval, Instant)
            .Should().BeFalse("the walk that just completed is what the next run stands on");
    }

    [Fact]
    public async Task ScheduledSyncTwice_SecondRunReachesBackOnlyTheShortWindow()
    {
        var store = new InMemoryConnectorStateStore();
        var first = new GlookoEndpointHandler();
        var second = new GlookoEndpointHandler();

        (await Scheduled(Service(first, state: store), ScheduledConfig())).Success.Should().BeTrue();
        (await Scheduled(Service(second, state: store), ScheduledConfig())).Success.Should().BeTrue();

        first.WindowCount.Should().Be(FullWalkChunks());
        second.WindowCount.Should().Be(1);
        Parse(second.Windows[0].Start).Should().Be(
            Instant.UtcDateTime - GlookoConstants.ShortWindow - TimeSpan.FromDays(1),
            "the first run's walk and lookback cover everything older");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ScheduledRun_InsideTheLookbackInterval_ReachesBackOnlyTheShortWindow(bool useV3Api)
    {
        var handler = new GlookoEndpointHandler();
        var walked = StampAgo(TimeSpan.FromMinutes(30));
        var read = StampAgo(TimeSpan.FromMinutes(5));
        var store = StoreWith((Completed, walked), (LookbackRead, read));
        var service = Service(handler, state: store);

        var result = await Scheduled(service, ScheduledConfig(useV3Api));

        result.Success.Should().BeTrue();
        handler.WindowCount.Should().Be(1);
        Parse(handler.Windows[0].Start).Should().Be(
            Instant.UtcDateTime - GlookoConstants.ShortWindow - TimeSpan.FromDays(1));
        store.Resume.Should().HaveCount(2);
        store.Resume[Completed].Should().Be(walked, "a short run leaves the stamps alone");
        store.Resume[LookbackRead].Should().Be(read, "a short run leaves the stamps alone");
        service.Published.Should().Contain(PublishKind.StateSpans);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ScheduledRun_OnceTheLookbackIntervalHasElapsed_ReachesBackTheLookbackAndRecordsIt(bool useV3Api)
    {
        var handler = new GlookoEndpointHandler();
        var walked = StampAgo(TimeSpan.FromMinutes(30));
        var store = StoreWith(
            (Completed, walked),
            (LookbackRead, StampAgo(GlookoConstants.LookbackReadInterval + TimeSpan.FromMinutes(1))));
        var config = ScheduledConfig(useV3Api);

        var result = await Scheduled(Service(handler, state: store), config);

        result.Success.Should().BeTrue();
        // The lookback plus the day of padding on each side is one chunk.
        handler.WindowCount.Should().Be(1);
        Parse(handler.Windows[0].Start).Should().Be(Instant.UtcDateTime.AddDays(-config.LookbackDays - 1));
        store.Resume[LookbackRead].Should().Be(Instant.UtcDateTime);
        store.Resume[Completed].Should().Be(walked, "a lookback read is not a walk");
        store.Resume.Should().NotContainKey(Attempted);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task ScheduledRun_AtTheLookbackIntervalBoundary_ReadsTheLookbackOnlyOnceItHasElapsed(
        long ticksShort, bool lookback)
    {
        var handler = new GlookoEndpointHandler();
        var config = ScheduledConfig();
        var store = StoreWith(
            (Completed, StampAgo(TimeSpan.FromMinutes(30))),
            (LookbackRead, StampAgo(GlookoConstants.LookbackReadInterval - TimeSpan.FromTicks(ticksShort))));

        await Scheduled(Service(handler, state: store), config);

        var reach = lookback ? TimeSpan.FromDays(config.LookbackDays) : GlookoConstants.ShortWindow;
        Parse(handler.Windows[0].Start).Should().Be(Instant.UtcDateTime - reach - TimeSpan.FromDays(1));
    }

    /// <summary>
    /// A lookback read whose chunk fails has not covered its window, so it is not recorded and the
    /// next cycle reads the lookback again rather than waiting out the interval.
    /// </summary>
    [Fact]
    public async Task ScheduledRun_WhenTheLookbackReadFails_ReadsTheLookbackAgainNextCycle()
    {
        var config = ScheduledConfig();
        var stale = StampAgo(GlookoConstants.LookbackReadInterval + TimeSpan.FromMinutes(1));
        var store = StoreWith((Completed, StampAgo(TimeSpan.FromMinutes(30))), (LookbackRead, stale));

        var failed = await Scheduled(
            Service(new GlookoEndpointHandler(failingPaths: [GlookoConstants.V3GraphDataPath]), state: store),
            config);

        failed.Success.Should().BeFalse();
        store.Resume[LookbackRead].Should().Be(stale);

        var next = new GlookoEndpointHandler();
        await Scheduled(Service(next, state: store), config);

        Parse(next.Windows[0].Start).Should().Be(Instant.UtcDateTime.AddDays(-config.LookbackDays - 1));
        store.Resume[LookbackRead].Should().Be(Instant.UtcDateTime);
    }

    [Fact]
    public async Task ScheduledRun_WhenTheLookbackReadIsCancelled_DoesNotRecordIt()
    {
        using var cancellation = new CancellationTokenSource();
        var store = StoreWith((Completed, StampAgo(TimeSpan.FromMinutes(30))));
        var service = Service(new GlookoEndpointHandler(), state: store);
        await cancellation.CancelAsync();

        var run = () => service.SyncDataAsync(ScheduledConfig(), cancellation.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
        store.Resume.Should().NotContainKey(LookbackRead);
    }

    [Fact]
    public async Task ScheduledRun_OnTheLookbackTier_HonoursAWiderLookback()
    {
        var handler = new GlookoEndpointHandler();
        var config = ScheduledConfig();
        config.LookbackDays = 30;

        await Scheduled(
            Service(handler, state: StoreWith((Completed, StampAgo(TimeSpan.FromMinutes(30))))),
            config);

        handler.WindowCount.Should().Be(3, "30 days plus two of padding is three fortnightly chunks");
        Parse(handler.Windows[0].Start).Should().Be(Instant.UtcDateTime.AddDays(-31));
    }

    [Fact]
    public async Task ScheduledRun_OnceTheIntervalHasElapsed_WalksAgain()
    {
        var handler = new GlookoEndpointHandler();
        var overdue = GlookoConstants.FullWalkInterval + TimeSpan.FromMinutes(1);
        var store = StoreWith((Completed, StampAgo(overdue)), (Attempted, StampAgo(overdue)));

        await Scheduled(Service(handler, state: store), ScheduledConfig());

        handler.WindowCount.Should().Be(FullWalkChunks());
        store.Resume[Completed].Should().Be(
            Instant.UtcDateTime, "the new walk replaces the stale stamp");
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task ScheduledRun_AtTheFullWalkIntervalBoundary_WalksOnlyOnceItHasElapsed(
        long ticksShort, bool walks)
    {
        var handler = new GlookoEndpointHandler();
        var store = StoreWith((Completed, StampAgo(GlookoConstants.FullWalkInterval - TimeSpan.FromTicks(ticksShort))));

        await Scheduled(Service(handler, state: store), ScheduledConfig());

        handler.WindowCount.Should().Be(walks ? FullWalkChunks() : 1);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task ScheduledRun_AtTheRetryIntervalBoundary_WalksOnlyOnceItHasElapsed(
        long ticksShort, bool walks)
    {
        var handler = new GlookoEndpointHandler();
        var overdue = GlookoConstants.FullWalkInterval + TimeSpan.FromMinutes(1);
        var store = StoreWith(
            (Completed, StampAgo(overdue)),
            (Attempted, StampAgo(GlookoConstants.FullWalkRetryInterval - TimeSpan.FromTicks(ticksShort))));

        await Scheduled(Service(handler, state: store), ScheduledConfig());

        handler.WindowCount.Should().Be(walks ? FullWalkChunks() : 1);
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
        var store = new InMemoryConnectorStateStore
        {
            OnWrite = resource =>
            {
                if (resource == Attempted)
                    requestsWhenAttemptLanded = failing.RequestsFor(GlookoConstants.V3GraphDataPath);
            },
        };

        var result = await Scheduled(Service(failing, state: store), ScheduledConfig());

        result.Success.Should().BeFalse();
        store.Resume.Keys.Should().BeEquivalentTo([Attempted]);
        requestsWhenAttemptLanded.Should().Be(0,
            "the attempt is stamped before the walk starts, so a walk that never finishes still counts");

        var next = new GlookoEndpointHandler();
        await Scheduled(Service(next, state: store), ScheduledConfig());

        next.WindowCount.Should().Be(1, "the retry interval has not elapsed");
        store.Resume.Should().NotContainKey(Completed);
    }

    [Fact]
    public async Task ScheduledRun_OnceTheRetryIntervalHasElapsed_WalksAgainAfterAFailure()
    {
        var handler = new GlookoEndpointHandler();
        var store = StoreWith((Attempted, StampAgo(GlookoConstants.FullWalkRetryInterval + TimeSpan.FromMinutes(1))));

        await Scheduled(Service(handler, state: store), ScheduledConfig());

        handler.WindowCount.Should().Be(FullWalkChunks());
        store.Resume.Should().ContainKey(Completed);
    }

    [Fact]
    public async Task ScheduledRun_WhenReadingTheScheduleTimesOut_Walks()
    {
        var handler = new GlookoEndpointHandler();
        var store = new InMemoryConnectorStateStore
        {
            BeforeRead = _ => throw new TaskCanceledException("The store timed out."),
        };

        var result = await Scheduled(Service(handler, state: store), ScheduledConfig());

        result.Success.Should().BeTrue();
        handler.WindowCount.Should().Be(FullWalkChunks());
    }

    [Fact]
    public async Task ScheduledRun_WhenTheStoreIsDownForTheScheduleRead_Walks()
    {
        var handler = new GlookoEndpointHandler();
        var store = new InMemoryConnectorStateStore
        {
            BeforeRead = _ => throw new ConnectorStateUnavailableException("connection refused", new InvalidOperationException()),
        };

        var result = await Scheduled(Service(handler, state: store), ScheduledConfig());

        result.Success.Should().BeTrue();
        handler.WindowCount.Should().Be(FullWalkChunks());
    }

    [Fact]
    public async Task ScheduledRun_WhenTheStoreIsDownForTheWalkRecord_StillSucceeds()
    {
        var handler = new GlookoEndpointHandler();
        var store = new InMemoryConnectorStateStore
        {
            BeforeWrite = _ => throw new ConnectorStateUnavailableException("connection refused", new InvalidOperationException()),
        };

        var result = await Scheduled(Service(handler, state: store), ScheduledConfig());

        result.Success.Should().BeTrue();
        handler.WindowCount.Should().Be(FullWalkChunks());
        store.Resume.Should().BeEmpty();
    }

    [Fact]
    public async Task ScheduledRun_WhenReadingTheScheduleHitsADefect_FailsInsteadOfWalking()
    {
        var handler = new GlookoEndpointHandler();
        var store = new InMemoryConnectorStateStore { BeforeRead = _ => throw new InvalidCastException("defect") };

        var result = await Scheduled(Service(handler, state: store), ScheduledConfig());

        result.Success.Should().BeFalse();
        handler.WindowCount.Should().Be(0);
    }

    [Fact]
    public async Task ScheduledRun_WhenRecordingTheWalkHitsADefect_FailsInsteadOfCarryingOn()
    {
        var handler = new GlookoEndpointHandler();
        var store = new InMemoryConnectorStateStore { BeforeWrite = _ => throw new InvalidCastException("defect") };

        var result = await Scheduled(Service(handler, state: store), ScheduledConfig());

        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task ScheduledRun_WhenTheCallerCancelsDuringTheScheduleRead_Propagates()
    {
        using var cancellation = new CancellationTokenSource();
        var storeSawTheCancellation = false;
        var handler = new GlookoEndpointHandler();
        var store = new InMemoryConnectorStateStore
        {
            BeforeRead = token =>
            {
                cancellation.Cancel();
                storeSawTheCancellation = token.IsCancellationRequested;
                token.ThrowIfCancellationRequested();
            },
        };

        var run = () => Service(handler, state: store)
            .SyncDataAsync(ScheduledConfig(), cancellation.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
        storeSawTheCancellation.Should().BeTrue("the store is handed the run's token");
        handler.WindowCount.Should().Be(0);
    }

    [Fact]
    public async Task ScheduledRun_WhenRecordingTheWalkTimesOut_StillSucceeds()
    {
        var handler = new GlookoEndpointHandler();
        var store = new InMemoryConnectorStateStore
        {
            BeforeWrite = _ => throw new TaskCanceledException("The store timed out."),
        };

        var result = await Scheduled(Service(handler, state: store), ScheduledConfig());

        result.Success.Should().BeTrue();
        handler.WindowCount.Should().Be(FullWalkChunks());
        store.Resume.Should().BeEmpty();
    }

    [Fact]
    public async Task ScheduledRun_WhenTheCallerCancelsDuringTheWalkRecord_Propagates()
    {
        using var cancellation = new CancellationTokenSource();
        var storeSawTheCancellation = false;
        var handler = new GlookoEndpointHandler();
        var store = new InMemoryConnectorStateStore
        {
            BeforeWrite = token =>
            {
                cancellation.Cancel();
                storeSawTheCancellation = token.IsCancellationRequested;
                token.ThrowIfCancellationRequested();
            },
        };

        var run = () => Service(handler, state: store)
            .SyncDataAsync(ScheduledConfig(), cancellation.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
        storeSawTheCancellation.Should().BeTrue("the store is handed the run's token");
        handler.WindowCount.Should().Be(0);
    }

    /// <summary>
    /// A caller that names its own lower bound has already chosen; the schedule neither widens nor
    /// records.
    /// </summary>
    [Fact]
    public async Task ScheduledRun_WithACallerSince_HonoursItAndLeavesTheScheduleAlone()
    {
        var handler = new GlookoEndpointHandler();
        var store = new InMemoryConnectorStateStore();

        await Service(handler, state: store).SyncDataAsync(
            ScheduledConfig(), CancellationToken.None, since: Instant.UtcDateTime.AddDays(-3));

        handler.WindowCount.Should().Be(1);
        Parse(handler.Windows[0].Start).Should().Be(Instant.UtcDateTime.AddDays(-4));
        store.Resume.Should().BeEmpty();
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
        var store = new InMemoryConnectorStateStore();

        var result = await Service(handler, state: store).SyncDataAsync(
            new SyncRequest(), ScheduledConfig(), CancellationToken.None);

        result.Success.Should().BeTrue();
        handler.WindowCount.Should().Be(FullWalkChunks());
        store.Resume.Should().BeEmpty();
    }

    /// <summary>
    /// The chunk count the floor spans, computed the way the sync computes it so a month's length
    /// never decides the test.
    /// </summary>
    private static int FullWalkChunks()
    {
        var now = Instant.UtcDateTime;
        return DateChunker.Chunk(
                now.AddMonths(-GlookoConstants.FullWalkMonths).AddDays(-1), now.AddDays(1),
                GlookoConstants.SyncChunkSize)
            .Count();
    }

    private static DateTime Parse(string timestamp) =>
        DateTime.Parse(timestamp, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
}
