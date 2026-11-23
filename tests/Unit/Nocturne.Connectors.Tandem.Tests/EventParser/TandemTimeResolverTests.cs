using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Connectors.Tandem.EventParser;
using Nocturne.Connectors.Tandem.Mappers;
using Xunit;

namespace Nocturne.Connectors.Tandem.Tests.EventParser;

public class TandemTimeResolverTests
{
    private const long RawTimestamp2024 = 504921600L; // 2024-01-01T00:00:00 wall clock

    [Fact]
    public void ToUtc_treats_raw_as_utc_when_offset_zero()
    {
        var resolver = new TandemTimeResolver(0);

        resolver.ToUtc(RawTimestamp2024).Should().Be(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        resolver.OffsetMinutesAt(resolver.ToUtc(RawTimestamp2024)).Should().Be(0);
    }

    [Fact]
    public void ToUtc_subtracts_positive_offset_to_recover_utc()
    {
        // A user at UTC+10 whose pump reads 00:00 local is actually at 14:00 UTC the previous day.
        var resolver = new TandemTimeResolver(10);

        resolver.ToUtc(RawTimestamp2024).Should().Be(new DateTime(2023, 12, 31, 14, 0, 0, DateTimeKind.Utc));
        resolver.OffsetMinutesAt(resolver.ToUtc(RawTimestamp2024)).Should().Be(600);
    }

    [Fact]
    public void ToUtc_adds_for_negative_offset()
    {
        var resolver = new TandemTimeResolver(-5);

        resolver.ToUtc(RawTimestamp2024).Should().Be(new DateTime(2024, 1, 1, 5, 0, 0, DateTimeKind.Utc));
        resolver.OffsetMinutesAt(resolver.ToUtc(RawTimestamp2024)).Should().Be(-300);
    }

    private const string LosAngeles = "America/Los_Angeles";

    private static long RawAt(int year, int month, int day, int hour, int minute) =>
        (long)(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc)
               - new DateTime(2008, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

    private static DateTime Utc(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, 30, 7, 30, -420)]  // before the change: PDT
    [InlineData(1, 30, 8, 30, -420)]  // occurs twice: the first occurrence, PDT
    [InlineData(2, 30, 10, 30, -480)] // after the change: PST
    public void Zone_reads_each_time_with_its_own_offset_when_clocks_go_back(
        int wallHour, int wallMinute, int utcHour, int utcMinute, int offsetMinutes)
    {
        var resolver = new TandemTimeResolver(-7, LosAngeles);

        var utc = resolver.ToUtc(RawAt(2026, 11, 1, wallHour, wallMinute));

        utc.Should().Be(Utc(2026, 11, 1, utcHour, utcMinute));
        resolver.OffsetMinutesAt(utc).Should().Be(offsetMinutes);
    }

    [Theory]
    [InlineData(1, 30, 9, 30, -480)]  // before the change: PST
    [InlineData(2, 30, 10, 0, -420)]  // skipped: the first instant after the gap, 03:00 PDT
    [InlineData(3, 30, 10, 30, -420)] // after the change: PDT
    public void Zone_reads_each_time_with_its_own_offset_when_clocks_go_forward(
        int wallHour, int wallMinute, int utcHour, int utcMinute, int offsetMinutes)
    {
        var resolver = new TandemTimeResolver(-8, LosAngeles);

        var utc = resolver.ToUtc(RawAt(2027, 3, 14, wallHour, wallMinute));

        utc.Should().Be(Utc(2027, 3, 14, utcHour, utcMinute));
        resolver.OffsetMinutesAt(utc).Should().Be(offsetMinutes);
    }

    [Fact]
    public void Zone_converts_a_naive_wall_clock_value()
    {
        var resolver = new TandemTimeResolver(0, LosAngeles);

        resolver.ToUtc(new DateTime(2026, 11, 1, 2, 30, 0, DateTimeKind.Utc))
            .Should().Be(Utc(2026, 11, 1, 10, 30));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Not/A_Zone")]
    public void Fixed_offset_applies_when_no_zone_resolves(string? zoneId)
    {
        var resolver = new TandemTimeResolver(-7, zoneId);

        resolver.UsesTimeZone.Should().BeFalse();
        var before = resolver.ToUtc(RawAt(2026, 11, 1, 0, 30));
        var after = resolver.ToUtc(RawAt(2026, 11, 1, 2, 30));
        before.Should().Be(Utc(2026, 11, 1, 7, 30));
        after.Should().Be(Utc(2026, 11, 1, 9, 30));
        resolver.OffsetMinutesAt(before).Should().Be(-420);
        resolver.OffsetMinutesAt(after).Should().Be(-420);
    }

    [Fact]
    public void Mapped_records_carry_the_offset_in_effect_at_their_own_time()
    {
        var before = RawAt(2026, 10, 31, 23, 0);
        var after = RawAt(2026, 11, 1, 3, 0);
        var blob = TandemEventBuilder.ToBase64(
            new TandemEventBuilder(256, before, seqNum: 1).UInt16(4, 120).UInt32(8, (uint)before),
            new TandemEventBuilder(256, after, seqNum: 2).UInt16(4, 130).UInt32(8, (uint)after));

        var glucose = new TandemCgmMapper(NullLogger.Instance, new TandemTimeResolver(-7, LosAngeles))
            .Map(TandemEventDecoder.Decode(blob));

        glucose.Select(g => (g.Timestamp, g.UtcOffset)).Should().BeEquivalentTo(new[]
        {
            (Utc(2026, 11, 1, 6, 0), (int?)-420),
            (Utc(2026, 11, 1, 11, 0), (int?)-480),
        });
    }
}
