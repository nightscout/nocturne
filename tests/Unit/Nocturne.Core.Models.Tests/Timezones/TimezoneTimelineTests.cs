using FluentAssertions;
using Nocturne.Core.Models.Timezones;
using Xunit;

namespace Nocturne.Core.Models.Tests.Timezones;

[Trait("Category", "Unit")]
public class TimezoneTimelineTests
{
    private static TimezoneTimelineEntry Entry(string zone, DateTime effectiveFromLocal) =>
        new() { Id = Guid.NewGuid(), Timezone = zone, EffectiveFrom = DateTime.SpecifyKind(effectiveFromLocal, DateTimeKind.Unspecified) };

    private static TimezoneTimelineEntry Origin(string zone) => Entry(zone, DateTime.MinValue);

    private static DateTime FakeUtc(int y, int mo, int d, int h, int mi = 0) =>
        new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    private static DateTime Utc(int y, int mo, int d, int h, int mi = 0) =>
        new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    // ── Daylight saving handled per-date (the core fix) ──────────────────────

    [Fact]
    public void Sydney_SameWallClock_ConvertsWithDstOffsetForTheDate()
    {
        var timeline = new TimezoneTimeline([Origin("Australia/Sydney")]);

        // Jan = AEDT (UTC+11); Jun = AEST (UTC+10). One static offset cannot do both.
        timeline.ToUtc(FakeUtc(2026, 1, 10, 0, 0)).Should().Be(Utc(2026, 1, 9, 13, 0));
        timeline.ToUtc(FakeUtc(2026, 6, 6, 0, 0)).Should().Be(Utc(2026, 6, 5, 14, 0));
    }

    [Fact]
    public void Toronto_AcrossSpringForward_UsesEstThenEdt()
    {
        var timeline = new TimezoneTimeline([Origin("America/Toronto")]);

        // The user's original report: 7am Mar 5 (EST -5) and 7am Mar 9 (EDT -4).
        timeline.ToUtc(FakeUtc(2026, 3, 5, 7, 0)).Should().Be(Utc(2026, 3, 5, 12, 0));
        timeline.ToUtc(FakeUtc(2026, 3, 9, 7, 0)).Should().Be(Utc(2026, 3, 9, 11, 0));
    }

    [Fact]
    public void ResultIsAlwaysUtcKind()
    {
        var timeline = new TimezoneTimeline([Origin("America/Toronto")]);
        timeline.ToUtc(FakeUtc(2026, 3, 9, 7, 0)).Kind.Should().Be(DateTimeKind.Utc);
    }

    // ── Travel (the Spain trip) ──────────────────────────────────────────────

    [Fact]
    public void SpainTrip_ReadingsInTripWindowUseTravelZone_OutsideUseHome()
    {
        // Home Sydney; trip to Madrid Mar 7-15 (CET UTC+1 in March); back to Sydney on return.
        var timeline = new TimezoneTimeline(
        [
            Origin("Australia/Sydney"),
            Entry("Europe/Madrid", new DateTime(2026, 3, 7)),
            Entry("Australia/Sydney", new DateTime(2026, 3, 15)),
        ]);

        // Mid-trip reading: 08:00 Madrid CET -> 07:00Z
        timeline.ToUtc(FakeUtc(2026, 3, 10, 8, 0)).Should().Be(Utc(2026, 3, 10, 7, 0));
        // Before the trip: 08:00 Sydney AEDT (+11) -> previous day 21:00Z
        timeline.ToUtc(FakeUtc(2026, 3, 5, 8, 0)).Should().Be(Utc(2026, 3, 4, 21, 0));
        // After return: 08:00 Sydney AEDT (+11) -> previous day 21:00Z
        timeline.ToUtc(FakeUtc(2026, 3, 20, 8, 0)).Should().Be(Utc(2026, 3, 19, 21, 0));
    }

    [Fact]
    public void Move_OpenEndedRelocation_AppliesFromTheMoveDateForward()
    {
        var timeline = new TimezoneTimeline(
        [
            Origin("Australia/Sydney"),
            Entry("America/Toronto", new DateTime(2026, 6, 1)),
        ]);

        // Before the move: Sydney AEST (+10)
        timeline.ToUtc(FakeUtc(2026, 5, 20, 12, 0)).Should().Be(Utc(2026, 5, 20, 2, 0));
        // After the move: Toronto EDT (-4)
        timeline.ToUtc(FakeUtc(2026, 6, 10, 12, 0)).Should().Be(Utc(2026, 6, 10, 16, 0));
    }

    [Fact]
    public void ZoneAt_PicksTheCoveringEntry()
    {
        var timeline = new TimezoneTimeline(
        [
            Origin("Australia/Sydney"),
            Entry("Europe/Madrid", new DateTime(2026, 3, 7)),
            Entry("Australia/Sydney", new DateTime(2026, 3, 15)),
        ]);

        timeline.ZoneAt(FakeUtc(2026, 3, 10, 8)).Should().Be("Europe/Madrid");
        timeline.ZoneAt(FakeUtc(2026, 3, 5, 8)).Should().Be("Australia/Sydney");
    }

    // ── DST edge hours never throw and never drop a reading ──────────────────

    [Fact]
    public void SpringForwardGap_NonExistentLocalTime_IsNudgedPastTheGap()
    {
        var timeline = new TimezoneTimeline([Origin("America/Toronto")]);

        // 02:30 on 2026-03-08 never existed (clocks jumped 02:00 -> 03:00). Nudge to 03:30 EDT (-4) = 07:30Z.
        var act = () => timeline.ToUtc(FakeUtc(2026, 3, 8, 2, 30));
        act.Should().NotThrow();
        timeline.ToUtc(FakeUtc(2026, 3, 8, 2, 30)).Should().Be(Utc(2026, 3, 8, 7, 30));
    }

    [Fact]
    public void FallBackAmbiguousHour_TakesStandardTimeInterpretation()
    {
        var timeline = new TimezoneTimeline([Origin("America/Toronto")]);

        // 01:30 on 2026-11-01 occurs twice; standard-time (EST -5) interpretation = 06:30Z.
        var result = timeline.ToUtc(FakeUtc(2026, 11, 1, 1, 30));
        result.Kind.Should().Be(DateTimeKind.Utc);
        result.Should().Be(Utc(2026, 11, 1, 6, 30));
    }

    // ── Empty-timeline fallback (no regression for un-seeded tenants) ────────

    [Fact]
    public void EmptyTimeline_WithLegacyOffset_AppliesTheOffset()
    {
        var timeline = new TimezoneTimeline([], fallbackOffsetHours: -5);

        // Legacy behaviour: corrected = wall - offset = 07:00 - (-5) = 12:00Z.
        timeline.ToUtc(FakeUtc(2026, 3, 5, 7, 0)).Should().Be(Utc(2026, 3, 5, 12, 0));
    }

    [Fact]
    public void EmptyTimeline_NoOffset_TreatsValueAsUtc()
    {
        var timeline = new TimezoneTimeline([]);
        timeline.ToUtc(FakeUtc(2026, 3, 5, 7, 0)).Should().Be(Utc(2026, 3, 5, 7, 0));
    }

    [Fact]
    public void TimestampBeforeEarliestEntry_FallsBackNotThrows()
    {
        // Earliest entry starts 2026-06-01; a reading before it has no covering entry.
        var timeline = new TimezoneTimeline([Entry("America/Toronto", new DateTime(2026, 6, 1))], fallbackOffsetHours: 0);
        timeline.ToUtc(FakeUtc(2026, 1, 1, 7, 0)).Should().Be(Utc(2026, 1, 1, 7, 0));
    }
}
