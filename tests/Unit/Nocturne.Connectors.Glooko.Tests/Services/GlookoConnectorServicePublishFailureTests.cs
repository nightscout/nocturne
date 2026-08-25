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
/// A rejected publish must reach <see cref="SyncResult.Success"/> and <see cref="SyncResult.Errors"/>
/// for every data type Glooko syncs, on both the V2 and the V3 fetch path. A tenant whose state spans,
/// temp basals, device events, system events, profiles or food entries never land otherwise sees a
/// green sync with that data missing, indistinguishable from a cycle that had none of it to publish.
/// </summary>
public class GlookoConnectorServicePublishFailureTests
{
    // Device and system events come from the V3 graph series; the V2 endpoints carry neither.
    [Theory]
    [InlineData(true, PublishKind.StateSpans)]
    [InlineData(true, PublishKind.ProfileStateSpans)]
    [InlineData(true, PublishKind.TempBasals)]
    [InlineData(true, PublishKind.DeviceEvents)]
    [InlineData(true, PublishKind.SystemEvents)]
    [InlineData(true, PublishKind.Profiles)]
    [InlineData(false, PublishKind.StateSpans)]
    [InlineData(false, PublishKind.ProfileStateSpans)]
    [InlineData(false, PublishKind.TempBasals)]
    [InlineData(false, PublishKind.Profiles)]
    public async Task SyncDataAsync_WhenOnePublishIsRejected_ReportsFailure(
        bool useV3Api, PublishKind rejected)
    {
        var service = BuildService(rejected);

        var result = await service.SyncDataAsync(
            BuildRequest(), BuildConfig(useV3Api), CancellationToken.None);

        service.Published.Should().Contain(rejected,
            "the payload must actually reach the publish under test, or the assertions below prove nothing");
        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle();
    }

