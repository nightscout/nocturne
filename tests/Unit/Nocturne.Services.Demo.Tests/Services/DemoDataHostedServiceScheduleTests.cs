using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
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
            nextResetUtc: null);

        delay.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void Wakes_for_a_reset_that_falls_before_the_next_generation()
    {
        var delay = DemoDataHostedService.DelayUntilNextWake(
            Start.UtcDateTime,
            Start.UtcDateTime.AddMinutes(5),
            Start.UtcDateTime.AddMinutes(2));

        delay.Should().Be(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void Does_not_wait_when_the_wake_time_has_passed()
    {
        var delay = DemoDataHostedService.DelayUntilNextWake(
            Start.UtcDateTime.AddMinutes(10),
            Start.UtcDateTime.AddMinutes(5),
            nextResetUtc: null);

        delay.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Delay_follows_the_clock_as_it_advances()
    {
        var clock = new FakeTimeProvider(Start);
        var nextGeneration = clock.GetUtcNow().UtcDateTime.AddMinutes(5);

        clock.Advance(TimeSpan.FromMinutes(3));

        DemoDataHostedService
            .DelayUntilNextWake(clock.GetUtcNow().UtcDateTime, nextGeneration, nextResetUtc: null)
            .Should().Be(TimeSpan.FromMinutes(2));
    }
}
