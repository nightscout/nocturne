using System.Globalization;
using FluentAssertions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Connectors.Glooko.Configurations;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Services;

/// <summary>
/// A background run has no upper bound and, for most Glooko accounts, no glucose watermark to hand
/// it a lower one, so the connector used to answer every such run with the whole history floor:
/// fourteen chunks and twenty-eight requests, every five minutes, for ever. The window now reaches
/// back a fixed lookback, and the whole floor only once per <see cref="GlookoConstants.FullWalkInterval"/>.
/// </summary>
public class GlookoConnectorServiceBackgroundWindowTests
{
    private static readonly SyncDataType[] Types =
    [
        SyncDataType.StateSpans, SyncDataType.TempBasals, SyncDataType.DeviceEvents, SyncDataType.Profiles,
    ];

    /// <summary>
    /// A stamp that keeps the schedule quiet: half an hour into the interval.
    /// </summary>
    private static ConnectorSyncCursor RecentWalk() =>
        new(FullWalkSchedule.Stamp(DateTimeOffset.UtcNow.AddMinutes(-30)), null);

    [Fact]
    public async Task SyncDataAsync_OnTheFirstRun_WalksTheFullHistoryAndRecordsIt()
    {
        var handler = new GlookoEndpointHandler();
        var store = new FakeCursorStore();
        var service = GlookoSyncHarness.Service(handler, cursorStore: store);

        var result = await service.SyncDataAsync(
            OpenEnded(), GlookoSyncHarness.Config(useV3Api: true), CancellationToken.None);

        result.Success.Should().BeTrue();
        handler.WindowCount.Should().Be(FullWalkChunks());
        Parse(handler.Windows[0].Start).Should().BeCloseTo(
            DateTime.UtcNow.AddMonths(-GlookoConstants.FullWalkMonths).AddDays(-1), TimeSpan.FromHours(1));

        var stamp = store.Saved.Should().ContainKey(GlookoConstants.FullWalkCursorResource).WhoseValue;
        FullWalkSchedule.IsDue(stamp.LastUpdatedAt, GlookoConstants.FullWalkInterval, DateTimeOffset.UtcNow)
            .Should().BeFalse("the walk that just completed is what the next run stands on");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SyncDataAsync_InsideTheInterval_ReachesBackOnlyTheLookback(bool useV3Api)
    {
        var handler = new GlookoEndpointHandler();
        var seeded = RecentWalk();
        var store = new FakeCursorStore { Saved = { [GlookoConstants.FullWalkCursorResource] = seeded } };
        var service = GlookoSyncHarness.Service(handler, cursorStore: store);
        var config = GlookoSyncHarness.Config(useV3Api);

        var result = await service.SyncDataAsync(OpenEnded(), config, CancellationToken.None);

        result.Success.Should().BeTrue();
        // The lookback plus the day of padding on each side is exactly one chunk.
        handler.WindowCount.Should().Be(1);
        Parse(handler.Windows[0].Start).Should().BeCloseTo(
            DateTime.UtcNow.AddDays(-config.LookbackDays - 1), TimeSpan.FromHours(1));
        store.Saved[GlookoConstants.FullWalkCursorResource].Should().BeSameAs(seeded,
            "an incremental run leaves the walk stamp alone");
        service.Published.Should().Contain(PublishKind.StateSpans);
    }

    [Fact]
    public async Task SyncDataAsync_InsideTheInterval_HonoursAWiderLookback()
    {
        var handler = new GlookoEndpointHandler();
        var store = new FakeCursorStore { Saved = { [GlookoConstants.FullWalkCursorResource] = RecentWalk() } };
        var service = GlookoSyncHarness.Service(handler, cursorStore: store);
        var config = GlookoSyncHarness.Config(useV3Api: true);
        config.LookbackDays = 30;

        await service.SyncDataAsync(OpenEnded(), config, CancellationToken.None);

        handler.WindowCount.Should().Be(3, "30 days plus two of padding is three fortnightly chunks");
        Parse(handler.Windows[0].Start).Should().BeCloseTo(
            DateTime.UtcNow.AddDays(-31), TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task SyncDataAsync_OnceTheIntervalHasElapsed_WalksAgain()
    {
        var handler = new GlookoEndpointHandler();
        var overdue = DateTimeOffset.UtcNow - GlookoConstants.FullWalkInterval - TimeSpan.FromMinutes(1);
        var store = new FakeCursorStore
        {
            Saved = { [GlookoConstants.FullWalkCursorResource] = new(FullWalkSchedule.Stamp(overdue), null) },
        };
        var service = GlookoSyncHarness.Service(handler, cursorStore: store);

        await service.SyncDataAsync(OpenEnded(), GlookoSyncHarness.Config(useV3Api: true), CancellationToken.None);

        handler.WindowCount.Should().Be(FullWalkChunks());
        Parse(store.Saved[GlookoConstants.FullWalkCursorResource].LastUpdatedAt!)
            .Should().BeAfter(overdue.UtcDateTime, "the new walk replaces the stale stamp");
    }

    /// <summary>
    /// A chunk that fails stops the pass, so the walk has not covered its history and must not be
    /// counted; the next run walks again instead of settling into the lookback.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenTheWalkFails_DoesNotRecordIt()
    {
        var handler = new GlookoEndpointHandler(failingPaths: [GlookoConstants.V3GraphDataPath]);
        var store = new FakeCursorStore();
        var service = GlookoSyncHarness.Service(handler, cursorStore: store);

        var result = await service.SyncDataAsync(
            OpenEnded(), GlookoSyncHarness.Config(useV3Api: true), CancellationToken.None);

        result.Success.Should().BeFalse();
        store.Saved.Should().NotContainKey(GlookoConstants.FullWalkCursorResource);
    }

    /// <summary>
    /// The caller's lower bound and the schedule's each reach as far back as the other allows: a
    /// repair from six weeks ago is not clipped to the lookback, and a bound inside the lookback
    /// does not narrow it.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WithACallerLowerBound_WidensButNeverNarrowsTheLookback()
    {
        var store = new FakeCursorStore { Saved = { [GlookoConstants.FullWalkCursorResource] = RecentWalk() } };
        var config = GlookoSyncHarness.Config(useV3Api: true);

        var wider = new GlookoEndpointHandler();
        await GlookoSyncHarness.Service(wider, cursorStore: store).SyncDataAsync(
            OpenEnded(from: DateTime.UtcNow.AddDays(-42)), config, CancellationToken.None);

        wider.WindowCount.Should().Be(4, "six weeks plus padding is four chunks");
        Parse(wider.Windows[0].Start).Should().BeCloseTo(DateTime.UtcNow.AddDays(-43), TimeSpan.FromHours(1));

        var narrower = new GlookoEndpointHandler();
        await GlookoSyncHarness.Service(narrower, cursorStore: store).SyncDataAsync(
            OpenEnded(from: DateTime.UtcNow.AddDays(-2)), config, CancellationToken.None);

        narrower.WindowCount.Should().Be(1);
        Parse(narrower.Windows[0].Start).Should().BeCloseTo(
            DateTime.UtcNow.AddDays(-config.LookbackDays - 1), TimeSpan.FromHours(1));
    }

    /// <summary>
    /// An explicit range is a manual re-import of one window: it is answered as asked whatever the
    /// schedule says, and it is not a walk, so it neither records one nor consumes one.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WithAnExplicitRange_IgnoresTheSchedule()
    {
        var handler = new GlookoEndpointHandler();
        var store = new FakeCursorStore();
        var service = GlookoSyncHarness.Service(handler, cursorStore: store);

        var result = await service.SyncDataAsync(
            new SyncRequest
            {
                From = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                To = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
                DataTypes = [.. Types],
            },
            GlookoSyncHarness.Config(useV3Api: true), CancellationToken.None);

        result.Success.Should().BeTrue();
        handler.WindowCount.Should().Be(3);
        store.Saved.Should().BeEmpty();
    }

    /// <summary>
    /// Nothing can remember a walk without a store, so a detached service (dry-run tooling) has no
    /// schedule: it answers the caller's bound, or the floor when there is none, as it always did.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WithoutACursorStore_AnswersTheCallerOrTheFloor()
    {
        var floor = new GlookoEndpointHandler();
        await GlookoSyncHarness.Service(floor).SyncDataAsync(
            OpenEnded(), GlookoSyncHarness.Config(useV3Api: true), CancellationToken.None);

        floor.WindowCount.Should().Be(FullWalkChunks());

        var bounded = new GlookoEndpointHandler();
        await GlookoSyncHarness.Service(bounded).SyncDataAsync(
            OpenEnded(from: DateTime.UtcNow.AddDays(-3)), GlookoSyncHarness.Config(useV3Api: true),
            CancellationToken.None);

        bounded.WindowCount.Should().Be(1, "the caller's three days are not widened to any lookback");
        Parse(bounded.Windows[0].Start).Should().BeCloseTo(DateTime.UtcNow.AddDays(-4), TimeSpan.FromHours(1));
    }

    private static SyncRequest OpenEnded(DateTime? from = null) => new() { From = from, DataTypes = [.. Types] };

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
