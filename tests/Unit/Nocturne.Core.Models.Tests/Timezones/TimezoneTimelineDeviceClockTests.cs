using FluentAssertions;
using Nocturne.Core.Models.Timezones;
using Xunit;

namespace Nocturne.Core.Models.Tests.Timezones;

/// <summary>
/// The composition rule: explicit user entries win, derived device-clock segments apply only where
/// the machine-seeded origin (or nothing) is in effect, and the legacy static offset stays the last
/// resort.
/// </summary>
[Trait("Category", "Unit")]
public class TimezoneTimelineDeviceClockTests
{
    private static TimezoneTimelineEntry Entry(string zone, DateTime effectiveFromLocal) =>
        new() { Id = Guid.NewGuid(), Timezone = zone, EffectiveFrom = DateTime.SpecifyKind(effectiveFromLocal, DateTimeKind.Unspecified) };

    private static TimezoneTimelineEntry Origin(string zone) => Entry(zone, DateTime.MinValue);

    private static DateTime Utc(int mo, int d, int h, int mi = 0) =>
        new(2026, mo, d, h, mi, 0, DateTimeKind.Utc);

    // Poseidon-shaped: home America/New_York (EDT −4 in April), device ran at UTC+2 for a window.
    private static readonly DeviceClockSegment AprilSegment = new()
    {
        FromUtc = Utc(4, 10, 0),
        ToUtc = Utc(4, 13, 0),
        OffsetMinutes = 120,
    };

    [Fact]
    public void SegmentOverridesTheSeededOrigin_InsideItsWindow()
    {
        var timeline = new TimezoneTimeline([Origin("America/New_York")], null, [AprilSegment]);

        // Wall 14:00 on Apr 11 → device offset +2 → 12:00 UTC (the zone would have said 18:00).
        timeline.ToUtc(Utc(4, 11, 14)).Should().Be(Utc(4, 11, 12));
    }

    [Fact]
    public void OutsideTheWindow_TheZoneStillApplies()
    {
        var timeline = new TimezoneTimeline([Origin("America/New_York")], null, [AprilSegment]);

        timeline.ToUtc(Utc(4, 20, 14)).Should().Be(Utc(4, 20, 18)); // EDT −4
        timeline.ToUtc(Utc(4, 5, 14)).Should().Be(Utc(4, 5, 18));
    }

    [Fact]
    public void ExplicitUserEntry_WinsOverASegmentCoveringTheSameWindow()
    {
        // The user asserted the trip themselves; a derived segment disagreeing with their entry
        // (+5h here vs Madrid's +2h) must not override it.
        var disagreeing = new DeviceClockSegment { FromUtc = Utc(4, 10, 0), ToUtc = Utc(4, 13, 0), OffsetMinutes = 300 };
        var timeline = new TimezoneTimeline(
            [
                Origin("America/New_York"),
                Entry("Europe/Madrid", new DateTime(2026, 4, 9)),
                Entry("America/New_York", new DateTime(2026, 4, 14)),
            ],
            null,
            [disagreeing]);

        timeline.ToUtc(Utc(4, 11, 14)).Should().Be(Utc(4, 11, 12)); // Madrid +2, not +5
    }

    [Fact]
    public void OpenEndedSegment_AppliesFromItsStartOnward()
    {
        var open = new DeviceClockSegment { FromUtc = Utc(4, 10, 0), ToUtc = null, OffsetMinutes = 120 };
        var timeline = new TimezoneTimeline([Origin("America/New_York")], null, [open]);

        timeline.ToUtc(Utc(6, 1, 14)).Should().Be(Utc(6, 1, 12));
    }

    [Fact]
    public void ToLocal_ReversesTheSegmentConversion()
    {
        var timeline = new TimezoneTimeline([Origin("America/New_York")], null, [AprilSegment]);

        timeline.ToLocal(Utc(4, 11, 12)).Should().Be(Utc(4, 11, 14));
        timeline.ToLocal(Utc(4, 20, 18)).Should().Be(Utc(4, 20, 14)); // outside: zone applies
    }

    [Fact]
    public void SegmentWithNoTimelineEntries_StillApplies_AndFallbackCoversTheRest()
    {
        var timeline = new TimezoneTimeline([], fallbackOffsetHours: -4, [AprilSegment]);

        timeline.ToUtc(Utc(4, 11, 14)).Should().Be(Utc(4, 11, 12)); // segment
        timeline.ToUtc(Utc(4, 20, 14)).Should().Be(Utc(4, 20, 18)); // static offset
    }

    // ── OffsetMinutesAtUtc (the detector's "expected clock") ─────────────────

    [Fact]
    public void ExpectedOffset_IsDstAware()
    {
        var timeline = new TimezoneTimeline([Origin("America/New_York")]);

        timeline.OffsetMinutesAtUtc(Utc(4, 10, 12)).Should().Be(-240); // EDT
        timeline.OffsetMinutesAtUtc(Utc(1, 10, 12)).Should().Be(-300); // EST
    }

    [Fact]
    public void ExpectedOffset_IgnoresDerivedSegments()
    {
        // The detector must measure against the user-asserted clock, never its own output.
        var timeline = new TimezoneTimeline([Origin("America/New_York")], null, [AprilSegment]);

        timeline.OffsetMinutesAtUtc(Utc(4, 11, 12)).Should().Be(-240);
    }

    [Fact]
    public void ExpectedOffset_FallsBackToStaticOffset_ThenZero()
    {
        new TimezoneTimeline([], fallbackOffsetHours: 10).OffsetMinutesAtUtc(Utc(4, 11, 12)).Should().Be(600);
        new TimezoneTimeline([]).OffsetMinutesAtUtc(Utc(4, 11, 12)).Should().Be(0);
    }
}
