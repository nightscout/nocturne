using FluentAssertions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Glooko.Configurations;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Services;

/// <summary>
/// A sync window wider than one chunk is fetched and published a chunk at a time, so every
/// <see cref="SyncResult.ItemsSynced"/> entry must be the sum over the chunks. A count that is
/// assigned rather than accumulated reports only the last chunk's work, and a publish that never
/// touches the dictionary reports none at all — either way the tenant's sync summary understates
/// what landed.
/// </summary>
public class GlookoConnectorServiceMultiChunkTests
{
    /// <summary>
    /// The chunks carry different record counts (one, then two), so a per-chunk count, the last
    /// chunk's count and the sum are three different numbers.
    /// </summary>
    private static int RecordsInChunk(int chunkOrdinal) => chunkOrdinal + 1;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SyncDataAsync_AcrossChunks_SumsStateSpansAndTempBasals(bool useV3Api)
    {
        var handler = new GlookoEndpointHandler(RecordsInChunk);
        var service = GlookoSyncHarness.Service(handler);

        var result = await service.SyncDataAsync(
            BuildRequest(), GlookoSyncHarness.Config(useV3Api), CancellationToken.None);

        handler.WindowCount.Should().Be(2,
            "the window must actually span more than one chunk, or the assertions below prove nothing");
        result.Success.Should().BeTrue();
        result.Errors.Should().BeEmpty();

        // Three suspended basals across the two chunks, plus the device settings' active program.
        result.ItemsSynced[SyncDataType.StateSpans].Should().Be(4);

        // V2 draws temp basals from the temporary-basal and suspend-basal endpoints alike; V3's
        // suspended basals are the only temp-basal series the payload carries.
        result.ItemsSynced[SyncDataType.TempBasals].Should().Be(useV3Api ? 3 : 6);

        if (useV3Api)
            // One reservoir change (device event) and one pump alarm (system event) per record.
            result.ItemsSynced[SyncDataType.DeviceEvents].Should().Be(6);

        // The device-settings fetch runs once per pass, after the chunks.
        result.ItemsSynced[SyncDataType.Profiles].Should().Be(1);
    }

    /// <summary>
    /// A sync that asks for state spans and nothing else must still report the spans it landed,
    /// rather than publishing them uncounted and summarising the run as zero work.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SyncDataAsync_AcrossChunks_CountsStateSpansWhenTheyAreTheOnlyTypeRequested(
        bool useV3Api)
    {
        var handler = new GlookoEndpointHandler(RecordsInChunk);
        var service = GlookoSyncHarness.Service(handler);
        var request = BuildRequest();
        request.DataTypes = [SyncDataType.StateSpans];

        var result = await service.SyncDataAsync(
            request, GlookoSyncHarness.Config(useV3Api), CancellationToken.None);

        handler.WindowCount.Should().Be(2,
            "the window must actually span more than one chunk, or the assertions below prove nothing");
        result.Success.Should().BeTrue();
        service.Published.Should().Contain(PublishKind.StateSpans);

        // Profiles are not requested, so the device-settings spans are never fetched.
        result.ItemsSynced[SyncDataType.StateSpans].Should().Be(3);
        result.ItemsSynced.Should().NotContainKey(SyncDataType.Profiles);
    }

    /// <summary>
    /// An endpoint that is down is down for every chunk, and the terminal progress message joins the
    /// whole error list, so a six-month window would otherwise hand the tenant the same two sentences
    /// thirteen times over.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_AcrossChunks_ReportsOneEntryPerDistinctFetchFailure()
    {
        var handler = new GlookoEndpointHandler(
            RecordsInChunk, failingPaths: [GlookoConstants.V3HistoriesPath]);
        var service = GlookoSyncHarness.Service(handler);
        var request = BuildRequest();
        request.DataTypes = [SyncDataType.CarbIntake, SyncDataType.Food];

        var result = await service.SyncDataAsync(
            request, GlookoSyncHarness.Config(useV3Api: true), CancellationToken.None);

        handler.WindowCount.Should().Be(2,
            "the window must actually span more than one chunk, or the assertions below prove nothing");
        result.Success.Should().BeFalse();
        result.Errors.Should().BeEquivalentTo(
            ["Failed to fetch CarbIntake", "Failed to fetch Food"]);
    }

    /// <summary>
    /// A window one day wider than a chunk, padded a day each side by the sync itself, spans exactly
    /// two chunks.
    /// </summary>
    private static SyncRequest BuildRequest() => new()
    {
        DataTypes =
        [
            SyncDataType.StateSpans, SyncDataType.TempBasals,
            SyncDataType.DeviceEvents, SyncDataType.Profiles,
        ],
        From = DateTime.UtcNow - (GlookoConstants.SyncChunkSize + TimeSpan.FromDays(1)),
    };
}
