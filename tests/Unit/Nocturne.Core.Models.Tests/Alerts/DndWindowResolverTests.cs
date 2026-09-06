using System;
using System.Collections.Generic;
using FluentAssertions;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.Core.Models.Tests.Alerts;

/// <summary>
/// <see cref="DndWindowResolver"/> (ADR 0004 D5): the single resolver both the live enricher
/// and the replay walker use to turn a tenant's DND windows into the active scope set plus the
/// tenant-wide <see cref="DoNotDisturbSnapshot"/>.
/// </summary>
[Trait("Category", "Unit")]
public class DndWindowResolverTests
{
    private static readonly DateTime T0 = new(2026, 06, 21, 12, 00, 00, DateTimeKind.Utc);
    private static DateTime Min(int m) => T0.AddMinutes(m);

    private static DndWindowSnapshot Window(
        DndScope scope,
        DateTime? startedAt = null,
        DateTime? endsAt = null,
        DateTime? clearedAt = null,
        DateTime? createdAt = null) =>
        new(scope, startedAt ?? Min(-10), endsAt, clearedAt, createdAt ?? startedAt ?? Min(-10));

    [Fact]
    public void NoWindows_resolvesToNothingActive()
    {
        var resolved = DndWindowResolver.Resolve(Array.Empty<DndWindowSnapshot>(), T0, receiptGated: false);

        resolved.Scopes.Should().BeEmpty();
        resolved.ActiveDoNotDisturb.Should().BeNull();
    }

    [Fact]
    public void AnAllWindow_populatesBothScopesAndTheProjection()
    {
        var resolved = DndWindowResolver.Resolve(
            new[] { Window(DndScope.All, startedAt: Min(-30)) }, T0, receiptGated: false);

        resolved.Scopes.Should().BeEquivalentTo(new[] { DndScope.All });
        resolved.ActiveDoNotDisturb.Should().NotBeNull();
        resolved.ActiveDoNotDisturb!.StartedAt.Should().Be(Min(-30));
        resolved.ActiveDoNotDisturb.Source.Should().Be("manual");
    }

    [Fact]
    public void ScopedWindows_feedTheGateOnly_andNeverTripTheConditionLeaf()
    {
        var resolved = DndWindowResolver.Resolve(
            new[] { Window(DndScope.Lows), Window(DndScope.Highs) }, T0, receiptGated: false);

        resolved.Scopes.Should().BeEquivalentTo(new[] { DndScope.Lows, DndScope.Highs });
        // do_not_disturb is the tenant-wide notion; a lows/highs mute is not tenant-wide.
        resolved.ActiveDoNotDisturb.Should().BeNull();
    }

    [Fact]
    public void ExpiredClearedAndNotYetStartedWindows_areAllInert()
    {
        var windows = new[]
        {
            Window(DndScope.Lows, startedAt: Min(-60), endsAt: Min(-30)),   // expired
            Window(DndScope.Highs, startedAt: Min(-60), clearedAt: Min(-5)), // cleared
            Window(DndScope.All, startedAt: Min(30)),                        // not started
        };

        var resolved = DndWindowResolver.Resolve(windows, T0, receiptGated: false);

        resolved.Scopes.Should().BeEmpty();
        resolved.ActiveDoNotDisturb.Should().BeNull();
    }

    [Fact]
    public void TheProjectionAnchorsOnTheEarliestActiveAllWindow()
    {
        var windows = new[]
        {
            Window(DndScope.All, startedAt: Min(-5)),
            Window(DndScope.All, startedAt: Min(-45)),
        };

        var resolved = DndWindowResolver.Resolve(windows, T0, receiptGated: false);

        // for_minutes measures from when the mute began, so the oldest active one wins.
        resolved.ActiveDoNotDisturb!.StartedAt.Should().Be(Min(-45));
    }

    /// <summary>
    /// Manual-wins is the pre-window <c>TenantAlertSettingsSnapshot.Resolve</c> contract — it
    /// tested the manual path first. It matters because a scheduled window opening on top of a
    /// running manual mute must not restart the anchor a sustained <c>do_not_disturb</c>
    /// condition measures its elapsed time from.
    /// </summary>
    [Fact]
    public void AnActiveManualWindow_outranksScheduledDndForTheAnchor()
    {
        var scheduled = new TenantAlertSettingsSnapshot.ActiveProjection(Min(-120), "schedule");

        var resolved = DndWindowResolver.Resolve(
            new[] { Window(DndScope.All, startedAt: Min(-20)) }, T0, receiptGated: false, scheduled);

        resolved.Scopes.Should().Contain(DndScope.All);
        resolved.ActiveDoNotDisturb!.StartedAt.Should().Be(Min(-20));
        resolved.ActiveDoNotDisturb.Source.Should().Be("manual");
    }

    [Fact]
    public void ScheduledDnd_aloneStillProducesTheAllScope()
    {
        var scheduled = new TenantAlertSettingsSnapshot.ActiveProjection(Min(-15), "schedule");

        var resolved = DndWindowResolver.Resolve(
            Array.Empty<DndWindowSnapshot>(), T0, receiptGated: false, scheduled);

        resolved.Scopes.Should().BeEquivalentTo(new[] { DndScope.All });
        resolved.ActiveDoNotDisturb!.Source.Should().Be("schedule");
    }

    [Fact]
    public void ReceiptGating_hidesAWindowTheServerHadNotYetReceived()
    {
        // Authored as starting an hour ago, but only synced five minutes ago.
        var window = Window(DndScope.All, startedAt: Min(-60), createdAt: Min(-5));
        var beforeReceipt = Min(-30);

        // Live: it is active now, whatever its receipt time.
        DndWindowResolver.Resolve(new[] { window }, T0, receiptGated: false)
            .Scopes.Should().Contain(DndScope.All);

        // Replay at an instant before receipt: the live engine could not have known, so replay
        // must not retroactively suppress the offline-authoring gap.
        var replayed = DndWindowResolver.Resolve(new[] { window }, beforeReceipt, receiptGated: true);
        replayed.Scopes.Should().BeEmpty();
        replayed.ActiveDoNotDisturb.Should().BeNull();
    }

    [Fact]
    public void ReceiptGating_admitsTheWindowOnceReceived()
    {
        var window = Window(DndScope.All, startedAt: Min(-60), createdAt: Min(-5));

        var replayed = DndWindowResolver.Resolve(new[] { window }, T0, receiptGated: true);

        replayed.Scopes.Should().Contain(DndScope.All);
        replayed.ActiveDoNotDisturb!.StartedAt.Should().Be(Min(-60));
    }

    /// <summary>
    /// The pairing the gate depends on: whenever <c>all</c> is in force the condition leaf sees
    /// DND too, and whenever it is not, it does not. Replay set only the scope half at one point,
    /// which made a tick the gate called suppressed evaluate its own do_not_disturb leaf false.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheProjectionIsNonNullExactlyWhenAllIsActive(bool receiptGated)
    {
        var windows = new List<DndWindowSnapshot>
        {
            Window(DndScope.Lows),
            Window(DndScope.All, startedAt: Min(-60), endsAt: Min(-30)),
        };

        var withoutAll = DndWindowResolver.Resolve(windows, T0, receiptGated);
        withoutAll.Scopes.Should().NotContain(DndScope.All);
        withoutAll.ActiveDoNotDisturb.Should().BeNull();

        windows.Add(Window(DndScope.All));
        var withAll = DndWindowResolver.Resolve(windows, T0, receiptGated);
        withAll.Scopes.Should().Contain(DndScope.All);
        withAll.ActiveDoNotDisturb.Should().NotBeNull();
    }
}
