using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Nocturne.Core.Models;
using Nocturne.Services.Demo.Configuration;
using Nocturne.Services.Demo.Services;
using Xunit;

namespace Nocturne.API.Tests.Services.Seeding;

/// <summary>
/// A tenant whose zone changes its clocks, generated on a host whose zone is UTC. The window is the
/// three days around a transition.
/// </summary>
public class DemoDstTests
{
    private const int Days = 3;

    public static TheoryData<string, DateTimeOffset> Transitions => new()
    {
        { "America/New_York", new DateTimeOffset(2026, 3, 9, 17, 0, 0, TimeSpan.Zero) },
        { "America/New_York", new DateTimeOffset(2026, 11, 2, 17, 0, 0, TimeSpan.Zero) },
        { "Pacific/Auckland", new DateTimeOffset(2026, 4, 6, 1, 0, 0, TimeSpan.Zero) },
        { "Pacific/Auckland", new DateTimeOffset(2026, 9, 28, 1, 0, 0, TimeSpan.Zero) },
    };

    private static DemoClock ClockFor(string zone, DateTimeOffset instant)
    {
        var time = new FakeTimeProvider(instant);
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        return new DemoClock(time, TimeZoneInfo.FindSystemTimeZoneById(zone));
    }

    private static List<DemoTimeStep> Timeline(DemoClock clock, int days = Days) => new DemoDataGenerator(
            Options.Create(new DemoModeConfiguration { BackfillDays = days, RandomSeed = 7 }),
            clock,
            NullLogger<DemoDataGenerator>.Instance,
            NullLoggerFactory.Instance)
        .GenerateHistoricalTimeline()
        .ToList();

    [Theory]
    [MemberData(nameof(Transitions))]
    public void The_steps_walk_every_five_minutes_through_a_transition(string zone, DateTimeOffset instant)
    {
        var clock = ClockFor(zone, instant);

        var steps = Timeline(clock);

        steps.Zip(steps.Skip(1), (a, b) => b.Utc - a.Utc)
            .Should().OnlyContain(gap => gap == TimeSpan.FromMinutes(5), "no reading is repeated or skipped");
        steps.Select(s => s.Entry.Mills).Should().OnlyHaveUniqueItems();
        steps.Should().OnlyContain(s => s.Time == clock.ToLocal(s.Utc), "the wall-clock time is read from the instant");
        var offsets = steps.Select(s => clock.Zone.GetUtcOffset(s.Utc)).Distinct().ToList();
        offsets.Should().HaveCount(2, "the window crosses the transition");
    }

    [Theory]
    [MemberData(nameof(Transitions))]
    public void Device_statuses_across_a_transition_have_unique_legacy_ids(string zone, DateTimeOffset instant)
    {
        var clock = ClockFor(zone, instant);

        var ids = Timeline(clock)
            .Select(step => DemoDeviceStatusGenerator.ForStep(step, clock, 110, 1.0))
            .OfType<DeviceStatus>()
            .Select(status => status.Mills.ToString("x24"))
            .ToList();

        ids.Should().NotBeEmpty();
        ids.Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [MemberData(nameof(Transitions))]
    public void Heart_rate_on_a_transition_day_is_unique_in_time_and_sync_id(string zone, DateTimeOffset instant)
    {
        var clock = ClockFor(zone, instant);
        var transitionDay = clock.ToLocal(instant.UtcDateTime).Date.AddDays(-1);

        var (heartRates, _) = DemoHealthDataGenerator.GenerateDailyActivity(transitionDay, "dev-sample", clock);

        heartRates.Select(h => h.Timestamp).Should().OnlyHaveUniqueItems();
        heartRates.Select(h => h.SyncIdentifier).Should().OnlyHaveUniqueItems();
        heartRates.Zip(heartRates.Skip(1), (a, b) => b.Timestamp - a.Timestamp)
            .Should().OnlyContain(gap => gap == TimeSpan.FromMinutes(5));
        heartRates.Should().HaveCountGreaterThan(0).And.NotHaveCount(288, "the day is 23 or 25 hours long");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_timeline_ending_in_the_daylight_half_of_a_fall_back_hour_writes_nothing_after_now(bool passTheEnd)
    {
        // 05:30 UTC on 1 November 2026 is 01:30 EDT, the first of New York's two 01:30s.
        var now = new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero);
        var clock = ClockFor("America/New_York", now);
        var generator = new DemoDataGenerator(
            Options.Create(new DemoModeConfiguration { BackfillDays = 1, RandomSeed = 7 }),
            clock,
            NullLogger<DemoDataGenerator>.Instance,
            NullLoggerFactory.Instance);

        var steps = (passTheEnd ? generator.GenerateHistoricalTimeline(now.UtcDateTime) : generator.GenerateHistoricalTimeline())
            .ToList();

        steps.Should().NotBeEmpty();
        steps.Should().OnlyContain(s => s.Utc <= now.UtcDateTime, "no reading is generated after now");
        steps[^1].Utc.Should().BeOnOrAfter(now.UtcDateTime.AddMinutes(-5), "the timeline runs up to now");
    }

    [Fact]
    public void A_repeated_fall_back_hour_keeps_the_standard_occurrence_on_the_plain_wall_clock_key()
    {
        var clock = ClockFor("America/New_York", new DateTimeOffset(2026, 11, 2, 17, 0, 0, TimeSpan.Zero));

        var (heartRates, _) = DemoHealthDataGenerator.GenerateDailyActivity(new DateTime(2026, 11, 1), "dev-sample", clock);

        var oneThirty = heartRates.Where(h => clock.ToLocal(h.Timestamp) == new DateTime(2026, 11, 1, 1, 30, 0)).ToList();
        oneThirty.Should().HaveCount(2);
        oneThirty.Single(h => h.Timestamp == new DateTime(2026, 11, 1, 6, 30, 0, DateTimeKind.Utc))
            .SyncIdentifier.Should().Be("dev-sample:hr:20261101T013000", "an earlier seed wrote 01:30 at the standard offset under this key");
        oneThirty.Single(h => h.Timestamp == new DateTime(2026, 11, 1, 5, 30, 0, DateTimeKind.Utc))
            .SyncIdentifier.Should().NotBe("dev-sample:hr:20261101T013000");
    }

    [Fact]
    public void A_zone_without_dst_only_shifts_the_same_day_by_its_offset()
    {
        var instant = new DateTimeOffset(2026, 3, 14, 20, 0, 0, TimeSpan.Zero);
        var brisbane = ClockFor("Australia/Brisbane", instant.AddHours(-10));
        var utc = ClockFor("UTC", instant);

        var shifted = Timeline(brisbane);
        var plain = Timeline(utc);

        shifted.Select(s => s.Time).Should().Equal(plain.Select(s => s.Time));
        shifted.Select(s => s.Entry.Sgv).Should().Equal(plain.Select(s => s.Entry.Sgv));
        shifted.Select(s => s.Entry.Mills + (long)TimeSpan.FromHours(10).TotalMilliseconds)
            .Should().Equal(plain.Select(s => s.Entry.Mills));
    }
}
