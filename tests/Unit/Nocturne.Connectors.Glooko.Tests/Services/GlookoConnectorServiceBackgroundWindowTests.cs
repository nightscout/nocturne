using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Moq;
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
    private static readonly DateTimeOffset Instant = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly FakeTimeProvider Clock = new(Instant);
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

    private static RecordingGlookoConnectorService Service(
        GlookoEndpointHandler handler,
        IConnectorSyncCursorStore? cursorStore = null,
        IConnectorPublisher? publisher = null) =>
        GlookoSyncHarness.Service(handler, publisher: publisher, cursorStore: cursorStore, clock: Clock);

    private static ConnectorSyncCursor StampAgo(TimeSpan ago) =>
        new(FullWalkSchedule.Stamp(Instant - ago), null);

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

        var result = await Scheduled(Service(handler, cursorStore: store), ScheduledConfig());

        result.Success.Should().BeTrue();
        handler.WindowCount.Should().Be(FullWalkChunks());
        Parse(handler.Windows[0].Start).Should().Be(
            Instant.UtcDateTime.AddMonths(-GlookoConstants.FullWalkMonths).AddDays(-1));

        store.Saved.Keys.Should().BeEquivalentTo([Completed, Attempted]);
        store.Saved[Completed].LastUpdatedAt.Should().Be(FullWalkSchedule.Stamp(Instant));
        store.Saved[Attempted].LastUpdatedAt.Should().Be(FullWalkSchedule.Stamp(Instant));
        FullWalkSchedule.IsDue(store.Saved[Completed].LastUpdatedAt, GlookoConstants.FullWalkInterval, Instant)
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
        var service = Service(handler, cursorStore: store);

        var result = await Scheduled(service, config);

        result.Success.Should().BeTrue();
        // The lookback plus the day of padding on each side is one chunk.
        handler.WindowCount.Should().Be(1);
        Parse(handler.Windows[0].Start).Should().Be(Instant.UtcDateTime.AddDays(-config.LookbackDays - 1));
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
            Service(handler, cursorStore: StoreWith((Completed, StampAgo(TimeSpan.FromMinutes(30))))),
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

        await Scheduled(Service(handler, cursorStore: store), ScheduledConfig());

        handler.WindowCount.Should().Be(FullWalkChunks());
        store.Saved[Completed].LastUpdatedAt.Should().Be(
            FullWalkSchedule.Stamp(Instant), "the new walk replaces the stale stamp");
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task ScheduledRun_AtTheFullWalkIntervalBoundary_WalksOnlyOnceItHasElapsed(
        long ticksShort, bool walks)
    {
        var handler = new GlookoEndpointHandler();
        var store = StoreWith((Completed, StampAgo(GlookoConstants.FullWalkInterval - TimeSpan.FromTicks(ticksShort))));

        await Scheduled(Service(handler, cursorStore: store), ScheduledConfig());

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

        await Scheduled(Service(handler, cursorStore: store), ScheduledConfig());

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
        var store = new FakeCursorStore
        {
            OnSet = resource =>
            {
                if (resource == Attempted)
                    requestsWhenAttemptLanded = failing.RequestsFor(GlookoConstants.V3GraphDataPath);
            },
        };

        var result = await Scheduled(Service(failing, cursorStore: store), ScheduledConfig());

        result.Success.Should().BeFalse();
        store.Saved.Keys.Should().BeEquivalentTo([Attempted]);
        requestsWhenAttemptLanded.Should().Be(0,
            "the attempt is stamped before the walk starts, so a walk that never finishes still counts");

        var next = new GlookoEndpointHandler();
        await Scheduled(Service(next, cursorStore: store), ScheduledConfig());

        next.WindowCount.Should().Be(1, "the retry interval has not elapsed");
        store.Saved.Should().NotContainKey(Completed);
    }

    [Fact]
    public async Task ScheduledRun_OnceTheRetryIntervalHasElapsed_WalksAgainAfterAFailure()
    {
        var handler = new GlookoEndpointHandler();
        var store = StoreWith((Attempted, StampAgo(GlookoConstants.FullWalkRetryInterval + TimeSpan.FromMinutes(1))));

        await Scheduled(Service(handler, cursorStore: store), ScheduledConfig());

        handler.WindowCount.Should().Be(FullWalkChunks());
        store.Saved.Should().ContainKey(Completed);
    }

    [Fact]
    public async Task ScheduledRun_WhenReadingTheScheduleTimesOut_Walks()
    {
        var handler = new GlookoEndpointHandler();
        var store = new FakeCursorStore
        {
            BeforeGet = _ => throw new TaskCanceledException("The store timed out."),
        };

        var result = await Scheduled(Service(handler, cursorStore: store), ScheduledConfig());

        result.Success.Should().BeTrue();
        handler.WindowCount.Should().Be(FullWalkChunks());
    }

    [Fact]
    public async Task ScheduledRun_WhenTheCallerCancelsDuringTheScheduleRead_Propagates()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new FakeCursorStore
        {
            BeforeGet = token =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            },
        };

        var run = () => Service(new GlookoEndpointHandler(), cursorStore: store)
            .SyncDataAsync(ScheduledConfig(), cancellation.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ScheduledRun_WhenRecordingTheWalkTimesOut_StillSucceeds()
    {
        var handler = new GlookoEndpointHandler();
        var store = new FakeCursorStore
        {
            BeforeSet = _ => throw new TaskCanceledException("The store timed out."),
        };

        var result = await Scheduled(Service(handler, cursorStore: store), ScheduledConfig());

        result.Success.Should().BeTrue();
        handler.WindowCount.Should().Be(FullWalkChunks());
        store.Saved.Should().BeEmpty();
    }

    [Fact]
    public async Task ScheduledRun_WhenTheCallerCancelsDuringTheWalkRecord_Propagates()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new FakeCursorStore
        {
            BeforeSet = token =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            },
        };

        var run = () => Service(new GlookoEndpointHandler(), cursorStore: store)
            .SyncDataAsync(ScheduledConfig(), cancellation.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
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

        await Service(handler, cursorStore: store).SyncDataAsync(
            ScheduledConfig(), CancellationToken.None, since: Instant.UtcDateTime.AddDays(-3));

        handler.WindowCount.Should().Be(1);
        Parse(handler.Windows[0].Start).Should().Be(Instant.UtcDateTime.AddDays(-4));
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

        var result = await Service(handler, cursorStore: store).SyncDataAsync(
            new SyncRequest(), ScheduledConfig(), CancellationToken.None);

        result.Success.Should().BeTrue();
        handler.WindowCount.Should().Be(FullWalkChunks());
        store.Saved.Should().BeEmpty();
    }

    /// <summary>
    /// Nothing can remember a walk without a store, so a detached service (dry-run tooling) resumes
    /// from what is stored, which for an account with nothing stored is the floor.
    /// </summary>
    [Fact]
    public async Task ScheduledRun_WithoutACursorStore_TakesTheBaseWindow()
    {
        var handler = new GlookoEndpointHandler();

        await Scheduled(Service(handler), ScheduledConfig());

        handler.WindowCount.Should().Be(FullWalkChunks());
    }

    /// <summary>
    /// Every family is fetched inside one window, so a run resuming from what is stored starts at the
    /// family furthest behind. Glucose from a chunk that published ahead of a failed one sits days
    /// past the treatments that chunk never published; the glucose watermark alone would leave those
    /// treatments below every later run's bound.
    /// </summary>
    [Fact]
    public async Task ScheduledRun_WithoutACursorStore_ResumesFromTheFamilyFurthestBehind()
    {
        var now = Instant.UtcDateTime;
        var publisher = new Mock<IConnectorPublisher> { DefaultValue = DefaultValue.Mock };
        publisher.SetupGet(p => p.IsAvailable).Returns(true);
        publisher.Setup(p => p.Glucose.GetLatestEntryTimestampAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(now.AddDays(-1));
        publisher.Setup(p => p.Treatments.GetLatestTreatmentTimestampAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(now.AddDays(-20));
        var handler = new GlookoEndpointHandler();

        await Scheduled(Service(handler, publisher: publisher.Object), ScheduledConfig());

        Parse(handler.Windows[0].Start).Should().Be(now.AddDays(-21).AddMinutes(-5),
            "the treatment watermark, the base's five minute overlap and a day of padding below it, not the glucose watermark");
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
