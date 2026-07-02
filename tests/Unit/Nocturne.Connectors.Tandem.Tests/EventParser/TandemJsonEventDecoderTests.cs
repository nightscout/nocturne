using System.Text.Json;
using FluentAssertions;
using Nocturne.Connectors.Tandem.EventParser;
using Nocturne.Connectors.Tandem.Models;
using Xunit;

namespace Nocturne.Connectors.Tandem.Tests.EventParser;

/// <summary>
/// Tests for the pump-logs JSON decode path added for the Tandem Source BFF API (tconnectsync v3):
/// normalized property-name matching, raw-value transforms, and bitmask-as-array conversion.
/// </summary>
public class TandemJsonEventDecoderTests
{
    private static TandemPumpLogEvent LogEvent(int eventCode, string json, uint seqNum = 1, long seqGroup = 0) =>
        new()
        {
            EventCode = eventCode,
            SequenceGroup = seqGroup,
            SequenceNumber = seqNum,
            PumpDateTime = "2024-01-01T00:00:00",
            EventProperties = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!,
        };

    [Fact]
    public void Decode_reads_header_and_wall_clock_timestamp()
    {
        var ev = TandemEventDecoder.Decode(LogEvent(20, """{"bolusId": 42}""", seqNum: 1234, seqGroup: 7));

        ev.Id.Should().Be(20);
        ev.Name.Should().Be("LID_BOLUS_COMPLETED");
        ev.SeqNum.Should().Be(1234u);
        ev.SequenceGroup.Should().Be(7);
        // pumpDateTime is pump-local wall-clock parsed as if UTC, matching the binary header:
        // 2024-01-01T00:00:00 is 504921600 seconds after the Tandem epoch (2008-01-01).
        ev.RawTimestampSeconds.Should().Be(504921600L);
        ev.IsKnown.Should().BeTrue();

        // Round-trip through the resolver matches the binary path's semantics.
        new TandemTimeResolver(0).ToUtc(ev.RawTimestampSeconds)
            .Should().Be(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Decode_matches_properties_by_normalized_name()
    {
        // Schema field names for 256 include "CGM Data Type", "EGV TimeStamp" and
        // "currentGlucoseDisplayValue"; the BFF sends camelCase keys.
        var ev = TandemEventDecoder.Decode(LogEvent(256, """
            {"currentGlucoseDisplayValue": 123, "egvTimestamp": 504921600, "algorithmState": 100}
            """));

        ev.Num("currentGlucoseDisplayValue").Should().Be(123);
        ev.Num("EGV TimeStamp").Should().Be(504921600);
        ev.Num("AlgorithmState").Should().Be(100);
    }

    [Fact]
    public void Decode_applies_ratio_transform_to_raw_value()
    {
        // "Rate" on LID_CGM_DATA_GXB carries a ratio 0.1 transform; the BFF sends the raw value.
        var ev = TandemEventDecoder.Decode(LogEvent(256, """{"rate": 15}"""));

        ev.Num("Rate").Should().BeApproximately(1.5, 1e-6);
        ev.Raw("Rate").Should().Be(15);
    }

    [Fact]
    public void Decode_resolves_enum_names_from_raw_value()
    {
        var ev = TandemEventDecoder.Decode(LogEvent(20, """{"completionStatus": 3, "bolusId": 42}"""));

        ev.EnumName("CompletionStatus").Should().Be("Completed");
        ev.Raw("BolusID").Should().Be(42);
    }

    [Fact]
    public void Decode_converts_bitmask_arrays_of_set_bit_indices()
    {
        // "ChangeType" on LID_BASAL_RATE_CHANGE is a bitmask; the BFF sends set-bit indices.
        var ev = TandemEventDecoder.Decode(LogEvent(3, """{"changeType": [2, 3], "commandedBasalRate": 1.2}"""));

        ev.Raw("ChangeType").Should().Be((1 << 2) | (1 << 3));
        ev.Bits("ChangeType").Should().BeEquivalentTo("\"temp rate start\"", "\"temp rate end\"");
        ev.Num("CommandedBasalRate").Should().BeApproximately(1.2, 1e-6);
    }

    [Fact]
    public void Decode_tolerates_plain_integer_bitmask_values()
    {
        var ev = TandemEventDecoder.Decode(LogEvent(3, """{"changeType": 4}"""));

        ev.Raw("ChangeType").Should().Be(4);
        ev.Bits("ChangeType").Should().BeEquivalentTo("\"temp rate start\"");
    }

    [Fact]
    public void Decode_returns_unknown_events_without_fields()
    {
        var ev = TandemEventDecoder.Decode(LogEvent(9999, """{"someProp": 1}"""));

        ev.IsKnown.Should().BeFalse();
        ev.Name.Should().Be("RawEvent");
        ev.Fields.Should().BeEmpty();
    }

    [Fact]
    public void Decode_skips_missing_and_non_numeric_properties()
    {
        var ev = TandemEventDecoder.Decode(LogEvent(256, """{"currentGlucoseDisplayValue": null}"""));

        ev.IsKnown.Should().BeTrue();
        ev.Has("currentGlucoseDisplayValue").Should().BeFalse();
        ev.Has("Rate").Should().BeFalse();
        ev.Num("Rate").Should().BeNull();
    }
}
