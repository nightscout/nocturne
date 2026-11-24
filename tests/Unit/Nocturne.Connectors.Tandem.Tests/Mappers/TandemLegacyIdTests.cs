using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Connectors.Tandem.EventParser;
using Nocturne.Connectors.Tandem.Mappers;
using Nocturne.Connectors.Tandem.Models;
using Nocturne.Connectors.Tandem.Tests.EventParser;
using Xunit;

namespace Nocturne.Connectors.Tandem.Tests.Mappers;

/// <summary>
/// The stored legacy id of every Tandem record type, as exact strings.
/// Why the keys must not change: <see cref="Nocturne.Connectors.Core.Utilities.ConnectorLegacyIds"/>.
/// </summary>
public class TandemLegacyIdTests
{
    private const long Raw = 504921600L;
    private static readonly TandemTimeResolver Time = new(0);

    private static TandemPumpEvent Ev(string json) =>
        TandemEventDecoder.Decode(JsonSerializer.Deserialize<TandemPumpLogEvent>(json)!);

    [Fact]
    public void Cgm()
    {
        var events = TandemEventDecoder.Decode(TandemEventBuilder.ToBase64(
            new TandemEventBuilder(256, Raw, seqNum: 5).UInt16(4, 142).UInt32(8, (uint)Raw)));

        new TandemCgmMapper(NullLogger.Instance, Time).Map(events)
            .Single().LegacyId.Should().Be("tandem_cgm_5");
    }

    [Fact]
    public void BolusCarbAndCalculation()
    {
        var completed = new TandemEventBuilder(20, Raw, seqNum: 100)
            .UInt16(0, 42).UInt16(2, 3).Float32(4, 1.5f).Float32(8, 2.5f).Float32(12, 2.5f);
        var request = new TandemEventBuilder(64, Raw, seqNum: 99)
            .UInt16(2, 42).UInt16(4, 120).UInt16(6, 30).Float32(8, 1.5f).UInt32(12, 10000);

        var result = new TandemBolusMapper(NullLogger.Instance, Time)
            .Map(TandemEventDecoder.Decode(TandemEventBuilder.ToBase64(completed, request)));

        result.Boluses.Single().LegacyId.Should().Be("tandem_bolus_42");
        result.CarbIntakes.Single().LegacyId.Should().Be("tandem_carb_42");
        result.BolusCalculations.Single().LegacyId.Should().Be("tandem_boluscalc_42");
    }

    [Fact]
    public void Basal()
    {
        var events = TandemEventDecoder.Decode(TandemEventBuilder.ToBase64(
            new TandemEventBuilder(279, Raw, seqNum: 1).UInt16(6, 1000),
            new TandemEventBuilder(279, Raw + 300, seqNum: 2).UInt16(6, 500)));

        new TandemBasalMapper(NullLogger.Instance, Time)
            .Map(events, Time.ToUtc(Raw + 600), ignoreZeroUnitBasal: false).Spans
            .Select(s => s.LegacyId).Should().Equal("tandem_basal_1", "tandem_basal_2");
    }

    [Fact]
    public void DeviceEvent()
    {
        var suspend = Ev("""{"eventCode": 11, "sequenceGroup": 0, "sequenceNumber": 447946, "pumpDateTime": "2026-05-15T23:39:19", "eventProperties": {"preSuspendState": 106, "insulinAmount": 55, "suspendReason": 0, "rpaTimeout": 15}}""");

        new TandemDeviceEventMapper(NullLogger.Instance, Time).Map([suspend])
            .Single().LegacyId.Should().Be("tandem_devent_447946");
    }

    [Fact]
    public void SystemEvent()
    {
        var reset = Ev("""{"eventCode": 5, "sequenceGroup": 0, "sequenceNumber": 2353636, "pumpDateTime": "2024-02-26T22:44:48", "eventProperties": {"alarmId": 3, "faultLocatorData": 8230, "param1": 0, "param2": 0}}""");

        new TandemSystemEventMapper(NullLogger.Instance, Time).Map([reset])
            .Single().OriginalId.Should().Be("tandem_sysevent_2353636");
    }

    [Fact]
    public void UserMode()
    {
        var start = Ev("""{"eventCode": 229, "sequenceGroup": 0, "sequenceNumber": 456961, "pumpDateTime": "2026-05-18T10:20:04", "eventProperties": {"currentUserMode": 2, "previousUserMode": 0, "requestedAction": 3, "spareA3": 0, "sleepStartedByGui": 0, "activeSleepSchedule": [], "spareB6": 0, "exerciseStoppedByTimer": 0, "exerciseChoice": 0, "exerciseTime": 0, "eatingSoonStoppedByTimer": 0}}""");
        var stop = Ev("""{"eventCode": 229, "sequenceGroup": 0, "sequenceNumber": 456965, "pumpDateTime": "2026-05-18T10:20:15", "eventProperties": {"currentUserMode": 1, "previousUserMode": 2, "requestedAction": 4, "spareA3": 0, "sleepStartedByGui": 0, "activeSleepSchedule": [0], "spareB6": 0, "exerciseStoppedByTimer": 0, "exerciseChoice": 0, "exerciseTime": 0, "eatingSoonStoppedByTimer": 0}}""");
        var mapper = new TandemUserModeMapper(NullLogger.Instance, Time);

        mapper.Map([start, stop]).Single().OriginalId.Should().Be("tandem_usermode_456961_456965");
        mapper.Map([start]).Single().OriginalId.Should().Be("tandem_usermode_456961");
    }
}
