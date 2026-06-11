using FluentAssertions;
using Nocturne.Connectors.Tandem.EventParser;
using Xunit;

namespace Nocturne.Connectors.Tandem.Tests.EventParser;

public class TandemEventDecoderTests
{
    // 504921600 seconds after the Tandem epoch (2008-01-01) == 2024-01-01T00:00:00Z.
    private const long RawTimestamp2024 = 504921600L;

    [Fact]
    public void Catalog_includes_core_event_definitions()
    {
        var defs = TandemEventCatalog.Definitions;

        defs.Should().ContainKey(20).WhoseValue.Name.Should().Be("LID_BOLUS_COMPLETED");
        defs.Should().ContainKey(256).WhoseValue.Name.Should().Be("LID_CGM_DATA_GXB");
        defs.Should().ContainKey(279).WhoseValue.Name.Should().Be("LID_BASAL_DELIVERY");
        // Merged from the custom-events resource.
        defs.Should().ContainKey(81).WhoseValue.Name.Should().Be("LID_DAILY_BASAL");
    }

    [Fact]
    public void Decode_reads_header_fields()
    {
        var blob = TandemEventBuilder.ToBase64(
            new TandemEventBuilder(20, RawTimestamp2024, seqNum: 1234).UInt16(0, 42).UInt16(2, 3));

        var events = TandemEventDecoder.Decode(blob);

        events.Should().HaveCount(1);
        var ev = events[0];
        ev.Id.Should().Be(20);
        ev.Name.Should().Be("LID_BOLUS_COMPLETED");
        ev.SeqNum.Should().Be(1234u);
        ev.RawTimestampSeconds.Should().Be(RawTimestamp2024);
        ev.IsKnown.Should().BeTrue();
    }

    [Fact]
    public void Decode_applies_enum_transform_and_reads_floats()
    {
        var blob = TandemEventBuilder.ToBase64(
            new TandemEventBuilder(20, RawTimestamp2024, seqNum: 1)
                .UInt16(0, 42)        // BolusID
                .UInt16(2, 3)         // CompletionStatus -> "Completed"
                .Float32(4, 1.5f)     // IOB
                .Float32(8, 2.5f)     // InsulinDelivered
                .Float32(12, 2.25f)); // InsulinRequested

        var ev = TandemEventDecoder.Decode(blob).Single();

        ev.Raw("BolusID").Should().Be(42);
        ev.EnumName("CompletionStatus").Should().Be("Completed");
        ev.Num("IOB").Should().BeApproximately(1.5, 1e-4);
        ev.Num("InsulinDelivered").Should().BeApproximately(2.5, 1e-4);
        ev.Num("InsulinRequested").Should().BeApproximately(2.25, 1e-4);
    }

    [Fact]
    public void Decode_applies_ratio_transform_on_signed_field()
    {
        // LID_CGM_DATA_GXB: Rate is int8 with a 0.1 ratio (mg/dL/min).
        var blob = TandemEventBuilder.ToBase64(
            new TandemEventBuilder(256, RawTimestamp2024, seqNum: 7)
                .Int8(0, 5)                      // Rate raw -> 0.5
                .UInt16(4, 120)                  // currentGlucoseDisplayValue
                .UInt32(8, (uint)RawTimestamp2024)); // EGV TimeStamp

        var ev = TandemEventDecoder.Decode(blob).Single();

        ev.Num("Rate").Should().BeApproximately(0.5, 1e-4);
        ev.Num("currentGlucoseDisplayValue").Should().Be(120);
        ev.Raw("EGV TimeStamp").Should().Be(RawTimestamp2024);
    }

    [Fact]
    public void Decode_marks_unknown_event_ids()
    {
        // Event id 4095 is not in the schema.
        var blob = TandemEventBuilder.ToBase64(new TandemEventBuilder(4095, RawTimestamp2024, seqNum: 9));

        var ev = TandemEventDecoder.Decode(blob).Single();

        ev.IsKnown.Should().BeFalse();
        ev.Name.Should().Be("RawEvent");
        ev.Fields.Should().BeEmpty();
    }

    [Fact]
    public void Decode_returns_empty_for_blank_input()
    {
        TandemEventDecoder.Decode(null).Should().BeEmpty();
        TandemEventDecoder.Decode("").Should().BeEmpty();
    }
}
