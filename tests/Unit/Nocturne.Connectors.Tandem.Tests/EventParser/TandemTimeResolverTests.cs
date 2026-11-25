using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Connectors.Tandem.EventParser;
using Nocturne.Connectors.Tandem.Mappers;
using Nocturne.Core.Models.Connectors;
using Nocturne.Core.Models.Timezones;
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

    [Theory]
    [InlineData(0, 30, 7, 30, -420)]
    [InlineData(2, 30, 10, 30, -480)]
    public void The_tenants_zone_reads_each_time_with_its_own_offset_when_no_setting_names_a_zone(
        int wallHour, int wallMinute, int utcHour, int utcMinute, int offsetMinutes)
    {
        var resolver = new TandemTimeResolver(0, null, LosAngeles);

        var utc = resolver.ToUtc(RawAt(2026, 11, 1, wallHour, wallMinute));

        utc.Should().Be(Utc(2026, 11, 1, utcHour, utcMinute));
        resolver.OffsetMinutesAt(utc).Should().Be(offsetMinutes);
        resolver.ResolvedZone.Should().BeEquivalentTo(
            new ConnectorTimeZone { Id = LosAngeles, Source = ConnectorTimeZoneSource.TenantZone });
    }

    [Fact]
    public void An_offset_that_is_set_wins_over_the_tenants_zone()
    {
        var resolver = new TandemTimeResolver(10, "  ", LosAngeles);

        resolver.ToUtc(RawTimestamp2024).Should().Be(new DateTime(2023, 12, 31, 14, 0, 0, DateTimeKind.Utc));
        resolver.ResolvedZone.Should().BeEquivalentTo(
            new ConnectorTimeZone { Id = "UTC+10:00", Source = ConnectorTimeZoneSource.ConnectorOffset });
    }

    [Fact]
    public void A_half_hour_negative_offset_is_named_with_its_sign()
    {
        new TandemTimeResolver(-3.5).ResolvedZone.Id.Should().Be("UTC-03:30");
    }

    [Fact]
    public void The_connectors_zone_wins_over_the_offset_and_the_tenants_zone()
    {
        var resolver = new TandemTimeResolver(10, "Europe/London", LosAngeles);

        resolver.ToUtc(RawAt(2026, 7, 1, 12, 0)).Should().Be(Utc(2026, 7, 1, 11, 0));
        resolver.ResolvedZone.Should().BeEquivalentTo(
            new ConnectorTimeZone { Id = "Europe/London", Source = ConnectorTimeZoneSource.ConnectorZone });
    }

    [Fact]
    public void A_zone_of_utc_is_a_deliberate_utc_that_the_tenants_zone_does_not_override()
    {
        var resolver = new TandemTimeResolver(0, "UTC", LosAngeles);

        resolver.ToUtc(RawTimestamp2024).Should().Be(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        resolver.ResolvedZone.Source.Should().Be(ConnectorTimeZoneSource.ConnectorZone);
    }

    [Fact]
    public void A_zone_that_does_not_resolve_falls_back_to_the_offset_before_the_tenants_zone()
    {
        var resolver = new TandemTimeResolver(-7, "Not/A_Zone", "Australia/Sydney");

        resolver.UsesTimeZone.Should().BeFalse();
        resolver.ToUtc(RawAt(2026, 11, 1, 0, 30)).Should().Be(Utc(2026, 11, 1, 7, 30));
        resolver.ResolvedZone.Source.Should().Be(ConnectorTimeZoneSource.ConnectorOffset);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Not/A_Zone")]
    public void With_no_setting_and_no_usable_tenant_zone_the_pump_is_read_as_utc(string? tenantZoneId)
    {
        var resolver = new TandemTimeResolver(0, null, tenantZoneId);

        resolver.UsesTimeZone.Should().BeFalse();
        resolver.ToUtc(RawTimestamp2024).Should().Be(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        resolver.ResolvedZone.Should().BeEquivalentTo(
            new ConnectorTimeZone { Id = "UTC", Source = ConnectorTimeZoneSource.Utc });
    }

    [Theory]
    [InlineData(0, null, true)]
    [InlineData(0, "", true)]
    [InlineData(0, "Not/A_Zone", true)]
    [InlineData(0.0001, null, true)]
    [InlineData(-7, null, false)]
    [InlineData(0, "America/Los_Angeles", false)]
    [InlineData(5.5, "Not/A_Zone", false)]
    public void The_tenants_zone_is_needed_only_when_no_setting_names_a_zone(
        double offset, string? zoneId, bool needed)
    {
        TandemTimeResolver.NeedsTenantZone(offset, zoneId).Should().Be(needed);
    }

    private static TimezoneTimeline MoveFromNewYorkToLosAngeles() => new(
    [
        new TimezoneTimelineEntry { EffectiveFrom = DateTime.MinValue, Timezone = "America/New_York" },
        new TimezoneTimelineEntry { EffectiveFrom = new DateTime(2026, 6, 1), Timezone = LosAngeles },
    ]);

    [Theory]
    [InlineData(5, 12, 16, -240)] // before the move: New York, EDT
    [InlineData(6, 2, 19, -420)]  // after the move: Los Angeles, PDT
    public void The_tenants_zone_history_reads_each_time_in_the_zone_in_effect_then(
        int month, int day, int utcHour, int offsetMinutes)
    {
        var resolver = new TandemTimeResolver(
            0, null, "Asia/Tokyo", MoveFromNewYorkToLosAngeles(), Utc(2026, 11, 24, 0, 0));

        var utc = resolver.ToUtc(RawAt(2026, month, day, 12, 0));

        utc.Should().Be(Utc(2026, month, day, utcHour, 0));
        resolver.OffsetMinutesAt(utc).Should().Be(offsetMinutes);
        resolver.UsesTimeZone.Should().BeTrue();
    }

    [Fact]
    public void The_tenants_zone_history_names_the_zone_in_effect_at_the_end_of_the_run()
    {
        var resolver = new TandemTimeResolver(
            0, null, null, MoveFromNewYorkToLosAngeles(), Utc(2026, 11, 24, 0, 0));

        resolver.ResolvedZone.Should().BeEquivalentTo(
            new ConnectorTimeZone { Id = LosAngeles, Source = ConnectorTimeZoneSource.TenantTimeline });
    }

    private static TimezoneTimeline HistoryStartingInMarch(string zone) => new(
    [
        new TimezoneTimelineEntry { EffectiveFrom = new DateTime(2026, 3, 1), Timezone = zone },
    ]);

    [Theory]
    [InlineData(1, 15, 17, -300)] // before the history: the tenant's zone, EST
    [InlineData(4, 15, 19, -420)] // after it starts: the history's zone, PDT
    public void A_time_before_the_zone_history_is_read_in_the_tenants_zone(
        int month, int day, int utcHour, int offsetMinutes)
    {
        var resolver = new TandemTimeResolver(
            0, null, "America/New_York", HistoryStartingInMarch(LosAngeles), Utc(2026, 11, 24, 0, 0));

        var utc = resolver.ToUtc(RawAt(2026, month, day, 12, 0));

        utc.Should().Be(Utc(2026, month, day, utcHour, 0));
        resolver.OffsetMinutesAt(utc).Should().Be(offsetMinutes);
        resolver.ReadsAsUtc.Should().BeFalse();
        resolver.ResolvedZone.Source.Should().Be(ConnectorTimeZoneSource.TenantTimeline);
    }

    [Fact]
    public void A_time_before_the_zone_history_is_read_as_utc_only_when_no_tenant_zone_covers_it()
    {
        var resolver = new TandemTimeResolver(
            0, null, null, HistoryStartingInMarch(LosAngeles), Utc(2026, 11, 24, 0, 0));

        resolver.ToUtc(RawAt(2026, 1, 15, 12, 0)).Should().Be(Utc(2026, 1, 15, 12, 0));
        resolver.OffsetMinutesAt(Utc(2026, 1, 15, 12, 0)).Should().Be(0);
        resolver.ReadsAsUtc.Should().BeTrue();
    }

    [Fact]
    public void A_zone_history_with_an_origin_entry_leaves_no_time_to_read_as_utc()
    {
        var origin = new TimezoneTimeline(
            [new TimezoneTimelineEntry { EffectiveFrom = DateTime.MinValue, Timezone = "America/New_York" }]);
        var resolver = new TandemTimeResolver(0, null, null, origin, Utc(2026, 11, 24, 0, 0));

        resolver.ToUtc(RawAt(2026, 1, 15, 12, 0)).Should().Be(Utc(2026, 1, 15, 17, 0));
        resolver.ReadsAsUtc.Should().BeFalse();
    }

    [Fact]
    public void An_empty_zone_history_leaves_the_tenants_zone_to_decide()
    {
        var resolver = new TandemTimeResolver(0, null, LosAngeles, new TimezoneTimeline([]), Utc(2026, 11, 24, 0, 0));

        resolver.ResolvedZone.Source.Should().Be(ConnectorTimeZoneSource.TenantZone);
        resolver.ToUtc(RawAt(2026, 11, 1, 2, 30)).Should().Be(Utc(2026, 11, 1, 10, 30));
    }

    [Fact]
    public void A_setting_on_the_connector_wins_over_the_tenants_zone_history()
    {
        var offset = new TandemTimeResolver(
            10, null, null, MoveFromNewYorkToLosAngeles(), Utc(2026, 11, 24, 0, 0));
        var zone = new TandemTimeResolver(
            0, "Europe/London", null, MoveFromNewYorkToLosAngeles(), Utc(2026, 11, 24, 0, 0));

        offset.ResolvedZone.Source.Should().Be(ConnectorTimeZoneSource.ConnectorOffset);
        zone.ResolvedZone.Source.Should().Be(ConnectorTimeZoneSource.ConnectorZone);
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
