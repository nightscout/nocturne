using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.Connectors.Glooko.Mappers;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Mappers;

public class GlookoProfileMapperActivationTests
{
    private const string ConnectorSource = "glooko-connector";
    private readonly GlookoProfileMapper _mapper;

    public GlookoProfileMapperActivationTests()
    {
        _mapper = new GlookoProfileMapper(ConnectorSource, Mock.Of<ILogger>());
    }

    private static GlookoV3DeviceSettingsResponse Build(
        params (string DeviceGuid, (string Timestamp, string? ActiveBasalProgram)[] Snapshots)[] devices)
    {
        var pumps = new Dictionary<string, Dictionary<string, GlookoV3PumpSettings>>();
        foreach (var (deviceGuid, snapshots) in devices)
        {
            var byTs = new Dictionary<string, GlookoV3PumpSettings>();
            foreach (var (ts, program) in snapshots)
            {
                byTs[ts] = new GlookoV3PumpSettings
                {
                    SyncTimestamp = ts,
                    BasalSettings = new GlookoV3BasalSettings
                    {
                        ActiveBasalProgram = program
                    }
                };
            }
            pumps[deviceGuid] = byTs;
        }

        return new GlookoV3DeviceSettingsResponse
        {
            DeviceSettings = new GlookoV3DeviceSettings { Pumps = pumps }
        };
    }

    [Fact]
    public void TransformDeviceSettingsToProfileActivations_SingleSnapshotWithProgram_EmitsOpenEndedActivation()
    {
        var response = Build(
            ("device-1", [("2026-04-01T10:00:00Z", "A")])
        );

        var spans = _mapper.TransformDeviceSettingsToProfileActivations(response);

        spans.Should().HaveCount(1);
        var span = spans[0];
        span.Phase.Should().Be(ProfileActivationPhase.Effective);
        span.Origin.Should().Be(ProfileActivationOrigin.Connector);
        span.DataSource.Should().Be(ConnectorSource);
        span.StartTimestamp.Should().Be(new DateTime(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc));
        span.EndTimestamp.Should().BeNull();
        span.BaseProfileName.Should().Be("A");
        span.SyncIdentifier.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void TransformDeviceSettingsToProfileActivations_MultipleSnapshots_EmitsAbuttingActivationsWithLastOpenEnded()
    {
        var response = Build(
            ("device-1",
            [
                ("2026-04-01T10:00:00Z", "A"),
                ("2026-04-02T10:00:00Z", "B"),
                ("2026-04-03T10:00:00Z", "A"),
            ])
        );

        var spans = _mapper.TransformDeviceSettingsToProfileActivations(response)
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
    public void TransformDeviceSettingsToProfileActivations_SnapshotsArrivingOutOfOrder_AreSortedChronologically()
    {
        var response = Build(
            ("device-1",
            [
                ("2026-04-03T10:00:00Z", "C"),
                ("2026-04-01T10:00:00Z", "A"),
                ("2026-04-02T10:00:00Z", "B"),
            ])
        );

        var spans = _mapper.TransformDeviceSettingsToProfileActivations(response)
            .OrderBy(s => s.StartTimestamp)
            .ToList();

        spans.Should().HaveCount(3);
        spans[0].EndTimestamp.Should().Be(spans[1].StartTimestamp);
        spans[1].EndTimestamp.Should().Be(spans[2].StartTimestamp);
        spans[2].EndTimestamp.Should().BeNull();
    }

    [Fact]
    public void TransformDeviceSettingsToProfileActivations_NullOrEmptyActiveBasalProgram_SkipsActivation()
    {
        var response = Build(
            ("device-1",
            [
                ("2026-04-01T10:00:00Z", "A"),
                ("2026-04-02T10:00:00Z", null),
                ("2026-04-03T10:00:00Z", ""),
            ])
        );

        var spans = _mapper.TransformDeviceSettingsToProfileActivations(response);

        spans.Should().HaveCount(1);
        spans[0].BaseProfileName.Should().Be("A");
        spans[0].EndTimestamp.Should().Be(new DateTime(2026, 4, 2, 10, 0, 0, DateTimeKind.Utc),
            because: "the next snapshot's timestamp ends the prior activation even when its program is null");
    }

    [Fact]
    public void TransformDeviceSettingsToProfileActivations_MultipleDevices_EmitsIndependentActivationsPerDevice()
    {
        var response = Build(
            ("device-1",
            [
                ("2026-04-01T10:00:00Z", "A"),
                ("2026-04-02T10:00:00Z", "B"),
            ]),
            ("device-2",
            [
                ("2026-04-01T10:00:00Z", "X"),
            ])
        );

        var spans = _mapper.TransformDeviceSettingsToProfileActivations(response);

        spans.Should().HaveCount(3);

        var device1Spans = spans.Where(s => s.SyncIdentifier!.Contains("device-1")).ToList();
        device1Spans.Should().HaveCount(2);
        device1Spans.Select(s => s.BaseProfileName).Should().BeEquivalentTo("A", "B");

        var device2Spans = spans.Where(s => s.SyncIdentifier!.Contains("device-2")).ToList();
        device2Spans.Should().HaveCount(1);
        device2Spans[0].BaseProfileName.Should().Be("X");
        device2Spans[0].EndTimestamp.Should().BeNull();
    }

    [Fact]
    public void TransformDeviceSettingsToProfileActivations_SyncIdentifiersAreStableAcrossRuns()
    {
        var response = Build(
            ("device-1",
            [
                ("2026-04-01T10:00:00Z", "A"),
                ("2026-04-02T10:00:00Z", "B"),
            ])
        );

        var first = _mapper.TransformDeviceSettingsToProfileActivations(response)
            .Select(s => s.SyncIdentifier).OrderBy(id => id).ToList();
        var second = _mapper.TransformDeviceSettingsToProfileActivations(response)
            .Select(s => s.SyncIdentifier).OrderBy(id => id).ToList();

        first.Should().Equal(second);
        first.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void TransformDeviceSettingsToProfileActivations_NullPumps_ReturnsEmpty()
    {
        var response = new GlookoV3DeviceSettingsResponse { DeviceSettings = null };

        var spans = _mapper.TransformDeviceSettingsToProfileActivations(response);

        spans.Should().BeEmpty();
    }
}
