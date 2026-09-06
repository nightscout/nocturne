using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.Connectors.Nightscout.Services.WriteBack;
using Xunit;

namespace Nocturne.Connectors.Nightscout.Tests.Services.WriteBack;

/// <summary>
/// Pins the breaker that guards a tenant's legacy Nightscout instance during a live
/// cutover. Too lax and write-back floods an instance that is already failing; too
/// strict and the tenant's old instance silently stops receiving data mid-migration.
/// Thresholds and the recovery window are asserted as literals so that changing the
/// constants fails here rather than silently changing production behaviour.
/// </summary>
[Trait("Category", "Unit")]
public class NightscoutCircuitBreakerTests
{
    private const int FailureThreshold = 5;
    private static readonly TimeSpan RecoveryTimeout = TimeSpan.FromSeconds(60);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private NightscoutCircuitBreaker CreateBreaker() => new(_time);

    private static void Fail(NightscoutCircuitBreaker breaker, int times)
    {
        for (var i = 0; i < times; i++)
            breaker.RecordFailure();
    }

    [Fact]
    public void IsOpen_IsFalse_WhenNothingHasFailed()
    {
        CreateBreaker().IsOpen.Should().BeFalse();
    }

    [Fact]
    public void IsOpen_IsFalse_OneFailureBelowThreshold()
    {
        var sut = CreateBreaker();

        Fail(sut, FailureThreshold - 1);

        sut.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void IsOpen_IsTrue_AtExactlyThreshold()
    {
        var sut = CreateBreaker();

        Fail(sut, FailureThreshold);

        sut.IsOpen.Should().BeTrue();
    }

    [Fact]
    public void IsOpen_IsTrue_OneTickBeforeRecoveryTimeoutElapses()
    {
        var sut = CreateBreaker();
        Fail(sut, FailureThreshold);

        _time.Advance(RecoveryTimeout - TimeSpan.FromTicks(1));

        sut.IsOpen.Should().BeTrue();
    }

    [Fact]
    public void IsOpen_IsFalse_AtExactlyRecoveryTimeout()
    {
        var sut = CreateBreaker();
        Fail(sut, FailureThreshold);

        _time.Advance(RecoveryTimeout);

        sut.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void IsOpen_IsFalse_AfterRecoveryTimeoutElapses()
    {
        var sut = CreateBreaker();
        Fail(sut, FailureThreshold);

        _time.Advance(RecoveryTimeout + TimeSpan.FromSeconds(1));

        sut.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void RecordSuccess_ClosesTheBreakerImmediately()
    {
        var sut = CreateBreaker();
        Fail(sut, FailureThreshold);

        sut.RecordSuccess();

        sut.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void RecordSuccess_ResetsTheConsecutiveFailureCount()
    {
        var sut = CreateBreaker();
        Fail(sut, FailureThreshold);
        sut.RecordSuccess();

        // If the count had merely been masked by the time window, one more failure
        // would re-open the breaker. The full threshold must be reached again.
        Fail(sut, FailureThreshold - 1);
        sut.IsOpen.Should().BeFalse();

        sut.RecordFailure();
        sut.IsOpen.Should().BeTrue();
    }

    [Fact]
    public void HalfOpenProbeFailure_ReopensForAnotherFullRecoveryWindow()
    {
        var sut = CreateBreaker();
        Fail(sut, FailureThreshold);
        _time.Advance(RecoveryTimeout);
        sut.IsOpen.Should().BeFalse();

        // The failure count is never decayed, so a single failed probe re-opens.
        sut.RecordFailure();
        sut.IsOpen.Should().BeTrue();

        _time.Advance(RecoveryTimeout - TimeSpan.FromTicks(1));
        sut.IsOpen.Should().BeTrue();

        _time.Advance(TimeSpan.FromTicks(1));
        sut.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void RecordFailure_WhileOpen_ExtendsTheOpenWindowFromTheLatestFailure()
    {
        var sut = CreateBreaker();
        Fail(sut, FailureThreshold);

        _time.Advance(TimeSpan.FromSeconds(59));
        sut.RecordFailure();

        // 60.5s after the breaker first opened, but only 1.5s after the newest failure.
        _time.Advance(TimeSpan.FromSeconds(1.5));

        sut.IsOpen.Should().BeTrue();
    }

    [Fact]
    public void HalfOpen_DoesNotRationProbes_EveryCallerIsLetThrough()
    {
        var sut = CreateBreaker();
        Fail(sut, FailureThreshold);
        _time.Advance(RecoveryTimeout);

        for (var i = 0; i < 50; i++)
            sut.IsOpen.Should().BeFalse(
                "this pins CURRENT behaviour, not desired behaviour: IsOpen is a pure "
                + "read, so nothing rations the half-open state to a single probe and "
                + "every concurrent write-back is released at once when the window "
                + "expires. Invert this if probe admission is ever added");
    }

    [Fact]
    public void DefaultConstructor_UsesTheSystemClock_AndStaysClosedUntilThreshold()
    {
        var sut = new NightscoutCircuitBreaker();

        Fail(sut, FailureThreshold - 1);
        sut.IsOpen.Should().BeFalse();

        sut.RecordFailure();
        sut.IsOpen.Should().BeTrue();
    }
}
