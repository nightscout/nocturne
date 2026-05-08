using FluentAssertions;
using Nocturne.Core.Models.Basal;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Core.Models.Tests.Basal;

[Trait("Category", "Unit")]
public class BasalCalculatorTests
{
    private const long HourMs = 3_600_000L;
    private const long DayMs = 24 * HourMs;

    /// <summary>2026-01-15 00:00:00 UTC — a non-DST winter date for clean math.</summary>
    private const long Jan15Midnight = 1768435200000L;

    private static ScheduleEntry Entry(string time, double value, int? seconds = null)
        => new() { Time = time, Value = value, TimeAsSeconds = seconds ?? (int)TimeOnly.Parse(time).ToTimeSpan().TotalSeconds };

    private static ScheduleAssignment Assign(
        long start, long end, IReadOnlyList<ScheduleEntry>? entries = null,
        double percentage = 100, long timeshiftMs = 0, string? tz = "UTC")
        => new(start, end, "Default", entries ?? [Entry("00:00", 1.0)], percentage, timeshiftMs, tz);

    // ─── basic shape ───────────────────────────────────────────────────────────

    [Fact]
    public void Empty_window_yields_no_segments()
    {
        var window = new BasalWindow(Jan15Midnight, Jan15Midnight); // zero-length
        var assignment = Assign(0, long.MaxValue);

        BasalCalculator.BuildSegments(window, [assignment]).Should().BeEmpty();
    }

    [Fact]
    public void Inverted_window_yields_no_segments()
    {
        var window = new BasalWindow(Jan15Midnight + HourMs, Jan15Midnight);
        var assignment = Assign(0, long.MaxValue);

        BasalCalculator.BuildSegments(window, [assignment]).Should().BeEmpty();
    }

    [Fact]
    public void No_assignments_yields_no_segments()
    {
        var window = new BasalWindow(Jan15Midnight, Jan15Midnight + DayMs);
        BasalCalculator.BuildSegments(window, []).Should().BeEmpty();
    }

    // ─── single-entry schedule ─────────────────────────────────────────────────

    [Fact]
    public void Single_entry_24h_emits_one_segment_clipped_to_window()
    {
        var window = new BasalWindow(Jan15Midnight, Jan15Midnight + DayMs);
        var assignment = Assign(Jan15Midnight - DayMs, Jan15Midnight + 2 * DayMs,
            [Entry("00:00", 0.8)]);

        var segs = BasalCalculator.BuildSegments(window, [assignment]).ToList();

        segs.Should().HaveCount(1);
        segs[0].StartMills.Should().Be(Jan15Midnight);
        segs[0].EndMills.Should().Be(Jan15Midnight + DayMs);
        segs[0].UnitsPerHour.Should().Be(0.8);
        segs[0].Units.Should().BeApproximately(0.8 * 24, 1e-9);
    }

    [Fact]
    public void Empty_entries_falls_back_to_default_rate_one_unit_per_hour()
    {
        var window = new BasalWindow(Jan15Midnight, Jan15Midnight + 6 * HourMs);
        var assignment = Assign(Jan15Midnight, Jan15Midnight + DayMs, []);

        var segs = BasalCalculator.BuildSegments(window, [assignment]).ToList();

        segs.Should().HaveCount(1);
        segs[0].UnitsPerHour.Should().Be(1.0);
        segs[0].Units.Should().BeApproximately(6.0, 1e-9);
    }

    // ─── multi-entry schedule, UTC, integration ────────────────────────────────

    [Fact]
    public void Multi_entry_emits_one_segment_per_entry_within_a_day()
    {
        // Schedule: 00:00 → 0.5, 06:00 → 1.0, 22:00 → 0.7
        var window = new BasalWindow(Jan15Midnight, Jan15Midnight + DayMs);
        var assignment = Assign(0, long.MaxValue,
            [Entry("00:00", 0.5), Entry("06:00", 1.0), Entry("22:00", 0.7)]);

        var segs = BasalCalculator.BuildSegments(window, [assignment]).ToList();

        // Three rate slots within the day: 00–06 (0.5), 06–22 (1.0), 22–24 (0.7)
        segs.Should().HaveCount(3);
        segs[0].UnitsPerHour.Should().Be(0.5);
        segs[0].DurationMills.Should().Be(6 * HourMs);
        segs[1].UnitsPerHour.Should().Be(1.0);
        segs[1].DurationMills.Should().Be(16 * HourMs);
        segs[2].UnitsPerHour.Should().Be(0.7);
        segs[2].DurationMills.Should().Be(2 * HourMs);

        // Total units = 0.5 * 6 + 1.0 * 16 + 0.7 * 2 = 3 + 16 + 1.4 = 20.4
        segs.Sum(s => s.Units).Should().BeApproximately(20.4, 1e-9);
    }