    /// <summary>
    /// The result is constructed optimistically, so a publish-only failure must not leave the
    /// success literal behind: <see cref="SyncResult.Message"/> is the documented fallback for
    /// consumers that find no <see cref="SyncResult.Errors"/>.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SyncDataAsync_WhenAPublishIsRejected_ReplacesTheOptimisticMessage(bool useV3Api)
    {
        var service = BuildService(PublishKind.StateSpans);

        var result = await service.SyncDataAsync(
            BuildRequest(), BuildConfig(useV3Api), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Sync failed while publishing data");
    }

    /// <summary>
    /// Profile state spans are state spans, so the tenant's StateSpans toggle governs them even
    /// though the device-settings fetch that produces them is gated on Profiles.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SyncDataAsync_WhenStateSpansAreNotRequested_DoesNotPublishProfileStateSpans(
        bool useV3Api)
    {
        var service = BuildService(rejected: null);
        var request = BuildRequest();
        request.DataTypes = [SyncDataType.Profiles];

        var result = await service.SyncDataAsync(request, BuildConfig(useV3Api), CancellationToken.None);

        result.Success.Should().BeTrue();
        service.Published.Should().Contain(PublishKind.Profiles);
        service.Published.Should().NotContain(PublishKind.ProfileStateSpans);
        result.ItemsSynced.Should().NotContainKey(SyncDataType.StateSpans);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SyncDataAsync_WhenEveryPublishIsAccepted_ReportsSuccessAndCountsEachType(
        bool useV3Api)
    {
        var service = BuildService(rejected: null);

        var result = await service.SyncDataAsync(
            BuildRequest(), BuildConfig(useV3Api), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Errors.Should().BeEmpty();
        // One span from the chunk's suspended basal, one from the device settings' active program.
        result.ItemsSynced[SyncDataType.StateSpans].Should().Be(2);
        result.ItemsSynced[SyncDataType.Profiles].Should().Be(1);

        if (useV3Api)
        {
            result.ItemsSynced[SyncDataType.TempBasals].Should().Be(1);
            // Device events and system events both count here — one of each.
            result.ItemsSynced[SyncDataType.DeviceEvents].Should().Be(2);
        }
        else
        {
            // V2 draws temp basals from the temporary-basal and suspend-basal endpoints alike.
            result.ItemsSynced[SyncDataType.TempBasals].Should().Be(2);
            result.ItemsSynced.Should().NotContainKey(SyncDataType.DeviceEvents);
        }
    }

    /// <summary>
    /// The food-entry publisher is metadata-shaped: it answers with the imported entries, and with
    /// <c>null</c> only from its own catch. A rejected import must therefore be reported, and an
    /// import that accepted nothing must not be. Both fetch paths share the publish helper; the V2
    /// endpoints are the ones that carry standalone foods.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenTheFoodEntryPublishFails_ReportsFailure()
    {
        var (publisher, metadata) = BuildFoodPublisher(imported: null);
        var service = BuildService(rejected: null, publisher);

        var result = await service.SyncDataAsync(
            BuildFoodRequest(), BuildConfig(useV3Api: false), CancellationToken.None);

        VerifyFoodEntriesWerePublished(metadata);
        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle()
            .Which.Should().Contain("Food");
    }

    /// <summary>
    /// An import the catalog accepted nothing from still counts what the sync handed over, so the
    /// tenant's card shows a food badge rather than the blank that reads as "never checked".
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenTheFoodEntryPublishImportsNothing_ReportsSuccessAndCountsFood()
    {
        var (publisher, metadata) = BuildFoodPublisher(imported: []);
        var service = BuildService(rejected: null, publisher);

        var result = await service.SyncDataAsync(
            BuildFoodRequest(), BuildConfig(useV3Api: false), CancellationToken.None);

        VerifyFoodEntriesWerePublished(metadata);
        result.Success.Should().BeTrue();
        result.Errors.Should().BeEmpty();
        result.ItemsSynced[SyncDataType.Food].Should().Be(1);
    }

    /// <summary>
    /// The scheduled sync names no data types and is expanded to every supported one, so that — not
    /// the hand-listed request the cases above use — is what a tenant's connector actually runs. A
    /// window that held no food still owes the card the zero that reads as "checked, found nothing".
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenTheRequestNamesNoTypes_CountsFoodAsChecked()
    {
        var service = BuildService(rejected: null);
        var request = new SyncRequest { From = DateTime.UtcNow.AddDays(-3) };

        var result = await service.SyncDataAsync(
            request, BuildConfig(useV3Api: true), CancellationToken.None);

        request.DataTypes.Should().Contain(SyncDataType.Food,
            "the expansion must actually reach food, or the assertions below prove nothing");
        result.Success.Should().BeTrue();
        result.Errors.Should().BeEmpty();
        result.ItemsSynced[SyncDataType.Food].Should().Be(0);
    }

    // ── Test infrastructure ─────────────────────────────────────────────

    private static SyncRequest BuildRequest() => new()
    {
        DataTypes =
        [
            SyncDataType.StateSpans, SyncDataType.TempBasals,
            SyncDataType.DeviceEvents, SyncDataType.Profiles,
        ],
        From = DateTime.UtcNow.AddDays(-3), // single chunk keeps one request per endpoint
    };

    /// <summary>
    /// Food is its own toggle, so a request that does not name it publishes no food at all — the
    /// food cases have to ask for it.
    /// </summary>
    private static SyncRequest BuildFoodRequest()
    {
        var request = BuildRequest();
        request.DataTypes = [.. request.DataTypes, SyncDataType.Food];
        return request;
    }

    private static GlookoConnectorConfiguration BuildConfig(bool useV3Api) =>
        GlookoSyncHarness.Config(useV3Api);

    private static RecordingGlookoConnectorService BuildService(
        PublishKind? rejected, IConnectorPublisher? publisher = null) =>
        GlookoSyncHarness.Service(new GlookoEndpointHandler(), rejected, publisher);

    /// <summary>
    /// A publisher whose food-entry import answers <paramref name="imported"/>. Every other publish
    /// the sync reaches is intercepted by <see cref="RecordingGlookoConnectorService"/>.
    /// </summary>
    private static (IConnectorPublisher Publisher, Mock<IMetadataPublisher> Metadata) BuildFoodPublisher(
        IReadOnlyList<ConnectorFoodEntry>? imported)
    {
        var metadata = new Mock<IMetadataPublisher>();
        metadata
            .Setup(m => m.PublishConnectorFoodEntriesAsync(
                It.IsAny<IEnumerable<ConnectorFoodEntryImport>>(), It.IsAny<string>(),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(imported);

        var publisher = new Mock<IConnectorPublisher>();
        publisher.SetupGet(p => p.IsAvailable).Returns(true);
        publisher.SetupGet(p => p.Metadata).Returns(metadata.Object);

        return (publisher.Object, metadata);
    }

    private static void VerifyFoodEntriesWerePublished(Mock<IMetadataPublisher> metadata) =>
        metadata.Verify(
            m => m.PublishConnectorFoodEntriesAsync(
                It.IsAny<IEnumerable<ConnectorFoodEntryImport>>(), It.IsAny<string>(),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "the import must actually be attempted, or the assertions below prove nothing");
}
