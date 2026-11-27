using System.Text.Json;
using FluentAssertions;
using Nocturne.Alerts.Corpus;
using Nocturne.Core.Alerts.Native;
using Xunit;

namespace Nocturne.Alerts.Native.Tests;

/// <summary>
/// The evaluate envelope's <c>clock_anomaly</c> (docs/alerts/engine-semantics.md §5.4) reaches the
/// binding, and is absent for an anchor within the 5-minute tolerance.
/// </summary>
public class ClockAnomalyBindingTests
{
    private static readonly DateTime Now = new(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc);

    private static RustRuleResult SignalLoss(DateTime lastReadingAt) =>
        RustAlertEngine.GetRuleResult(RustAlertEngine.Evaluate(
            new RustAlertRule
            {
                Id = Guid.Empty,
                ConditionType = "signal_loss",
                ConditionParams = JsonSerializer.SerializeToElement(new { schema_version = 2, timeout_minutes = 20 }),
            },
            JsonSerializer.SerializeToElement(new { last_reading_at = lastReadingAt }),
            Now));

    [NativeFact]
    public void The_library_exports_the_tolerance_a_host_selects_readings_by()
    {
        RustAlertEngine.ClockTolerance().Should().Be(TimeSpan.FromMinutes(5));
    }

    [NativeFact]
    public void A_reading_dated_an_hour_ahead_is_unknown_and_reported()
    {
        var result = SignalLoss(Now.AddHours(1));

        result.Unknown.Should().BeTrue();
        result.ClockAnomaly.Should().Equal(new RustClockAnomaly("last_reading_at", 3_600_000));
        result.NextChangeAt.Should().Be(Now.AddMinutes(55));
    }

    [NativeFact]
    public void A_reading_exactly_five_minutes_ahead_is_within_the_tolerance()
    {
        var result = SignalLoss(Now.AddMinutes(5));

        result.Unknown.Should().BeFalse();
        result.Root.Should().BeFalse();
        result.ClockAnomaly.Should().BeNull();
    }
}
