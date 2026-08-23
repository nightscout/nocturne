using FluentAssertions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Services;

/// <summary>
/// Glooko's fetches each sit behind their own catch — the supporting ones singly, the V2 batch's
/// endpoints in a loop — so a Glooko endpoint that is down used to cost the tenant that data while
/// the run still reported green. A failed fetch of a type the tenant enabled must reach
/// <see cref="SyncResult.Success"/> and <see cref="SyncResult.Errors"/>, while whatever the run
/// already fetched still publishes.
/// </summary>
public class GlookoConnectorServiceFetchFailureTests
{
    /// <summary>
    /// Every V2 batch endpoint against the types its payload feeds, so a row that stops matching what
    /// the mappers actually read off that payload ships red. Glucose is the connector's primary type
    /// and has one V2 source, so a dead CGM endpoint reporting green was indistinguishable from a
    /// quiet day; a bolus carries the wizard's carbs alongside its insulin, foods become carb intakes
    /// as well as catalog entries, and all three basal endpoints feed temp basals.
    /// </summary>
    /// <param name="stillPublished">
    ///     A publish the failing endpoint has no part in, which must still be reached — the failure is
    ///     sticky, not an abort.
    /// </param>
    [Theory]
    [InlineData(GlookoConstants.CgmReadingsPath, "Glucose", PublishKind.StateSpans)]
    [InlineData(GlookoConstants.MeterReadingsPath, "ManualBG", PublishKind.StateSpans)]
    [InlineData(GlookoConstants.NormalBolusesPath, "Boluses,CarbIntake", PublishKind.StateSpans)]
    [InlineData(GlookoConstants.FoodsPath, "CarbIntake,Food", PublishKind.StateSpans)]
    [InlineData(GlookoConstants.ScheduledBasalsPath, "TempBasals", PublishKind.StateSpans)]
    [InlineData(GlookoConstants.TemporaryBasalsPath, "TempBasals", PublishKind.StateSpans)]
    [InlineData(GlookoConstants.SuspendBasalsPath, "StateSpans,TempBasals", PublishKind.TempBasals)]
    public async Task SyncDataAsync_WhenAV2BatchEndpointFails_ReportsEveryTypeItServes(
        string failingPath, string expectedTypes, PublishKind stillPublished)
    {
        var service = BuildService(failingPath);

        var result = await service.SyncDataAsync(
            Request(V2BatchTypes), GlookoSyncHarness.Config(useV3Api: false), CancellationToken.None);

        // The whole batch is fetched before anything publishes, so a publish rejection can never be
        // the failure that named the run.
        result.Message.Should().Be("Sync failed while fetching data");
        result.Errors
            .Where(error => error.StartsWith("Failed to fetch ", StringComparison.Ordinal))
            .Should().BeEquivalentTo(
                expectedTypes.Split(',').Select(type => $"Failed to fetch {type}"));
        service.Published.Should().Contain(stillPublished);
    }

