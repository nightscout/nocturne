using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Services;
using Nocturne.Tests.Shared.Connectors;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Services;

/// <summary>
/// The walk schedule kept in the connector's resume state, driven over the shared rig: what a reset
/// gives up.
/// </summary>
public class GlookoConnectorStateTests
{
    private static readonly DateTimeOffset Instant = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private const string Ssv2Resource = "/api/v2/cgm/egvs";

    private static GlookoConnectorConfiguration ScheduledConfig()
    {
        var config = GlookoSyncHarness.Config(useV3Api: true);
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

    private static GlookoConnectorService Service(
        GlookoEndpointHandler handler, IConnectorPublisher publisher, bool signInRefused = false) =>
        new(
            new HttpClient(handler),
            new ConnectorServerResolver<GlookoConnectorConfiguration>(null, null, null),
            NullLogger<GlookoConnectorService>.Instance,
            Mock.Of<IRetryDelayStrategy>(),
            new StaticGlookoTokenProvider(refuses: signInRefused),
            publisher,
            timeProvider: new FakeTimeProvider(Instant),
            endpointPause: TimeSpan.Zero);

    /// <summary>
    /// A reset forgets the walk, so the next scheduled run walks the whole history again and offers
    /// every record the first walk offered; the store already holds each of them. The SSV2 resource cursors are
    /// vendor state and stay.
    /// </summary>
    [Fact]
    public async Task ScheduledRun_AfterAReset_WalksAgainAndOffersEveryRecordAgain()
    {
        var store = new StoringConnectorPublisher();
        var state = (InMemoryConnectorStateStore)store.State;
        state.Vendor[Ssv2Resource] = JsonNode.Parse("""{"LastUpdatedAt":"2026-02-01T00:00:00.000Z","LastGuid":"g1"}""")!;
        var first = new GlookoEndpointHandler();

        (await Service(first, store).SyncDataAsync(ScheduledConfig(), CancellationToken.None)).Success.Should().BeTrue();
        var offered = store.Offered;
        state.Resume.Should().ContainKey(GlookoConstants.FullWalkStamp);

        var reset = SyncRequest.Reset(Instant.UtcDateTime.AddDays(-3), Instant.UtcDateTime, null);
        (await Service(new GlookoEndpointHandler(), store).SyncDataAsync(reset, ScheduledConfig(), CancellationToken.None))
            .Success.Should().BeTrue();
        state.Resume.Should().BeEmpty("a reset forgets the walk and a requested run records none");
        var offeredBeforeTheWalk = store.Offered;
        var storedBeforeTheWalk = store.Stored.Keys.ToList();

        var next = new GlookoEndpointHandler();
        (await Service(next, store).SyncDataAsync(ScheduledConfig(), CancellationToken.None)).Success.Should().BeTrue();

        next.WindowCount.Should().Be(first.WindowCount);
        store.Offered.Should().BeEquivalentTo(offeredBeforeTheWalk.ToDictionary(
            o => o.Key, o => o.Value + offered.GetValueOrDefault(o.Key)));
        store.Stored.Keys.Should().BeEquivalentTo(storedBeforeTheWalk);
        state.Resume[GlookoConstants.FullWalkStamp].Should().Be(Instant.UtcDateTime);
        FullWalkSchedule.IsDue(state.Resume[GlookoConstants.FullWalkStamp], GlookoConstants.FullWalkInterval, Instant)
            .Should().BeFalse();
        state.Vendor.Keys.Should().Equal(Ssv2Resource);
    }

    private static StoringConnectorPublisher StoreWithTheSchedule()
    {
        var store = new StoringConnectorPublisher();
        var state = (InMemoryConnectorStateStore)store.State;
        state.Resume[GlookoConstants.FullWalkStamp] = Instant.UtcDateTime.AddDays(-1);
        state.Resume[GlookoConstants.FullWalkAttemptStamp] = Instant.UtcDateTime.AddDays(-1);
        state.Resume[GlookoConstants.LookbackReadStamp] = Instant.UtcDateTime.AddHours(-2);
        state.Vendor[Ssv2Resource] = JsonNode.Parse("""{"LastUpdatedAt":"2026-02-01T00:00:00.000Z","LastGuid":"g1"}""")!;
        return store;
    }

    [Fact]
    public async Task A_reset_whose_sign_in_is_refused_leaves_the_schedule_and_the_vendor_state_as_they_were()
    {
        var store = StoreWithTheSchedule();
        var state = (InMemoryConnectorStateStore)store.State;
        var resumeBefore = new Dictionary<string, DateTime>(state.Resume);
        var vendorBefore = state.Vendor.ToDictionary(v => v.Key, v => v.Value.ToJsonString());
        var handler = new GlookoEndpointHandler();

        var result = await Service(handler, store, signInRefused: true).SyncDataAsync(
            SyncRequest.Reset(null, Instant.UtcDateTime, null), ScheduledConfig(), CancellationToken.None);

        result.Success.Should().BeFalse("the sign-in was refused");
        state.Resume.Should().BeEquivalentTo(resumeBefore);
        state.Vendor.ToDictionary(v => v.Key, v => v.Value.ToJsonString()).Should().BeEquivalentTo(vendorBefore);
        handler.WindowCount.Should().Be(0);
    }

    /// <summary>
    /// The schedule chooses the window of every type, so a reset of one type keeps it; the reset run
    /// reads the window it was given for that type.
    /// </summary>
    [Fact]
    public async Task A_reset_of_one_type_keeps_the_schedule_and_reads_the_window_it_was_given()
    {
        var store = StoreWithTheSchedule();
        var state = (InMemoryConnectorStateStore)store.State;
        var resumeBefore = new Dictionary<string, DateTime>(state.Resume);
        var handler = new GlookoEndpointHandler();

        var reset = SyncRequest.Reset(Instant.UtcDateTime.AddDays(-3), Instant.UtcDateTime, [SyncDataType.TempBasals]);
        (await Service(handler, store).SyncDataAsync(reset, ScheduledConfig(), CancellationToken.None))
            .Success.Should().BeTrue();

        state.Resume.Should().BeEquivalentTo(resumeBefore);
        handler.WindowCount.Should().Be(1);
        DateTime.Parse(handler.Windows[0].Start, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal)
            .Should().Be(Instant.UtcDateTime.AddDays(-4));
        store.Offered.Keys.Should().NotBeEmpty();
    }
}
