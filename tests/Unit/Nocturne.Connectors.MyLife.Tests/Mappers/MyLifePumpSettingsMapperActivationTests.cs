using FluentAssertions;
using Nocturne.Connectors.MyLife.Mappers;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Connectors.MyLife.Tests.Mappers;

public class MyLifePumpSettingsMapperActivationTests
{
    private const string ConnectorSource = "mylife-connector";

    private static long Ticks(int year, int month, int day, int hour = 0, int minute = 0)
    {
        var mills = new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        return mills * 10_000;
    }

    private static MyLifePumpSettingsReadout Readout(
        string id,
        long uploadTicks,
        string? activeBasalProgramName,
        string? deviceSerialNumber = "SN-1")
    {
        return new MyLifePumpSettingsReadout
        {
            Id = id,
            UploadDateTime = uploadTicks,
            ActiveBasalProgramName = activeBasalProgramName,
            DeviceSerialNumber = deviceSerialNumber,
        };
    }

    [Fact]
    public void MapToProfileActivations_SingleReadoutWithProgram_EmitsOpenEndedActivation()
    {
        var readouts = new[]
        {
            Readout("r1", Ticks(2026, 4, 1, 10), "Morning"),
        };

        var spans = MyLifePumpSettingsMapper.MapToProfileActivations(readouts, ConnectorSource);

        spans.Should().HaveCount(1);
        var span = spans[0];
        span.Phase.Should().Be(ProfileActivationPhase.Effective);
        span.Origin.Should().Be(ProfileActivationOrigin.Connector);
        span.DataSource.Should().Be(ConnectorSource);
        span.StartTimestamp.Should().Be(new DateTime(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc));
        span.EndTimestamp.Should().BeNull();
        span.BaseProfileName.Should().Be("Morning");
        span.SyncIdentifier.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void MapToProfileActivations_MultipleReadouts_EmitsAbuttingActivationsWithLastOpenEnded()
    {
        var readouts = new[]
        {
            Readout("r1", Ticks(2026, 4, 1, 10), "A"),
            Readout("r2", Ticks(2026, 4, 2, 10), "B"),
            Readout("r3", Ticks(2026, 4, 3, 10), "A"),
        };

        var spans = MyLifePumpSettingsMapper.MapToProfileActivations(readouts, ConnectorSource)
            .OrderBy(s => s.StartTimestamp)
            .ToList();

        spans.Should().HaveCount(3);

        spans[0].StartTimestamp.Should().Be(new DateTime(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc));
        spans[0].EndTimestamp.Should().Be(new DateTime(2026, 4, 2, 10, 0, 0, DateTimeKind.Utc));
        spans[0].BaseProfileName.Should().Be("A");

        spans[1].StartTimestamp.Should().Be(new DateTime(2026, 4, 2, 10, 0, 0, DateTimeKind.Utc));
        spans[1].EndTimestamp.Should().Be(new DateTime(2026, 4, 3, 10, 0, 0, DateTimeKind.Utc));
        spans[1].BaseProfileName.Should().Be("B");

        spans[2].StartTimestamp.Should().Be(new DateTime(2026, 4, 3, 10, 0, 0, DateTimeKind.Utc));
        spans[2].EndTimestamp.Should().BeNull();
        spans[2].BaseProfileName.Should().Be("A");
    }

    [Fact]
    public void MapToProfileActivations_ReadoutsArrivingOutOfOrder_AreSortedChronologically()
    {
        var readouts = new[]
        {
            Readout("r3", Ticks(2026, 4, 3, 10), "C"),
            Readout("r1", Ticks(2026, 4, 1, 10), "A"),
            Readout("r2", Ticks(2026, 4, 2, 10), "B"),
        };

        var spans = MyLifePumpSettingsMapper.MapToProfileActivations(readouts, ConnectorSource)
            .OrderBy(s => s.StartTimestamp)
            .ToList();

        spans.Should().HaveCount(3);
        spans[0].EndTimestamp.Should().Be(spans[1].StartTimestamp);
        spans[1].EndTimestamp.Should().Be(spans[2].StartTimestamp);
        spans[2].EndTimestamp.Should().BeNull();
    }

    [Fact]
    public void MapToProfileActivations_NullOrEmptyActiveBasalProgram_SkipsActivation()
    {
        var readouts = new[]
        {
            Readout("r1", Ticks(2026, 4, 1, 10), "A"),
            Readout("r2", Ticks(2026, 4, 2, 10), null),
            Readout("r3", Ticks(2026, 4, 3, 10), ""),
        };

        var spans = MyLifePumpSettingsMapper.MapToProfileActivations(readouts, ConnectorSource);

        spans.Should().HaveCount(1);
        spans[0].BaseProfileName.Should().Be("A");
        spans[0].EndTimestamp.Should().Be(
            new DateTime(2026, 4, 2, 10, 0, 0, DateTimeKind.Utc),
            because: "the next readout's timestamp closes the prior span even when its program is null");
    }

    [Fact]
    public void MapToProfileActivations_MultipleDevices_EmitsIndependentActivationsPerDevice()
    {
        var readouts = new[]
        {
            Readout("r1", Ticks(2026, 4, 1, 10), "A", deviceSerialNumber: "SN-1"),
            Readout("r2", Ticks(2026, 4, 2, 10), "B", deviceSerialNumber: "SN-1"),
            Readout("r3", Ticks(2026, 4, 1, 10), "X", deviceSerialNumber: "SN-2"),
        };

        var spans = MyLifePumpSettingsMapper.MapToProfileActivations(readouts, ConnectorSource);

        spans.Should().HaveCount(3);

        var sn1 = spans.Where(s => s.SyncIdentifier!.Contains("SN-1"))
            .OrderBy(s => s.StartTimestamp).ToList();
        sn1.Should().HaveCount(2);
        sn1[0].EndTimestamp.Should().Be(sn1[1].StartTimestamp);
        sn1[1].EndTimestamp.Should().BeNull();

        var sn2 = spans.Where(s => s.SyncIdentifier!.Contains("SN-2")).ToList();
        sn2.Should().HaveCount(1);
        sn2[0].BaseProfileName.Should().Be("X");
        sn2[0].EndTimestamp.Should().BeNull();
    }

    [Fact]
    public void MapToProfileActivations_SyncIdentifiersAreStableAcrossRuns()
    {
        var readouts = new[]
        {
            Readout("r1", Ticks(2026, 4, 1, 10), "A"),
            Readout("r2", Ticks(2026, 4, 2, 10), "B"),
        };

        var first = MyLifePumpSettingsMapper.MapToProfileActivations(readouts, ConnectorSource)
            .Select(s => s.SyncIdentifier).OrderBy(id => id).ToList();
        var second = MyLifePumpSettingsMapper.MapToProfileActivations(readouts, ConnectorSource)
            .Select(s => s.SyncIdentifier).OrderBy(id => id).ToList();

        first.Should().Equal(second);
        first.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void MapToProfileActivations_EmptyList_ReturnsEmpty()
    {
        var spans = MyLifePumpSettingsMapper.MapToProfileActivations(Array.Empty<MyLifePumpSettingsReadout>(), ConnectorSource);

        spans.Should().BeEmpty();
    }
}