    /// <summary>
    /// A payload that arrived but would not map loses the tenant exactly what one that never arrived
    /// does, so the two report alike.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenAV2BatchEndpointServesUnmappablePayload_ReportsItsType()
    {
        var service = GlookoSyncHarness.Service(
            new GlookoEndpointHandler(malformedPaths: [GlookoConstants.CgmReadingsPath]));

        var result = await service.SyncDataAsync(
            Request(SyncDataType.Glucose, SyncDataType.StateSpans),
            GlookoSyncHarness.Config(useV3Api: false), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Be("Failed to fetch Glucose");
        service.Published.Should().Contain(PublishKind.StateSpans);
    }

    /// <summary>
    /// A failed run withholds the connector's last-successful-sync stamp, so a batch endpoint whose
    /// types the tenant switched off must not be able to fail the sync.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenAV2BatchEndpointFailsForSwitchedOffTypes_ReportsSuccess()
    {
        var service = BuildService(GlookoConstants.CgmReadingsPath);

        var result = await service.SyncDataAsync(
            Request(SyncDataType.StateSpans), GlookoSyncHarness.Config(useV3Api: false),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Errors.Should().BeEmpty();
        service.Published.Should().Contain(PublishKind.StateSpans);
    }

    /// <summary>
    /// Device settings are the only profile source in either fetch mode, so both modes must report
    /// the loss — and both must still publish the chunk's own state spans.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SyncDataAsync_WhenTheDeviceSettingsFetchFails_ReportsFailureAndKeepsPublishing(
        bool useV3Api)
    {
        var service = BuildService(GlookoConstants.V3DeviceSettingsPath);

        var result = await service.SyncDataAsync(
            Request(SyncDataType.Profiles, SyncDataType.StateSpans),
            GlookoSyncHarness.Config(useV3Api), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Be("Failed to fetch Profiles");
        service.Published.Should().NotContain(PublishKind.Profiles);
        service.Published.Should().Contain(PublishKind.StateSpans);
    }

    /// <summary>
    /// Histories carry the meals the V3 path draws carbs from; without them carbs silently fall back
    /// to the coarser carbAll series. <see cref="SyncResult.Message"/> is what a reader with no
    /// <see cref="SyncResult.Errors"/> is shown, so it must name the fetch and not a publish.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenTheHistoriesFetchFails_ReportsFailureAsAFetch()
    {
        var service = BuildService(GlookoConstants.V3HistoriesPath);

        var result = await service.SyncDataAsync(
            Request(SyncDataType.CarbIntake), GlookoSyncHarness.Config(useV3Api: true),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Be("Failed to fetch CarbIntake");
        result.Message.Should().Be("Sync failed while fetching data");
    }

    /// <summary>
    /// A failed run withholds the connector's last-successful-sync stamp, so a fetch that only feeds
    /// a type the tenant switched off must not be able to fail the sync.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenTheHistoriesFetchFailsForSwitchedOffTypes_ReportsSuccess()
    {
        var service = BuildService(GlookoConstants.V3HistoriesPath);

        var result = await service.SyncDataAsync(
            Request(SyncDataType.StateSpans), GlookoSyncHarness.Config(useV3Api: true),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    /// <summary>
    /// The V2 foods endpoint only enriches V3 food entries with externalId and brand, so its failure
    /// is sticky rather than fatal: the entries still publish, and still count.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenTheFoodMetadataFetchFails_ReportsFailureAndStillPublishesFood()
    {
        var (publisher, metadata) = BuildFoodPublisher();
        var service = GlookoSyncHarness.Service(
            new GlookoEndpointHandler(
                failingPaths: [GlookoConstants.FoodsPath], withHistoryMeals: true),
            rejected: null, publisher);

        var result = await service.SyncDataAsync(
            Request(SyncDataType.Food), GlookoSyncHarness.Config(useV3Api: true),
            CancellationToken.None);

        metadata.Verify(
            m => m.PublishConnectorFoodEntriesAsync(
                It.IsAny<IEnumerable<ConnectorFoodEntryImport>>(), It.IsAny<string>(),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "the entries must still reach the catalog, or the assertions below prove nothing");
        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Be("Failed to fetch Food");
        result.ItemsSynced[SyncDataType.Food].Should().Be(1);
    }

    // ── Test infrastructure ─────────────────────────────────────────────

    /// <summary>Every type the V2 batch endpoints serve, so no row is gated off by an inactive type.</summary>
    private static readonly SyncDataType[] V2BatchTypes =
    [
        SyncDataType.Glucose, SyncDataType.ManualBG, SyncDataType.Boluses, SyncDataType.CarbIntake,
        SyncDataType.Food, SyncDataType.StateSpans, SyncDataType.TempBasals,
    ];

    private static SyncRequest Request(params SyncDataType[] dataTypes) => new()
    {
        DataTypes = [.. dataTypes],
        From = DateTime.UtcNow.AddDays(-3), // single chunk keeps one request per endpoint
    };

    private static RecordingGlookoConnectorService BuildService(params string[] failingPaths) =>
        GlookoSyncHarness.Service(new GlookoEndpointHandler(failingPaths: failingPaths));

    private static (IConnectorPublisher Publisher, Mock<IMetadataPublisher> Metadata) BuildFoodPublisher()
    {
        var metadata = new Mock<IMetadataPublisher>();
        metadata
            .Setup(m => m.PublishConnectorFoodEntriesAsync(
                It.IsAny<IEnumerable<ConnectorFoodEntryImport>>(), It.IsAny<string>(),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var publisher = new Mock<IConnectorPublisher>();
        publisher.SetupGet(p => p.IsAvailable).Returns(true);
        publisher.SetupGet(p => p.Metadata).Returns(metadata.Object);

        return (publisher.Object, metadata);
    }
}
