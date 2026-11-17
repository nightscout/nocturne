using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Services.Seeding;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Seeding;

/// <summary>
/// A seed lays its days out in the tenant's zone. The instant below is Saturday 20:00 UTC, which is
/// already Sunday 09:00 in Auckland, so a seed that read the host's zone would end a day early.
/// </summary>
public class SampleDataSeederTenantClockTests : IDisposable
{
    private const string Auckland = "Pacific/Auckland";
    private static readonly Guid TenantId = Guid.Parse("0196a3f2-7b1e-7c3a-9d52-4e8f1a6b2c10");
    private static readonly DateTimeOffset Instant = new(2026, 3, 14, 20, 0, 0, TimeSpan.Zero);

    private readonly SqliteTestDatabase _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId);

    public void Dispose() => _db.Dispose();

    private static SeedingClock AucklandClock() =>
        SeedingClock.Resolve(Auckland, new FakeTimeProvider(Instant));

    [Fact]
    public void Today_is_the_tenants_local_day()
    {
        var clock = AucklandClock();

        clock.LocalToday.Should().Be(new DateTime(2026, 3, 15));
        clock.LocalNow.Should().Be(new DateTime(2026, 3, 15, 9, 0, 0));
    }

    [Fact]
    public void A_local_time_converts_to_utc_in_the_tenants_zone()
    {
        AucklandClock().ToUtc(new DateTime(2026, 3, 15, 7, 0, 0))
            .Should().Be(new DateTime(2026, 3, 14, 18, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void A_time_in_a_skipped_hour_reads_at_the_standard_offset()
    {
        var clock = SeedingClock.Resolve("America/New_York", new FakeTimeProvider(Instant));

        clock.ToUtc(new DateTime(2026, 3, 8, 2, 30, 0))
            .Should().Be(new DateTime(2026, 3, 8, 7, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void The_generator_frame_is_the_tenants_zone_not_the_hosts()
    {
        var time = new FakeTimeProvider(Instant);
        time.SetLocalTimeZone(TimeZoneInfo.Utc);

        var demo = SeedingClock.Resolve(Auckland, time).Demo;

        demo.LocalNow.Should().Be(new DateTime(2026, 3, 15, 9, 0, 0));
        demo.ToUtc(new DateTime(2026, 3, 15, 9, 0, 0)).Should().Be(Instant.UtcDateTime);
    }

    [Fact]
    public void An_unknown_recorded_zone_falls_back_to_the_providers_zone()
    {
        var time = new FakeTimeProvider(Instant);
        time.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById(Auckland));

        var clock = SeedingClock.Resolve("Not/AZone", time);

        clock.ZoneId.Should().Be(Auckland);
        clock.LocalNow.Should().Be(new DateTime(2026, 3, 15, 9, 0, 0));
    }

    [Fact]
    public void No_recorded_zone_falls_back_to_the_providers_zone_as_an_iana_id()
    {
        var time = new FakeTimeProvider(Instant);
        time.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("New Zealand Standard Time"));

        SeedingClock.Resolve(null, time).ZoneId.Should().Be(Auckland);
    }

    [Fact]
    public async Task The_weigh_in_cut_off_is_the_tenants_local_time()
    {
        var clock = AucklandClock();
        await using var db = _db.CreateContext();
        db.TenantId = TenantId;

        await SampleDataSeeder.SeedBodyWeightAsync(db, clock, days: 7, "dev-sample", CancellationToken.None);

        var sunday = await db.BodyWeights.SingleAsync(w => w.SyncIdentifier == "demo-weight-2026-03-15");
        var weighedAt = DateTimeOffset.FromUnixTimeMilliseconds(sunday.Mills).UtcDateTime;
        weighedAt.Should().BeOnOrAfter(new DateTime(2026, 3, 14, 18, 0, 0, DateTimeKind.Utc));
        weighedAt.Should().BeBefore(new DateTime(2026, 3, 14, 19, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task The_weigh_in_cut_off_leaves_out_a_weigh_in_still_to_come_locally()
    {
        var beforeSevenLocal = new FakeTimeProvider(new DateTimeOffset(2026, 3, 14, 17, 0, 0, TimeSpan.Zero));
        var clock = SeedingClock.Resolve(Auckland, beforeSevenLocal);
        await using var db = _db.CreateContext();
        db.TenantId = TenantId;

        await SampleDataSeeder.SeedBodyWeightAsync(db, clock, days: 7, "dev-sample", CancellationToken.None);

        (await db.BodyWeights.AnyAsync(w => w.SyncIdentifier == "demo-weight-2026-03-15")).Should().BeFalse();
        (await db.BodyWeights.AnyAsync(w => w.SyncIdentifier == "demo-weight-2026-03-08")).Should().BeTrue();
    }

    [Fact]
    public async Task The_timezone_timeline_starts_at_the_tenants_window_start_in_the_tenants_zone()
    {
        var clock = AucklandClock();
        await using var db = _db.CreateContext();
        db.TenantId = TenantId;

        await SampleDataSeeder.SeedTimezoneTimelineAsync(db, clock, days: 30, CancellationToken.None);

        var rows = await db.TimezoneTimeline.OrderBy(t => t.EffectiveFrom).ToListAsync();
        rows.Should().HaveCount(3);
        rows[0].EffectiveFrom.Should().Be(new DateTime(2026, 2, 13));
        rows[0].Timezone.Should().Be(Auckland);
        rows[2].Timezone.Should().Be(Auckland);
    }

    [Fact]
    public async Task The_dnd_window_is_the_tenants_previous_evening()
    {
        var clock = AucklandClock();
        await using var db = _db.CreateContext();
        db.TenantId = TenantId;

        await SampleDataSeeder.SeedDndWindowAsync(db, clock, CancellationToken.None);

        var window = await db.DndWindows.SingleAsync();
        window.StartedAt.Should().Be(new DateTime(2026, 3, 14, 9, 30, 0, DateTimeKind.Utc));
    }
}