    [Fact]
    public void Window_starts_mid_segment_takes_active_rate_until_next_boundary()
    {
        // Schedule: 00:00 → 0.5, 06:00 → 1.0
        // Window: 03:00 to 09:00 → segment (03:00..06:00 at 0.5), (06:00..09:00 at 1.0)
        var window = new BasalWindow(Jan15Midnight + 3 * HourMs, Jan15Midnight + 9 * HourMs);
        var assignment = Assign(0, long.MaxValue,
            [Entry("00:00", 0.5), Entry("06:00", 1.0)]);

        var segs = BasalCalculator.BuildSegments(window, [assignment]).ToList();

        segs.Should().HaveCount(2);
        segs[0].UnitsPerHour.Should().Be(0.5);
        segs[0].DurationMills.Should().Be(3 * HourMs);
        segs[1].UnitsPerHour.Should().Be(1.0);
        segs[1].DurationMills.Should().Be(3 * HourMs);
        segs.Sum(s => s.Units).Should().BeApproximately(0.5 * 3 + 1.0 * 3, 1e-9);
    }

    [Fact]
    public void Multi_day_window_repeats_schedule_per_day()
    {
        // 3-day window with constant 1.0 schedule should produce the same total as the closed form.
        var window = new BasalWindow(Jan15Midnight, Jan15Midnight + 3 * DayMs);
        var assignment = Assign(0, long.MaxValue,
            [Entry("00:00", 1.0), Entry("12:00", 2.0)]);

        var total = BasalCalculator.BuildSegments(window, [assignment]).Sum(s => s.Units);

        // 3 days × (12h × 1.0 + 12h × 2.0) = 3 × 36 = 108
        total.Should().BeApproximately(108.0, 1e-9);
    }

    // ─── CCP scaling ───────────────────────────────────────────────────────────

    [Fact]
    public void Ccp_percentage_scales_effective_rate_but_not_scheduled()
    {
        var window = new BasalWindow(Jan15Midnight, Jan15Midnight + 4 * HourMs);
        var assignment = Assign(0, long.MaxValue,
            [Entry("00:00", 1.0)],
            percentage: 80);

        var seg = BasalCalculator.BuildSegments(window, [assignment]).Single();
        seg.UnitsPerHour.Should().BeApproximately(0.8, 1e-9);
        seg.ScheduledUnitsPerHour.Should().Be(1.0);
        seg.Units.Should().BeApproximately(0.8 * 4, 1e-9);
    }

    [Fact]
    public void Ccp_timeshift_advances_the_schedule_lookup()
    {
        // Schedule: 00:00 → 0.5, 06:00 → 1.0. Timeshift = +1h means at wall-clock 05:00,
        // we look up 06:00 → rate is 1.0 from 05:00 onwards instead of 06:00.
        var window = new BasalWindow(Jan15Midnight + 4 * HourMs, Jan15Midnight + 7 * HourMs);
        var assignment = Assign(0, long.MaxValue,
            [Entry("00:00", 0.5), Entry("06:00", 1.0)],
            timeshiftMs: HourMs);

        var segs = BasalCalculator.BuildSegments(window, [assignment]).ToList();

        // 04:00..05:00 at 0.5 (lookup 05:00→entry[00:00]=0.5),
        // 05:00..07:00 at 1.0 (lookup ≥06:00→entry[06:00]=1.0)
        segs.Should().HaveCount(2);
        segs[0].StartMills.Should().Be(Jan15Midnight + 4 * HourMs);
        segs[0].EndMills.Should().Be(Jan15Midnight + 5 * HourMs);
        segs[0].UnitsPerHour.Should().Be(0.5);
        segs[1].StartMills.Should().Be(Jan15Midnight + 5 * HourMs);
        segs[1].UnitsPerHour.Should().Be(1.0);
    }

    // ─── multiple assignments (mid-window switch) ──────────────────────────────

