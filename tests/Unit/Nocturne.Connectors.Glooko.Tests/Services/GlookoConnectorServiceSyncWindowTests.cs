using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Glooko.Configurations;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Services;

/// <summary>
/// The chunk loop is bounded at both ends by the request. A run that reads only the lower bound
/// answers a re-import of one month by crawling every chunk from that month to today.
/// </summary>
public class GlookoConnectorServiceSyncWindowTests
{
    private static readonly DateTime AskedFrom = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime AskedTo = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SyncDataAsync_WhenGivenAnUpperBound_StopsTheChunkLoopThere(bool useV3Api)
    {
        var handler = new GlookoEndpointHandler();
        var service = GlookoSyncHarness.Service(handler);

        var result = await service.SyncDataAsync(
            new SyncRequest
            {
                From = AskedFrom,
                To = AskedTo,
                DataTypes =
                [
                    SyncDataType.StateSpans, SyncDataType.TempBasals,
                    SyncDataType.DeviceEvents, SyncDataType.Profiles,
                ],
            },
            GlookoSyncHarness.Config(useV3Api),
            CancellationToken.None);

        result.Success.Should().BeTrue();

        // A month, padded a day each side, is three of the connector's fortnightly chunks.
        handler.WindowCount.Should().Be(3);
        Parse(handler.Windows[^1].End).Should().BeBefore(AskedTo.AddDays(2),
            "the caller bounded the range at both ends");
    }

    [Theory]
    [InlineData(10)]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task SyncDataAsync_WithNoRange_ReachesBackTheFullWalkFromTheClockInGlookoTime(int deviceOffsetHours)
    {
        var now = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        var handler = new GlookoEndpointHandler();
        var service = GlookoSyncHarness.Service(handler, clock: new FakeTimeProvider(now));
        var config = GlookoSyncHarness.Config(useV3Api: false);
        config.TimezoneOffset = deviceOffsetHours;

        var result = await service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.TempBasals] }, config, CancellationToken.None);

        result.Success.Should().BeTrue();
        var expectedFrom = now.UtcDateTime.AddMonths(-GlookoConstants.FullWalkMonths).AddHours(deviceOffsetHours).AddDays(-1);
        var expectedTo = now.UtcDateTime.AddHours(deviceOffsetHours).AddDays(1);
        Parse(handler.Windows[0].Start).Should().Be(expectedFrom);
        Parse(handler.Windows[^1].End).Should().Be(expectedTo);
    }

    private static DateTime Parse(string timestamp) =>
        DateTime.Parse(timestamp, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
}
