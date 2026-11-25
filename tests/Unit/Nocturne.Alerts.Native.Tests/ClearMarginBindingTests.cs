using System.Text.Json;
using FluentAssertions;
using Nocturne.Alerts.Corpus;
using Nocturne.Core.Alerts.Native;
using Xunit;

namespace Nocturne.Alerts.Native.Tests;

/// <summary>
/// A leaf's clear margin crosses the binding as a member of the stored condition, and "open" is
/// read from the tracker state the host already sends (docs/alerts/engine-semantics.md §3).
/// </summary>
public class ClearMarginBindingTests
{
    private static readonly DateTime Now = new(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc);

    private static bool? RootAt(decimal glucose, object conditionParams, RustTrackerState? tracker, string kind = "threshold")
    {
        var response = RustAlertEngine.Evaluate(
            new RustAlertRule
            {
                Id = Guid.Empty,
                ConditionType = kind,
                ConditionParams = JsonSerializer.SerializeToElement(conditionParams),
            },
            JsonSerializer.SerializeToElement(new
            {
                latest_value = glucose,
                latest_timestamp = Now,
                last_reading_at = Now,
            }),
            Now,
            tracker: tracker);
        return RustAlertEngine.GetRuleResult(response).Root;
    }

    private static readonly object Low = new { direction = "below", value = 70, clear_margin = 5 };

    private static readonly RustTrackerState Open = new()
    {
        State = "active",
        ActiveExcursionOrdinal = 1,
        LastExcursionOrdinal = 1,
        UpdatedAt = Now.AddMinutes(-5),
    };

    private static readonly RustTrackerState Idle = new()
    {
        State = "idle",
        LastExcursionOrdinal = 1,
        UpdatedAt = Now.AddMinutes(-5),
    };

    [NativeFact]
    public void An_open_tracker_widens_the_limit_and_a_closed_one_does_not()
    {
        RootAt(72m, Low, Open).Should().BeTrue();
        RootAt(75m, Low, Open).Should().BeFalse();
        RootAt(72m, Low, Idle).Should().BeFalse();
        RootAt(72m, Low, null).Should().BeFalse();
        RootAt(69m, Low, null).Should().BeTrue();
    }

    [NativeFact]
    public void Under_not_the_margin_moves_the_limit_to_the_leafs_false_side()
    {
        var notLow = new { child = new { type = "threshold", threshold = Low } };
        RootAt(66m, notLow, Open, "not").Should().BeTrue();
        RootAt(64m, notLow, Open, "not").Should().BeFalse();
        RootAt(66m, notLow, Idle, "not").Should().BeFalse();
    }

    [NativeFact]
    public void A_zero_margin_reads_as_none()
    {
        var zero = new { direction = "below", value = 70, clear_margin = 0 };
        var none = new { direction = "below", value = 70 };
        foreach (var glucose in new[] { 69m, 70m, 72m })
            RootAt(glucose, zero, Open).Should().Be(RootAt(glucose, none, Open));
    }

    [NativeFact]
    public void An_invalid_margin_is_an_unevaluable_body()
    {
        var act = () => RootAt(72m, new { direction = "below", value = 70, clear_margin = -1 }, Open);
        act.Should().Throw<RustAlertEngineException>().WithMessage("*margin_negative*clear_margin*");
    }
}