    [Fact]
    public void Multiple_assignments_chronological_no_gap()
    {
        // First half: profile A at 0.5; second half: profile B at 1.5.
        var midWindow = Jan15Midnight + 12 * HourMs;
        var window = new BasalWindow(Jan15Midnight, Jan15Midnight + DayMs);
        var profileA = new ScheduleAssignment(
            Jan15Midnight, midWindow, "A", [Entry("00:00", 0.5)], 100, 0, "UTC");
        var profileB = new ScheduleAssignment(
            midWindow, Jan15Midnight + DayMs, "B", [Entry("00:00", 1.5)], 100, 0, "UTC");

        var segs = BasalCalculator.BuildSegments(window, [profileA, profileB]).ToList();

        segs.Should().HaveCount(2);
        segs[0].ProfileName.Should().Be("A");
        segs[0].UnitsPerHour.Should().Be(0.5);
        segs[1].ProfileName.Should().Be("B");
        segs[1].UnitsPerHour.Should().Be(1.5);
        segs.Sum(s => s.Units).Should().BeApproximately(0.5 * 12 + 1.5 * 12, 1e-9);
    }

    [Fact]
    public void Out_of_order_assignments_are_sorted_chronologically()
    {
        var midWindow = Jan15Midnight + 12 * HourMs;
        var window = new BasalWindow(Jan15Midnight, Jan15Midnight + DayMs);
        var second = new ScheduleAssignment(
            midWindow, Jan15Midnight + DayMs, "B", [Entry("00:00", 1.5)], 100, 0, "UTC");
        var first = new ScheduleAssignment(
            Jan15Midnight, midWindow, "A", [Entry("00:00", 0.5)], 100, 0, "UTC");

        var segs = BasalCalculator.BuildSegments(window, [second, first]).ToList();

        segs.Should().HaveCount(2);
        segs[0].ProfileName.Should().Be("A");
        segs[1].ProfileName.Should().Be("B");
    }

    [Fact]
    public void Assignment_outside_window_is_skipped()
    {
        var window = new BasalWindow(Jan15Midnight, Jan15Midnight + 6 * HourMs);
        var inside = Assign(Jan15Midnight, Jan15Midnight + 6 * HourMs, [Entry("00:00", 1.0)]);
        var beforeWindow = new ScheduleAssignment(
            Jan15Midnight - 2 * DayMs, Jan15Midnight - DayMs, "Old",
            [Entry("00:00", 99.0)], 100, 0, "UTC");

        var segs = BasalCalculator.BuildSegments(window, [beforeWindow, inside]).ToList();

        segs.Should().AllSatisfy(s => s.UnitsPerHour.Should().Be(1.0));
    }

    // ─── duplicate entries (review #1) ─────────────────────────────────────────

    [Fact]
    public void Duplicate_entries_at_same_time_keep_last_value()
    {
        // Two entries at 06:00 — last upload should win (0.5, not 0.9).
        var window = new BasalWindow(Jan15Midnight, Jan15Midnight + DayMs);
        var assignment = Assign(0, long.MaxValue,
            [Entry("00:00", 0.4), Entry("06:00", 0.9), Entry("06:00", 0.5)]);

        var segs = BasalCalculator.BuildSegments(window, [assignment]).ToList();

        // Two slots: 00–06 (0.4), 06–24 (0.5). The 0.9 must NOT appear.
        segs.Should().HaveCount(2);
        segs[0].UnitsPerHour.Should().Be(0.4);
        segs[1].UnitsPerHour.Should().Be(0.5);
        segs.Sum(s => s.Units).Should().BeApproximately(0.4 * 6 + 0.5 * 18, 1e-9);
    }

    // ─── DST handling (review #2) ──────────────────────────────────────────────

