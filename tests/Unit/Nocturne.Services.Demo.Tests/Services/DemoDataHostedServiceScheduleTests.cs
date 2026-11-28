using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.Services.Demo.Configuration;
using Nocturne.Services.Demo.Services;
using Xunit;

namespace Nocturne.Services.Demo.Tests.Services;

public class DemoDataHostedServiceScheduleTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Waits_until_the_next_generation_when_no_reset_is_scheduled()
    {
        var delay = DemoDataHostedService.DelayUntilNextWake(
            Start.UtcDateTime,
            Start.UtcDateTime.AddMinutes(5),
            nextResetUtc: null,
            nextVisitorResetUtc: null);

        delay.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void Wakes_for_a_reset_that_falls_before_the_next_generation()
    {
        var delay = DemoDataHostedService.DelayUntilNextWake(
            Start.UtcDateTime,
            Start.UtcDateTime.AddMinutes(5),
            Start.UtcDateTime.AddMinutes(2),
            nextVisitorResetUtc: null);

        delay.Should().Be(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void Does_not_wait_when_the_wake_time_has_passed()
    {
        var delay = DemoDataHostedService.DelayUntilNextWake(
            Start.UtcDateTime.AddMinutes(10),
            Start.UtcDateTime.AddMinutes(5),
            nextResetUtc: null,
            nextVisitorResetUtc: null);

        delay.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Delay_follows_the_clock_as_it_advances()
    {
        var clock = new FakeTimeProvider(Start);
        var nextGeneration = clock.GetUtcNow().UtcDateTime.AddMinutes(5);

        clock.Advance(TimeSpan.FromMinutes(3));

        DemoDataHostedService
            .DelayUntilNextWake(clock.GetUtcNow().UtcDateTime, nextGeneration, nextResetUtc: null, nextVisitorResetUtc: null)
            .Should().Be(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void Wakes_for_a_visitor_reset_that_falls_before_the_next_generation_and_reset()
    {
        var delay = DemoDataHostedService.DelayUntilNextWake(
            Start.UtcDateTime,
            Start.UtcDateTime.AddMinutes(5),
            Start.UtcDateTime.AddMinutes(4),
            Start.UtcDateTime.AddMinutes(1));

        delay.Should().Be(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void A_recorded_visitor_reset_is_kept_across_a_restart()
    {
        var recorded = Start.UtcDateTime.AddMinutes(-10);

        DemoDataHostedService
            .NextVisitorResetDue(new DemoModeConfiguration { VisitorResetIntervalMinutes = 60 }, recorded, Start.UtcDateTime)
            .Should().Be(recorded);
    }

    [Fact]
    public void With_no_recorded_visitor_reset_the_first_is_one_interval_away()
    {
        DemoDataHostedService
            .NextVisitorResetDue(new DemoModeConfiguration { VisitorResetIntervalMinutes = 60 }, null, Start.UtcDateTime)
            .Should().Be(Start.UtcDateTime.AddMinutes(60));
    }

    [Fact]
    public void Visitor_resets_are_off_at_a_zero_interval()
    {
        DemoDataHostedService
            .NextVisitorResetDue(new DemoModeConfiguration { VisitorResetIntervalMinutes = 0 }, Start.UtcDateTime, Start.UtcDateTime)
            .Should().BeNull();
    }
}
