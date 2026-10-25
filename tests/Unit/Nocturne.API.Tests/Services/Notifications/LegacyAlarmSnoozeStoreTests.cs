using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Services.Notifications;

namespace Nocturne.API.Tests.Services.Notifications;

[Trait("Category", "Unit")]
public class LegacyAlarmSnoozeStoreTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 24, 3, 0, 0, TimeSpan.Zero));

    [Fact]
    public void A_snooze_refuses_another_until_it_ends()
    {
        var store = new LegacyAlarmSnoozeStore(_time);

        store.TrySnooze(Tenant, 2, "default", 60_000).Should().BeTrue();
        _time.Advance(TimeSpan.FromSeconds(59));
        store.TrySnooze(Tenant, 2, "default", 60_000).Should().BeFalse();
        _time.Advance(TimeSpan.FromSeconds(1));
        store.TrySnooze(Tenant, 2, "default", 60_000).Should().BeTrue();
    }

    [Fact]
    public void A_snooze_holds_only_its_own_level_and_group()
    {
        var store = new LegacyAlarmSnoozeStore(_time);

        store.TrySnooze(Tenant, 2, "default", 60_000).Should().BeTrue();

        store.TrySnooze(Tenant, 1, "default", 60_000).Should().BeTrue();
        store.TrySnooze(Tenant, 2, "pump", 60_000).Should().BeTrue();
    }

    [Fact]
    public void A_snooze_lasts_at_most_a_day()
    {
        var store = new LegacyAlarmSnoozeStore(_time);

        store.TrySnooze(Tenant, 2, "default", int.MaxValue).Should().BeTrue();

        _time.Advance(TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1));
        store.TrySnooze(Tenant, 2, "default", 60_000).Should().BeFalse();
        _time.Advance(TimeSpan.FromSeconds(1));
        store.TrySnooze(Tenant, 2, "default", 60_000).Should().BeTrue();
    }

    [Fact]
    public void A_tenant_cannot_grow_past_its_cap_and_another_tenant_is_unaffected()
    {
        var other = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var store = new LegacyAlarmSnoozeStore(_time);
        store.TrySnooze(other, 2, "keep", int.MaxValue).Should().BeTrue();

        for (var i = 0; i < LegacyAlarmSnoozeStore.MaxAlarmsPerTenant * 3; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            store.TrySnooze(Tenant, 2, $"group-{i}", int.MaxValue).Should().BeTrue();
        }

        store.TrySnooze(other, 2, "keep", 60_000).Should().BeFalse();
        store.TrySnooze(Tenant, 2, $"group-{LegacyAlarmSnoozeStore.MaxAlarmsPerTenant * 3 - 1}", 60_000).Should().BeFalse();
        store.TrySnooze(Tenant, 2, "group-0", 60_000).Should().BeTrue();
    }

    [Fact]
    public void A_full_tenant_drops_ended_snoozes_before_a_live_one()
    {
        var store = new LegacyAlarmSnoozeStore(_time);
        store.TrySnooze(Tenant, 2, "live", 3_600_000).Should().BeTrue();
        for (var i = 0; i < LegacyAlarmSnoozeStore.MaxAlarmsPerTenant - 1; i++)
            store.TrySnooze(Tenant, 2, $"short-{i}", 60_000).Should().BeTrue();

        _time.Advance(TimeSpan.FromMinutes(2));
        store.TrySnooze(Tenant, 2, "new", 60_000).Should().BeTrue();

        store.TrySnooze(Tenant, 2, "live", 60_000).Should().BeFalse();
    }

    [Fact]
    public void A_long_group_is_cut_so_two_long_groups_with_one_prefix_share_a_snooze()
    {
        var store = new LegacyAlarmSnoozeStore(_time);
        var prefix = new string('g', LegacyAlarmSnoozeStore.MaxGroupLength);

        store.TrySnooze(Tenant, 2, prefix + "a", 60_000).Should().BeTrue();

        store.TrySnooze(Tenant, 2, prefix + "b", 60_000).Should().BeFalse();
    }
}