    [Fact]
    public void Dst_spring_forward_skipped_local_hour_resolves_to_gap_end()
    {
        // 2026-03-08 02:00 EST → 03:00 EDT. Local 02:30 doesn't exist.
        // A schedule entry at 02:30 should activate at the moment the gap ends (03:00 EDT).
        // 03:00 EDT = 07:00 UTC.
        var marchSundayMidnight = new DateTimeOffset(2026, 3, 8, 5, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(); // 2026-03-08 00:00 EST = 05:00 UTC
        var window = new BasalWindow(marchSundayMidnight, marchSundayMidnight + DayMs);
        var assignment = Assign(0, long.MaxValue,
            [Entry("00:00", 0.5), Entry("02:30", 1.0)],
            tz: "America/New_York");

        var segs = BasalCalculator.BuildSegments(window, [assignment]).ToList();

        // The 02:30 boundary should land at exactly 03:00 EDT = 07:00 UTC
        var transitionUtc = new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var transitionSeg = segs.FirstOrDefault(s => s.StartMills == transitionUtc);
        transitionSeg.UnitsPerHour.Should().Be(1.0,
            "the 02:30 entry should activate at the end of the DST gap");
    }

    [Fact]
    public void Dst_fall_back_ambiguous_local_hour_uses_first_occurrence()
    {
        // 2026-11-01 02:00 EDT → 01:00 EST. Local 01:30 happens twice.
        // A schedule entry at 01:30 should activate at the FIRST occurrence (01:30 EDT = 05:30 UTC),
        // not the second (01:30 EST = 06:30 UTC).
        var novSundayMidnight = new DateTimeOffset(2026, 11, 1, 4, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(); // 00:00 EDT = 04:00 UTC
        var window = new BasalWindow(novSundayMidnight, novSundayMidnight + DayMs);
        var assignment = Assign(0, long.MaxValue,
            [Entry("00:00", 0.5), Entry("01:30", 1.0), Entry("03:00", 0.7)],
            tz: "America/New_York");

        var segs = BasalCalculator.BuildSegments(window, [assignment]).ToList();

        // First occurrence of 01:30 EDT = 05:30 UTC
        var firstOccurrenceUtc = new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var firstOccurrenceSeg = segs.FirstOrDefault(s => s.StartMills == firstOccurrenceUtc);
        firstOccurrenceSeg.UnitsPerHour.Should().Be(1.0,
            "the 01:30 entry should activate at the first (DST-active) occurrence");
    }

    // ─── timeshift edge cases ──────────────────────────────────────────────────

    [Fact]
    public void Negative_timeshift_emits_segments_in_chronological_order()
    {
        // Schedule: 00:00 → 0.5, 06:00 → 1.0. Timeshift = -2h means lookup time = wall - 2h.
        // At wall 00:00 we look up 22:00 → entry[06:00]=1.0 (last-active-of-previous-day).
        // At wall 02:00 we look up 00:00 → entry[00:00]=0.5.
        // At wall 08:00 we look up 06:00 → entry[06:00]=1.0.
        // Boundaries land at absolute midnight + entry.Seconds - timeshiftMs:
        //   00:00 boundary at midnight + 0  - (-2h) = +2h
        //   06:00 boundary at midnight + 6h - (-2h) = +8h
        var window = new BasalWindow(Jan15Midnight, Jan15Midnight + DayMs);
        var assignment = Assign(0, long.MaxValue,
            [Entry("00:00", 0.5), Entry("06:00", 1.0)],
            timeshiftMs: -2 * HourMs);

        var segs = BasalCalculator.BuildSegments(window, [assignment]).ToList();

        segs.Should().HaveCount(3);
        segs[0].StartMills.Should().Be(Jan15Midnight);
        segs[0].EndMills.Should().Be(Jan15Midnight + 2 * HourMs);
        segs[0].UnitsPerHour.Should().Be(1.0);
        segs[1].EndMills.Should().Be(Jan15Midnight + 8 * HourMs);
        segs[1].UnitsPerHour.Should().Be(0.5);
        segs[2].UnitsPerHour.Should().Be(1.0);

        // Segments should be strictly chronological with no gaps.
        for (int i = 1; i < segs.Count; i++)
            segs[i].StartMills.Should().Be(segs[i - 1].EndMills);
    }

    [Fact]
    public void Empty_entries_with_ccp_percentage_scales_default_rate()
    {
        // Empty schedule + 80% CCP → default 1.0 U/hr × 0.8 = 0.8 U/hr.
        var window = new BasalWindow(Jan15Midnight, Jan15Midnight + 4 * HourMs);
        var assignment = Assign(Jan15Midnight, Jan15Midnight + DayMs, [],
            percentage: 80);

        var seg = BasalCalculator.BuildSegments(window, [assignment]).Single();
        seg.UnitsPerHour.Should().BeApproximately(0.8, 1e-9);
        seg.ScheduledUnitsPerHour.Should().Be(1.0);
        seg.Units.Should().BeApproximately(0.8 * 4, 1e-9);
    }
}
