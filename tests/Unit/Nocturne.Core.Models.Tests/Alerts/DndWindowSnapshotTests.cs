using System;
using FluentAssertions;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.Core.Models.Tests.Alerts;

/// <summary>
/// Resolve-vs-stored for <see cref="DndWindowSnapshot"/> (ADR 0004): live
/// (<see cref="DndWindowSnapshot.IsActiveAt"/>) and replay
/// (<see cref="DndWindowSnapshot.WasActiveAt"/>, receipt-gated).
/// </summary>
[Trait("Category", "Unit")]
public class DndWindowSnapshotTests
{
    private static readonly DateTime T0 = new(2026, 06, 21, 12, 00, 00, DateTimeKind.Utc);
    private static DateTime Min(int m) => T0.AddMinutes(m);

    private static DndWindowSnapshot Window(
        DateTime? startedAt = null,
        DateTime? endsAt = null,
        DateTime? clearedAt = null,
        DateTime? createdAt = null) =>
        new(
            DndScope.Lows,
            StartedAt: startedAt ?? T0,
            EndsAt: endsAt,
            ClearedAt: clearedAt,
            CreatedAt: createdAt ?? startedAt ?? T0);

    // ---- live: IsActiveAt(now) ----

    [Fact]
    public void IsActiveAt_Active_WhenStartedNotClearedIndefinite() =>
        Window(startedAt: Min(-1)).IsActiveAt(T0).Should().BeTrue();

    [Fact]
    public void IsActiveAt_Active_WithinExpiry() =>
        Window(startedAt: Min(-1), endsAt: Min(30)).IsActiveAt(T0).Should().BeTrue();

    [Fact]
    public void IsActiveAt_Inactive_NotYetStarted() =>
        Window(startedAt: Min(1)).IsActiveAt(T0).Should().BeFalse();

    [Fact]
    public void IsActiveAt_Inactive_Expired() =>
        Window(startedAt: Min(-30), endsAt: Min(-1)).IsActiveAt(T0).Should().BeFalse();

    [Fact]
    public void IsActiveAt_Inactive_Cleared() =>
        Window(startedAt: Min(-30), clearedAt: Min(-1)).IsActiveAt(T0).Should().BeFalse();

    [Fact]
    public void IsActiveAt_ExpiryIsExclusive() =>
        // now == ends_at => expired (active requires now < ends).
        Window(startedAt: Min(-30), endsAt: T0).IsActiveAt(T0).Should().BeFalse();

    // ---- replay: WasActiveAt(T), receipt-gated (ADR 0004 D7) ----

    [Fact]
    public void WasActiveAt_Inactive_WhenReceivedAfterT_OfflineGap()
    {
        // Authored (started) offline at T0-5, but the server only received it at T0+5.
        // Replaying the tick at T0 must NOT see it active — the live engine didn't,
        // and the gap-delivered events must not be retroactively suppressed.
        var w = Window(startedAt: Min(-5), createdAt: Min(5));
        w.WasActiveAt(T0).Should().BeFalse();
    }

    [Fact]
    public void WasActiveAt_Active_WhenReceivedByT() =>
        Window(startedAt: Min(-5), createdAt: Min(-5)).WasActiveAt(T0).Should().BeTrue();

    [Fact]
    public void WasActiveAt_RespectsHistoricalClear()
    {
        var w = Window(startedAt: Min(-30), createdAt: Min(-30), clearedAt: Min(-1));
        w.WasActiveAt(T0).Should().BeFalse();      // cleared before this tick
        w.WasActiveAt(Min(-5)).Should().BeTrue();  // still active at an earlier tick
    }

    [Fact]
    public void WasActiveAt_RespectsHistoricalExpiry()
    {
        var w = Window(startedAt: Min(-30), createdAt: Min(-30), endsAt: Min(-1));
        w.WasActiveAt(T0).Should().BeFalse();
        w.WasActiveAt(Min(-5)).Should().BeTrue();
    }

    // ---- boundary ties + supersede edges (from review) ----

    [Fact]
    public void IsActiveAt_StartedAtInstant_IsActive() =>
        // started_at is inclusive.
        Window(startedAt: T0).IsActiveAt(T0).Should().BeTrue();

    [Fact]
    public void WasActiveAt_ReceivedAtInstant_IsActive() =>
        // created_at is inclusive — received exactly at the tick.
        Window(startedAt: Min(-5), createdAt: T0).WasActiveAt(T0).Should().BeTrue();

    [Fact]
    public void ClearedBeforeStarted_NeverActive()
    {
        // Superseded so fast the clear predates intent — never active at any tick.
        var w = Window(startedAt: T0, createdAt: T0, clearedAt: Min(-1));
        w.IsActiveAt(T0).Should().BeFalse();
        w.WasActiveAt(T0).Should().BeFalse();
    }

    [Fact]
    public void ClearAndExpiryBothSet_EarlierBoundWins()
    {
        // Cleared at +10, expiry at +30 → the earlier bound (clear) ends it.
        var w = Window(startedAt: Min(-5), createdAt: Min(-5), endsAt: Min(30), clearedAt: Min(10));
        w.IsActiveAt(Min(5)).Should().BeTrue();    // before either bound
        w.IsActiveAt(Min(20)).Should().BeFalse();  // past the clear, before expiry
    }
}
