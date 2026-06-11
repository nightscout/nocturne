using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Connectors.Tandem.EventParser;
using Nocturne.Connectors.Tandem.Mappers;
using Nocturne.Connectors.Tandem.Tests.EventParser;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Connectors.Tandem.Tests.Mappers;

public class TandemMapperTests
{
    private const long Raw = 504921600L; // 2024-01-01T00:00:00Z
    private static readonly TandemTimeResolver Time = new(0);

    [Fact]
    public void CgmMapper_maps_reading_using_egv_timestamp()
    {
        var blob = TandemEventBuilder.ToBase64(
            new TandemEventBuilder(256, Raw + 600, seqNum: 5) // store time differs from EGV time
                .UInt16(4, 142)
                .UInt32(8, (uint)Raw));
        var events = TandemEventDecoder.Decode(blob);

        var glucose = new TandemCgmMapper(NullLogger.Instance, Time).Map(events);

        glucose.Should().HaveCount(1);
        glucose[0].Mgdl.Should().Be(142);
        glucose[0].Timestamp.Should().Be(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        glucose[0].DataSource.Should().Be(TandemMapHelpers.Source);
        glucose[0].SyncIdentifier.Should().Be("tandem_cgm_5");
    }

    [Fact]
    public void CgmMapper_skips_zero_display_values()
    {
        var blob = TandemEventBuilder.ToBase64(
            new TandemEventBuilder(256, Raw, seqNum: 6).UInt16(4, 0).UInt32(8, (uint)Raw));

        new TandemCgmMapper(NullLogger.Instance, Time).Map(TandemEventDecoder.Decode(blob))
            .Should().BeEmpty();
    }

    [Fact]
    public void BolusMapper_reassembles_bolus_with_carbs_and_calculation()
    {
        var completed = new TandemEventBuilder(20, Raw, seqNum: 100)
            .UInt16(0, 42)     // BolusID
            .UInt16(2, 3)      // CompletionStatus -> Completed
            .Float32(4, 1.5f)  // IOB
            .Float32(8, 2.5f)  // InsulinDelivered
            .Float32(12, 2.5f); // InsulinRequested
        var msg1 = new TandemEventBuilder(64, Raw, seqNum: 99)
            .UInt16(2, 42)     // BolusID
            .UInt16(4, 120)    // BG
            .UInt16(6, 30)     // CarbAmount
            .Float32(8, 1.5f)  // IOB
            .UInt32(12, 10000); // CarbRatio raw -> 10 g/u

        var events = TandemEventDecoder.Decode(TandemEventBuilder.ToBase64(completed, msg1));

        var result = new TandemBolusMapper(NullLogger.Instance, Time).Map(events);

        result.Boluses.Should().ContainSingle();
        var bolus = result.Boluses[0];
        bolus.Insulin.Should().Be(2.5);
        bolus.BolusType.Should().Be(BolusType.Normal);
        bolus.SyncIdentifier.Should().Be("tandem_bolus_42");

        result.CarbIntakes.Should().ContainSingle();
        result.CarbIntakes[0].Carbs.Should().Be(30);
        result.CarbIntakes[0].CorrelationId.Should().Be(bolus.CorrelationId);

        result.BolusCalculations.Should().ContainSingle();
        var calc = result.BolusCalculations[0];
        calc.CarbInput.Should().Be(30);
        calc.BloodGlucoseInput.Should().Be(120);
        calc.InsulinOnBoard.Should().BeApproximately(1.5, 1e-4);
        calc.CarbRatio.Should().BeApproximately(10, 1e-4);
    }

    [Fact]
    public void BasalMapper_spans_each_delivery_to_the_next()
    {
        var first = new TandemEventBuilder(279, Raw, seqNum: 1).UInt16(6, 1000); // 1000 mU/hr -> 1.0 U/hr
        var second = new TandemEventBuilder(279, Raw + 300, seqNum: 2).UInt16(6, 500);
        var events = TandemEventDecoder.Decode(TandemEventBuilder.ToBase64(first, second));
        var windowEnd = Time.ToUtc(Raw + 600);

        var basals = new TandemBasalMapper(NullLogger.Instance, Time)
            .Map(events, windowEnd, ignoreZeroUnitBasal: false);

        basals.Should().HaveCount(2);
        basals[0].Rate.Should().Be(1.0);
        basals[0].EndTimestamp.Should().Be(Time.ToUtc(Raw + 300));
        basals[1].Rate.Should().Be(0.5);
        basals[1].EndTimestamp.Should().Be(windowEnd);
    }
}
