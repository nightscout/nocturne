using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.Alerts.Corpus;
using Nocturne.API.Multitenancy;
using Microsoft.Extensions.Logging;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.API.Services.Audit;
using Nocturne.API.Tests.Services.Alerts.Engines;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// Rules driven only by <see cref="AlertSweepJob"/> runs, through the real orchestrator and
/// evaluation engine over in-memory tracker state.
/// </summary>
[Trait("Category", "Unit")]
public class AlertSweepJobWallClockTests
{
    private static readonly Guid Tenant = Guid.Parse("00000000-0000-0000-0003-000000000001");
    private static readonly Guid RuleId = Guid.Parse("00000000-0000-0000-0003-000000000002");
    private static readonly DateTime T0 = new(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc);

    private const string SignalLoss15 = """{"timeout_minutes": 15}""";

    [NativeFact]
    public async Task Tenant_with_no_reading_history_does_not_fire()
    {
        var fixture = new Fixture(AlertConditionType.SignalLoss, SignalLoss15)
        {
            LastReadingAt = null,
        };

        await fixture.SweepAt(T0.AddHours(6));

        fixture.Dispatches.Should().Be(0);
        fixture.InstancesCreated.Should().Be(0);
    }

    [NativeFact]
    public async Task Reading_driven_leaves_keep_the_verdict_of_the_last_reading()
    {
        // Opened by a low reading. The sweep re-judges that same reading, so it must not feed
        // false and start hysteresis.
        const string lowOrLoss = """
            {"operator": "or", "conditions": [
              {"type": "threshold", "threshold": {"direction": "below", "value": 70}},
              {"type": "signal_loss", "signal_loss": {"timeout_minutes": 15}}
            ]}
            """;
        var fixture = new Fixture(AlertConditionType.Composite, lowOrLoss)
        {
            LastReadingAt = T0,
            LatestMgdl = 60,
        };

        await fixture.SweepAt(T0.AddMinutes(1));
        fixture.InstancesCreated.Should().Be(1);

        await fixture.SweepAt(T0.AddMinutes(2));
        await fixture.SweepAt(T0.AddMinutes(4));
        (await fixture.TrackerRepo.GetTrackerStateAsync(RuleId))!.State.Should().Be("active");
        fixture.Closed.Should().BeEmpty();
    }

    [NativeFact]
    public async Task A_rate_leaf_judges_the_rate_derived_from_the_previous_reading()
    {
        const string fallOrLoss = """
            {"operator": "or", "conditions": [
              {"type": "rate_of_change", "rate_of_change": {"direction": "falling", "rate": 3}},
              {"type": "signal_loss", "signal_loss": {"timeout_minutes": 15}}
            ]}
            """;
        var fixture = new Fixture(AlertConditionType.Composite, fallOrLoss);
        fixture.Readings.Add(new SensorGlucose { Id = Guid.NewGuid(), Timestamp = T0.AddMinutes(-5), Mgdl = 120 });
        fixture.Readings.Add(new SensorGlucose { Id = Guid.NewGuid(), Timestamp = T0, Mgdl = 100 });
        fixture.LastReadingAt = T0;

        await fixture.SweepAt(T0.AddMinutes(1));

        fixture.InstancesCreated.Should().Be(1, "the derived rate is -4 mg/dL/min");
    }

    [NativeFact]
    public async Task Rule_without_a_wall_clock_leaf_is_not_evaluated()
    {
        const string nightLow = """
            {"operator": "and", "conditions": [
              {"type": "threshold", "threshold": {"direction": "below", "value": 70}},
              {"type": "time_of_day", "time_of_day": {"from": "22:00", "to": "06:00", "timezone": null}}
            ]}
            """;
        var fixture = new Fixture(AlertConditionType.Composite, nightLow)
        {
            LastReadingAt = T0,
            LatestMgdl = 60,
        };

        await fixture.SweepAt(T0.AddHours(11));

        fixture.InstancesCreated.Should().Be(0);
        (await fixture.TrackerRepo.GetTrackerStateAsync(RuleId)).Should().BeNull();
    }

    [NativeFact]
    public async Task Measures_signal_loss_from_the_last_usable_reading()
    {
        var fixture = new Fixture(AlertConditionType.SignalLoss, SignalLoss15);
        fixture.Readings.Add(new SensorGlucose { Timestamp = T0, Mgdl = 110 });
        for (var minutes = 5; minutes <= 20; minutes += 5)
            fixture.Readings.Add(new SensorGlucose { Timestamp = T0.AddMinutes(minutes), Mgdl = 0 });
        fixture.LastReadingAt = T0.AddMinutes(20);

        await fixture.SweepAt(T0.AddMinutes(14));
        fixture.InstancesCreated.Should().Be(0);

        await fixture.SweepAt(T0.AddMinutes(20.5));
        fixture.InstancesCreated.Should().Be(1, "error readings are not signal: the last usable one is 20 minutes old");
    }

    /// <summary>
    /// A usable reading dated far ahead, behind a selected reading with no value: the lookback
    /// reads only up to the selected reading, so signal loss counts from the last real usable
    /// reading and is not held by the future one.
    /// </summary>
    [NativeFact]
    public async Task The_lookback_behind_an_unusable_reading_skips_a_usable_one_dated_ahead()
    {
        var fixture = new Fixture(AlertConditionType.SignalLoss, SignalLoss15);
        fixture.Readings.Add(new SensorGlucose { Timestamp = T0, Mgdl = 110 });
        fixture.Readings.Add(new SensorGlucose { Timestamp = T0.AddMinutes(5), Mgdl = 0 });
        fixture.Readings.Add(new SensorGlucose { Timestamp = T0.AddHours(2), Mgdl = 110 });

        await fixture.SweepAt(T0.AddMinutes(20.5));

        fixture.InstancesCreated.Should().Be(1, "the last real usable reading is 20 minutes old");
    }

    [NativeFact]
    public async Task A_failing_clock_notice_does_not_stop_the_wall_clock_pass()
    {
        var fixture = new Fixture(AlertConditionType.SignalLoss, SignalLoss15);
        fixture.Readings.Add(new SensorGlucose { Timestamp = T0, Mgdl = 110 });
        fixture.ClockNotice
            .Setup(n => n.ReportReadingAsync(It.IsAny<AlertReadingSelection>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("lock timeout"));

        await fixture.SweepAt(T0.AddMinutes(20));

        fixture.InstancesCreated.Should().Be(1);
    }

    [NativeFact]
    public async Task The_lookback_for_a_usable_reading_runs_once_per_newest_reading()
    {
        var fixture = new Fixture(AlertConditionType.SignalLoss, SignalLoss15);
        fixture.Readings.Add(new SensorGlucose { Timestamp = T0, Mgdl = 110 });
        fixture.Readings.Add(new SensorGlucose { Timestamp = T0.AddMinutes(5), Mgdl = 0 });
        fixture.LastReadingAt = T0.AddMinutes(5);

        await fixture.SweepAt(T0.AddMinutes(6));
        await fixture.SweepAt(T0.AddMinutes(6.5));
        await fixture.SweepAt(T0.AddMinutes(7));
        fixture.Canonical.Verify(
            c => c.GetRecentAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);

        fixture.Readings.Add(new SensorGlucose { Timestamp = T0.AddMinutes(10), Mgdl = 0 });
        await fixture.SweepAt(T0.AddMinutes(15.5));
        fixture.Canonical.Verify(
            c => c.GetRecentAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        fixture.InstancesCreated.Should().Be(1, "the last usable reading is still the one at T0");
    }

    [NativeFact]
    public async Task An_outage_with_no_usable_reading_in_the_lookback_still_fires()
    {
        var fixture = new Fixture(AlertConditionType.SignalLoss, SignalLoss15);
        for (var minutes = 0; minutes <= 60; minutes += 5)
            fixture.Readings.Add(new SensorGlucose { Timestamp = T0.AddMinutes(minutes), Mgdl = 0 });
        fixture.LastReadingAt = T0.AddMinutes(60);

        await fixture.SweepAt(T0.AddMinutes(60.5));

        fixture.InstancesCreated.Should().Be(1);
    }

    /// <summary>
    /// A reading-driven body is swept once an enabled auto-resolve tree holds a wall-clock kind
    /// (docs/alerts/engine-semantics.md §5.1), and the sweep's kept verdict follows the edit.
    /// </summary>
    [NativeFact]
    public async Task A_wall_clock_auto_resolve_tree_puts_the_rule_on_the_sweep_once_enabled()
    {
        var fixture = new Fixture(AlertConditionType.Threshold, """{"direction": "below", "value": 70}""")
        {
            LastReadingAt = T0,
            LatestMgdl = 50,
        };
        const string stale = """{"type": "staleness", "staleness": {"operator": ">", "value": 20}}""";
        fixture.Rules[0] = fixture.Rules[0] with { AutoResolveEnabled = false, AutoResolveParams = stale };

        await fixture.SweepAt(T0.AddMinutes(1));
        fixture.InstancesCreated.Should().Be(0, "a threshold rule is evaluated per reading only");

        fixture.Rules[0] = fixture.Rules[0] with { AutoResolveEnabled = true };
        await fixture.SweepAt(T0.AddMinutes(2));
        fixture.InstancesCreated.Should().Be(1);
    }

    /// <summary>
    /// A threshold rule is swept once its <c>on_unknown</c> reads unknown as known, since its leaf
    /// turns unknown when the reading goes stale (docs/alerts/engine-semantics.md §5.1); the kept
    /// verdict follows an edit of that word alone.
    /// </summary>
    [NativeFact]
    public async Task An_edit_of_on_unknown_alone_moves_the_rule_onto_the_sweep()
    {
        var fixture = new Fixture(AlertConditionType.Threshold, """{"direction": "below", "value": 70}""")
        {
            LastReadingAt = T0,
            LatestMgdl = 50,
        };

        await fixture.SweepAt(T0.AddMinutes(1));
        fixture.InstancesCreated.Should().Be(0, "under hold a threshold rule is evaluated per reading only");

        await fixture.SetOnUnknown(AlertRuleOnUnknown.NotMet);
        await fixture.SweepAt(T0.AddMinutes(2));
        fixture.InstancesCreated.Should().Be(1);
    }

    /// <summary>
    /// A reading's age runs from when it was stored (docs/alerts/engine-semantics.md §3): one
    /// that arrives 20 minutes after its timestamp is fresh on arrival, and stale one bound later.
    /// </summary>
    [NativeFact]
    public async Task A_late_reading_is_fresh_on_arrival_and_stale_one_bound_later()
    {
        const string lowOrLoss = """
            {"operator": "or", "conditions": [
              {"type": "threshold", "threshold": {"direction": "below", "value": 70}},
              {"type": "signal_loss", "signal_loss": {"timeout_minutes": 60}}
            ]}
            """;
        var fixture = new Fixture(AlertConditionType.Composite, lowOrLoss);
        fixture.Readings.Add(new SensorGlucose { Timestamp = T0, CreatedAt = T0.AddMinutes(20), Mgdl = 60 });
        fixture.LastReadingAt = T0;
        await fixture.SetOnUnknown(AlertRuleOnUnknown.NotMet);

        await fixture.SweepAt(T0.AddMinutes(21));
        fixture.InstancesCreated.Should().Be(1, "the reading arrived a minute ago");

        await fixture.SweepAt(T0.AddMinutes(32));
        (await fixture.TrackerRepo.GetTrackerStateAsync(RuleId))!.State.Should().Be("hysteresis");
    }

    /// <summary>
    /// Past the freshness bound the sweep's reading-driven leaves read unknown
    /// (docs/alerts/engine-semantics.md §3): under hold the excursion they opened stays open,
    /// under not_met it enters hysteresis.
    /// </summary>
    [NativeTheory]
    [InlineData(AlertRuleOnUnknown.Hold, "active")]
    [InlineData(AlertRuleOnUnknown.NotMet, "hysteresis")]
    public async Task A_stale_reading_holds_or_releases_the_excursion_it_opened(string onUnknown, string state)
    {
        const string lowOrLoss = """
            {"operator": "or", "conditions": [
              {"type": "threshold", "threshold": {"direction": "below", "value": 70}},
              {"type": "signal_loss", "signal_loss": {"timeout_minutes": 60}}
            ]}
            """;
        var fixture = new Fixture(AlertConditionType.Composite, lowOrLoss)
        {
            LastReadingAt = T0,
            LatestMgdl = 60,
        };
        await fixture.SetOnUnknown(onUnknown);

        await fixture.SweepAt(T0.AddMinutes(1));
        fixture.InstancesCreated.Should().Be(1);

        await fixture.SweepAt(T0.AddMinutes(20));
        (await fixture.TrackerRepo.GetTrackerStateAsync(RuleId))!.State.Should().Be(state);
    }

    /// <summary>
    /// The engine reads a body it cannot evaluate as its root kind alone, so a wall-clock leaf
    /// inside it does not put the rule on the sweep; the per-reading pass reports the fault.
    /// </summary>
    [NativeFact]
    public async Task An_unevaluable_tree_is_not_swept()
    {
        const string lossAndBroken = """
            {"operator": "and", "conditions": [
              {"type": "signal_loss", "signal_loss": {"timeout_minutes": 15}},
              {"type": "composite"}
            ]}
            """;
        var fixture = new Fixture(AlertConditionType.Composite, lossAndBroken)
        {
            LastReadingAt = T0,
        };

        await fixture.SweepAt(T0.AddMinutes(1));
        await fixture.SweepAt(T0.AddMinutes(2));
        await fixture.SweepAt(T0.AddMinutes(3));

        fixture.OrchestratorLogger.Entries.Should().BeEmpty();
        fixture.InstancesCreated.Should().Be(0);
    }

    [NativeFact]
    public async Task An_unwalkable_tree_is_reported_once_per_version()
    {
        var unwalkable = new AlertRule
        {
            Id = Guid.Parse("00000000-0000-0000-0003-0000000000b1"),
            Name = "Unwalkable",
            ConditionType = AlertConditionType.Composite,
            ConditionParams = null!,
        };
        var fixture = new Fixture(AlertConditionType.SignalLoss, SignalLoss15, unwalkable)
        {
            LastReadingAt = T0,
        };

        await fixture.SweepAt(T0.AddMinutes(1));
        await fixture.SweepAt(T0.AddMinutes(2));
        await fixture.SweepAt(T0.AddMinutes(3));

        fixture.Logger.Warnings.Should().ContainSingle(w => w.Contains(unwalkable.Id.ToString()));
    }

    [NativeFact]
    public async Task Escalates_a_child_of_a_reading_driven_parent()
    {
        // The parent is not wall-clock, so the sweep evaluates only the child; the child's
        // reference must still resolve against every enabled rule of the tenant.
        var parentId = Guid.Parse("00000000-0000-0000-0003-0000000000a1");
        var parent = new AlertRule
        {
            Id = parentId,
            Name = "Low",
            ConditionType = AlertConditionType.Threshold,
            ConditionParams = """{"direction": "below", "value": 70}""",
        };
        var fixture = new Fixture(
            AlertConditionType.AlertState,
            $$"""{"alert_id": "{{parentId}}", "state": "unacknowledged", "for_minutes": 15}""",
            parent)
        {
            LastReadingAt = T0,
            LatestMgdl = 60,
        };
        fixture.ActiveAlerts[parentId] = new ActiveAlertSnapshot("firing", T0, null);

        await fixture.SweepAt(T0.AddMinutes(10));
        fixture.InstancesCreated.Should().Be(0, "the parent has been unacknowledged for 10 minutes");

        await fixture.SweepAt(T0.AddMinutes(15));
        fixture.InstancesCreated.Should().Be(1);
        fixture.LastPayload!.AlertType.Should().Be(AlertConditionType.AlertState);
        (await fixture.TrackerRepo.GetTrackerStateAsync(parentId)).Should().BeNull(
            "the reading-driven parent is not the sweep's to evaluate");
    }

    [NativeFact]
    public async Task Signal_loss_opens_dispatches_once_and_closes_on_resume()
    {
        var fixture = new Fixture(AlertConditionType.SignalLoss, SignalLoss15)
        {
            LastReadingAt = T0,
        };

        await fixture.SweepAt(T0.AddMinutes(14.5));
        fixture.InstancesCreated.Should().Be(0, "the 15-minute timeout has not elapsed");

        await fixture.SweepAt(T0.AddMinutes(15));
        fixture.InstancesCreated.Should().Be(1);
        fixture.Dispatches.Should().Be(1);
        fixture.LastPayload!.AlertType.Should().Be(AlertConditionType.SignalLoss);

        await fixture.SweepAt(T0.AddMinutes(15.5));
        await fixture.SweepAt(T0.AddMinutes(16));
        await fixture.SweepAt(T0.AddMinutes(45));
        fixture.InstancesCreated.Should().Be(1, "a continuing outage is one excursion");
        fixture.Dispatches.Should().Be(1, "a continuing outage must not re-dispatch every sweep");

        await fixture.ReadingAt(T0.AddMinutes(46));
        fixture.Closed.Should().BeEmpty("with hysteresis the first false evaluation only starts the window");

        await fixture.SweepAt(T0.AddMinutes(46.5));
        fixture.Closed.Should().ContainSingle()
            .Which.Type.Should().Be(ExcursionTransitionType.ExcursionClosed);
        (await fixture.TrackerRepo.GetTrackerStateAsync(RuleId))!.State.Should().Be("idle");
        fixture.Dispatches.Should().Be(1);
    }

    [NativeFact]
    public async Task Fires_signal_loss_nested_in_a_night_window()
    {
        const string nightLoss = """
            {"operator": "and", "conditions": [
              {"type": "signal_loss", "signal_loss": {"timeout_minutes": 15}},
              {"type": "time_of_day", "time_of_day": {"from": "22:00", "to": "06:00", "timezone": null}}
            ]}
            """;
        var fixture = new Fixture(AlertConditionType.Composite, nightLoss)
        {
            LastReadingAt = T0.AddHours(9),
            LatestMgdl = 110,
        };

        await fixture.SweepAt(T0.AddHours(9).AddMinutes(20));
        fixture.InstancesCreated.Should().Be(0, "the outage is 20 minutes old but it is 21:20");

        await fixture.SweepAt(T0.AddHours(10));
        fixture.InstancesCreated.Should().Be(1, "at 22:00 the window opens on an outage already past 15 minutes");
        fixture.Dispatches.Should().Be(1);
        fixture.LastPayload!.AlertType.Should().Be(AlertConditionType.Composite);

        await fixture.SweepAt(T0.AddHours(10).AddMinutes(30));
        fixture.Dispatches.Should().Be(1, "a continuing outage must not re-dispatch every sweep");
    }

    [NativeFact]
    public async Task Signal_loss_is_not_evaluated_before_its_next_change_and_is_at_it()
    {
        var fixture = new Fixture(AlertConditionType.SignalLoss, SignalLoss15) { LastReadingAt = T0 };

        await fixture.SweepAt(T0.AddMinutes(1));
        fixture.Evaluations.Should().Be(1);
        (await fixture.TrackerRepo.GetTrackerStateAsync(RuleId)).Should().BeEquivalentTo(
            new { NextChangeKnown = true, NextChangeAt = (DateTime?)T0.AddMinutes(15) });

        await fixture.SweepAt(T0.AddMinutes(5));
        await fixture.SweepAt(T0.AddMinutes(15).AddTicks(-1));
        fixture.Evaluations.Should().Be(1, "nothing but the clock moves the rule before its next change");

        await fixture.SweepAt(T0.AddMinutes(15));
        fixture.Evaluations.Should().Be(2);
        fixture.InstancesCreated.Should().Be(1);

        await fixture.SweepAt(T0.AddMinutes(30));
        fixture.Evaluations.Should().Be(2, "a lost signal stays lost until a reading evaluates the rule");
    }

    [NativeFact]
    public async Task A_reading_moves_the_next_change()
    {
        var fixture = new Fixture(AlertConditionType.SignalLoss, SignalLoss15) { LastReadingAt = T0 };

        await fixture.SweepAt(T0.AddMinutes(1));
        await fixture.ReadingAt(T0.AddMinutes(10));
        fixture.Evaluations.Should().Be(2);
        (await fixture.TrackerRepo.GetTrackerStateAsync(RuleId))!.NextChangeAt.Should().Be(T0.AddMinutes(25));

        await fixture.SweepAt(T0.AddMinutes(16));
        fixture.Evaluations.Should().Be(2);
        fixture.InstancesCreated.Should().Be(0);

        await fixture.SweepAt(T0.AddMinutes(25));
        fixture.Evaluations.Should().Be(3);
        fixture.InstancesCreated.Should().Be(1);
    }

    /// <summary>
    /// Readings at <see cref="T0"/> minus five minutes and at <see cref="T0"/>, the newer one
    /// evaluated, so its recorded next change is <see cref="T0"/> plus fifteen minutes.
    /// </summary>
    private static async Task<Fixture> SignalLossAfterTwoReadingsAsync()
    {
        var fixture = new Fixture(AlertConditionType.SignalLoss, SignalLoss15);
        fixture.Readings.Add(new SensorGlucose { Timestamp = T0.AddMinutes(-5), Mgdl = 120 });
        fixture.Readings.Add(new SensorGlucose { Timestamp = T0, Mgdl = 120 });
        await fixture.ReadingAt(T0);
        (await fixture.TrackerRepo.GetTrackerStateAsync(RuleId))!.NextChangeAt.Should().Be(T0.AddMinutes(15));
        await fixture.SweepAt(T0.AddMinutes(5));
        fixture.Evaluations.Should().Be(1, "the sweep reads the facts the reading's evaluation read");
        return fixture;
    }

    /// <summary>A delete evaluates nothing, and the newest reading left is five minutes older.</summary>
    [NativeFact]
    public async Task Deleting_the_newest_reading_is_evaluated_when_the_older_one_times_out()
    {
        var fixture = await SignalLossAfterTwoReadingsAsync();
        fixture.Readings.RemoveAll(r => r.Timestamp == T0);

        await fixture.SweepAt(T0.AddMinutes(10));

        fixture.Evaluations.Should().Be(2);
        fixture.InstancesCreated.Should().Be(1);
    }

    /// <summary>A V3 edit of the newest reading evaluates nothing.</summary>
    [NativeFact]
    public async Task Editing_the_newest_reading_is_evaluated_against_the_edit()
    {
        var fixture = await SignalLossAfterTwoReadingsAsync();
        fixture.Readings.Single(r => r.Timestamp == T0).Timestamp = T0.AddMinutes(-3);

        await fixture.SweepAt(T0.AddMinutes(12));

        fixture.Evaluations.Should().Be(2);
        fixture.InstancesCreated.Should().Be(1);
    }

    public enum LostEvaluation
    {
        /// <summary>The orchestrator logs and swallows the engine's failure.</summary>
        Failed,

        /// <summary>The writer drops the decision, as when a sweep on another replica landed first.</summary>
        Dropped,
    }

    /// <summary>
    /// A low reading whose evaluation stores nothing leaves the next change of the reading before
    /// it, when that reading goes stale; the sweep must start the low's window, not wait for it.
    /// </summary>
    [NativeTheory]
    [InlineData(LostEvaluation.Failed)]
    [InlineData(LostEvaluation.Dropped)]
    public async Task A_reading_whose_evaluation_stored_nothing_is_evaluated_by_the_sweep(LostEvaluation lost)
    {
        var fixture = new Fixture(
            AlertConditionType.Composite,
            """
            {"operator": "or", "conditions": [
              {"type": "signal_loss", "signal_loss": {"timeout_minutes": 60}},
              {"type": "sustained", "sustained": {"minutes": 5, "child": {"type": "threshold", "threshold": {"direction": "below", "value": 70}}}}
            ]}
            """);
        fixture.Readings.Add(new SensorGlucose { Timestamp = T0, Mgdl = 120 });
        await fixture.ReadingAt(T0);
        (await fixture.TrackerRepo.GetTrackerStateAsync(RuleId)).Should().BeEquivalentTo(
            new { NextChangeKnown = true, NextChangeAt = (DateTime?)T0.AddMinutes(11) });

        fixture.Readings.Add(new SensorGlucose { Timestamp = T0.AddMinutes(5), Mgdl = 60 });
        fixture.LatestMgdl = 60;
        fixture.LoseNextEvaluation = lost;
        await fixture.ReadingAt(T0.AddMinutes(5));

        await fixture.SweepAt(T0.AddMinutes(5.5));
        fixture.Evaluations.Should().Be(3);
        await fixture.SweepAt(T0.AddMinutes(10.5));
        fixture.InstancesCreated.Should().Be(1);
    }

    /// <summary>
    /// Each tree with whether the fixture's context leaves its other leaf unknown, so the rule is
    /// held and its tracker row records only the hold, from the first sweep
    /// (docs/alerts/engine-semantics.md §6.4).
    /// </summary>
    public static TheoryData<string, bool> RulesReadingOtherFacts => new()
    {
        { Or("""{"type": "pump_suspended", "pump_suspended": {"is_active": true}}"""), true },
        { Or("""{"type": "override_active", "override_active": {"is_active": true}}"""), false },
        { Or("""{"type": "predicted", "predicted": {"operator": "<", "value": 55, "within_minutes": 30}}"""), true },
        { Or("""{"type": "time_of_day", "time_of_day": {"from": "22:00", "to": "06:00", "timezone": null}}"""), false },
        { Or("""{"type": "day_of_week", "day_of_week": {"days": ["monday"]}}"""), false },
        { Or("""{"type": "glucose_bucket", "glucose_bucket": {"buckets": ["low"]}}"""), true },
    };

    private static string Or(string other) =>
        $$$"""{"operator": "or", "conditions": [{"type": "signal_loss", "signal_loss": {"timeout_minutes": 15}}, {{{other}}}]}""";

    [NativeTheory]
    [MemberData(nameof(RulesReadingOtherFacts))]
    public async Task A_rule_reading_a_fact_no_reading_moves_is_evaluated_every_sweep(string tree, bool held)
    {
        var fixture = new Fixture(AlertConditionType.Composite, tree) { LastReadingAt = T0 };

        await fixture.SweepAt(T0.AddMinutes(1));
        await fixture.SweepAt(T0.AddMinutes(1.5));
        await fixture.SweepAt(T0.AddMinutes(2));

        fixture.Evaluations.Should().Be(3);
        var state = await fixture.TrackerRepo.GetTrackerStateAsync(RuleId);
        if (held)
        {
            state!.State.Should().Be("idle");
            state.ActiveExcursionId.Should().BeNull();
            state.HeldSince.Should().Be(T0.AddMinutes(1), "a hold begins at its first held evaluation");
            state.HeldFacts.Should().NotBeNullOrEmpty();
        }
        else
            state!.NextChangeKnown.Should().BeFalse();
    }

    private const string LoopStale30 = """{"operator": ">", "minutes": 30}""";

    /// <summary>
    /// Every write of an APS snapshot announces itself and evaluates the rules that read it, so a
    /// loop rule waits for its next change like a glucose-only one (docs/alerts/engine-semantics.md §5.3).
    /// </summary>
    [NativeFact]
    public async Task A_loop_rule_waits_for_its_next_change_and_an_aps_write_moves_it()
    {
        var fixture = new Fixture(AlertConditionType.LoopStale, LoopStale30) { LastReadingAt = T0, LastApsCycleAt = T0 };

        await fixture.SweepAt(T0.AddMinutes(1));
        fixture.Evaluations.Should().Be(1);
        (await fixture.TrackerRepo.GetTrackerStateAsync(RuleId))!.NextChangeAt.Should().BeAfter(T0.AddMinutes(30));
        await fixture.SweepAt(T0.AddMinutes(10));
        fixture.Evaluations.Should().Be(1, "nothing but the clock moves the rule before its next change");

        fixture.LastApsCycleAt = T0.AddMinutes(10);
        await fixture.WriteAt(T0.AddMinutes(10), ContextFactWrites.Aps);
        fixture.Evaluations.Should().Be(2, "the APS write evaluates the rule that reads it");

        await fixture.SweepAt(T0.AddMinutes(31));
        fixture.Evaluations.Should().Be(2, "the write moved the next change past the one first recorded");
        fixture.InstancesCreated.Should().Be(0);

        await fixture.SweepAt(T0.AddMinutes(41));
        fixture.Evaluations.Should().Be(3, "a skipped rule is still evaluated at its next change");
        fixture.InstancesCreated.Should().Be(1);
    }

    [NativeFact]
    public async Task A_write_of_a_fact_the_rule_does_not_read_evaluates_nothing()
    {
        var fixture = new Fixture(AlertConditionType.LoopStale, LoopStale30) { LastReadingAt = T0, LastApsCycleAt = T0 };
        await fixture.SweepAt(T0.AddMinutes(1));

        await fixture.WriteAt(T0.AddMinutes(5), ContextFactWrites.Pump, ContextFactWrites.Insulin);

        fixture.Evaluations.Should().Be(1);
    }

    /// <summary>
    /// Every write that moves a site change or a sensor start announces itself, deduplication's re-picks
    /// included, so a device-age rule waits for its next change. A re-pick that moves the anchor earlier
    /// is evaluated when it lands, and the rule then fires at the moved anchor's bound, not the recorded one.
    /// </summary>
    [NativeTheory]
    [InlineData(AlertConditionType.SiteAge, """{"operator": ">", "value": 72}""")]
    [InlineData(AlertConditionType.SensorAge, """{"operator": ">", "value": 3}""")]
    public async Task A_device_age_rule_waits_for_its_next_change_and_a_re_pick_of_its_anchor_moves_it(
        AlertConditionType type, string seventyTwoHours)
    {
        var fixture = new Fixture(type, seventyTwoHours) { LastReadingAt = T0 };
        fixture.SetDeviceAnchor(type, T0);

        await fixture.SweepAt(T0.AddMinutes(1));
        fixture.Evaluations.Should().Be(1);
        await fixture.SweepAt(T0.AddMinutes(10));
        fixture.Evaluations.Should().Be(1, "nothing but the clock moves the rule before its next change");

        fixture.SetDeviceAnchor(type, T0.AddMinutes(-20));
        await fixture.WriteAt(T0.AddMinutes(10), DeviceEventMoves);
        fixture.Evaluations.Should().Be(2, "the re-pick's announcement evaluates the rule that reads the anchor");

        await fixture.SweepAt(T0.AddHours(72).AddMinutes(-10));
        fixture.Evaluations.Should().Be(3, "the moved anchor's bound has passed, ahead of the first one recorded");
        fixture.InstancesCreated.Should().Be(1);
    }

    /// <summary>
    /// An anchor moved later, by a new site change or a re-pick of the later copy, is evaluated when it
    /// is announced and records the later bound, so the bound first recorded opens nothing.
    /// </summary>
    [NativeFact]
    public async Task A_site_age_rule_whose_anchor_moves_later_waits_for_the_later_bound()
    {
        var fixture = new Fixture(AlertConditionType.SiteAge, """{"operator": ">", "value": 72}""")
        {
            LastReadingAt = T0, LastSiteChangeAt = T0,
        };
        await fixture.SweepAt(T0.AddMinutes(1));

        fixture.LastSiteChangeAt = T0.AddMinutes(20);
        await fixture.WriteAt(T0.AddMinutes(21), DeviceEventMoves);
        fixture.Evaluations.Should().Be(2);

        await fixture.SweepAt(T0.AddHours(72).AddMinutes(10));
        fixture.Evaluations.Should().Be(2, "the next change recorded is the later anchor's bound");
        fixture.InstancesCreated.Should().Be(0, "the site is not 72 hours old by the later anchor");

        await fixture.SweepAt(T0.AddHours(72).AddMinutes(21));
        fixture.Evaluations.Should().Be(3);
        fixture.InstancesCreated.Should().Be(1);
    }

    private static string[] DeviceEventMoves =>
        [.. ContextFactWrites.MovedIn(RecordsChanged.Written([typeof(DeviceEvent)])).Facts];

    /// <summary>A rule over a fact that moves with the clock (insulin on board) is evaluated by every sweep.</summary>
    [NativeTheory]
    [InlineData(AlertConditionType.TimeSinceLastBolus, """{"operator": "<", "minutes": 60}""")]
    public async Task A_rule_over_a_fact_the_sweep_may_not_wait_for_is_evaluated_every_sweep(
        AlertConditionType type, string payload)
    {
        var fixture = new Fixture(type, payload) { LastReadingAt = T0, LastBolusAt = T0 };

        await fixture.SweepAt(T0.AddMinutes(1));
        await fixture.SweepAt(T0.AddMinutes(1.5));
        await fixture.SweepAt(T0.AddMinutes(2));

        fixture.Evaluations.Should().Be(3);
        (await fixture.TrackerRepo.GetTrackerStateAsync(RuleId))!.NextChangeKnown.Should().BeFalse(
            "a next change the sweep would never wait for is not recorded");
    }

    /// <summary>
    /// Behind a newest reading with no value the context rests on a reading found by looking back,
    /// which a write behind it can change without moving the newest reading, so no rule waits then.
    /// </summary>
    [NativeFact]
    public async Task Behind_an_unusable_newest_reading_a_loop_rule_is_evaluated_every_sweep()
    {
        var fixture = new Fixture(AlertConditionType.LoopStale, LoopStale30) { LastApsCycleAt = T0 };
        fixture.Readings.Add(new SensorGlucose { Timestamp = T0.AddMinutes(-5), Mgdl = 120 });
        fixture.Readings.Add(new SensorGlucose { Timestamp = T0, Mgdl = 0 });

        await fixture.SweepAt(T0.AddMinutes(1));
        await fixture.SweepAt(T0.AddMinutes(1.5));

        fixture.Evaluations.Should().Be(2);
    }

    /// <summary>
    /// A newest reading with no value is read as that reading, with the sensor's no_value state
    /// from the first reading without one, and signal loss measured from the last reading with a
    /// value (docs/alerts/engine-semantics.md §4, sensor state).
    /// </summary>
    [NativeFact]
    public async Task Behind_a_newest_reading_with_no_value_the_sweep_reads_the_no_value_state()
    {
        const string lowOrLost = """
            {"operator": "or", "conditions": [
              {"type": "threshold", "threshold": {"direction": "below", "value": 70}},
              {"type": "signal_loss", "signal_loss": {"timeout_minutes": 15}}
            ]}
            """;
        var fixture = new Fixture(AlertConditionType.Composite, lowOrLost);
        fixture.Readings.Add(new SensorGlucose { Timestamp = T0, CreatedAt = T0, Mgdl = 60 });
        fixture.Readings.Add(new SensorGlucose { Timestamp = T0.AddMinutes(5), CreatedAt = T0.AddMinutes(5), Mgdl = 0 });
        fixture.Readings.Add(new SensorGlucose { Timestamp = T0.AddMinutes(10), CreatedAt = T0.AddMinutes(10), Mgdl = 0 });

        await fixture.SweepAt(T0.AddMinutes(10.5));

        var context = fixture.LastContext!;
        context.LatestValue.Should().BeNull("the newest reading has no value, and an older value is not read in its place");
        context.LatestTimestamp.Should().Be(T0.AddMinutes(10));
        context.LastReadingAt.Should().Be(T0, "signal loss means no reading with a value");
        context.SensorState.Should().Be(new SensorStateSnapshot(SensorState.NoValue, T0.AddMinutes(5)));
        fixture.InstancesCreated.Should().Be(0, "the low leaf waits for a value, and the signal is lost for less than 15 minutes");

        await fixture.SweepAt(T0.AddMinutes(15));
        fixture.InstancesCreated.Should().Be(1, "readings with no value are not signal");
    }

    /// <summary>
    /// A device status that says the sensor is ok again is a write that evaluates the glucose rules:
    /// a low held while the sensor reported an error fires on that write, not on the next reading
    /// (docs/alerts/engine-semantics.md §5.2).
    /// </summary>
    [NativeFact]
    public async Task A_low_held_by_a_sensor_error_fires_on_the_status_write_that_says_ok()
    {
        var fixture = new Fixture(AlertConditionType.Threshold, """{"direction": "below", "value": 70}""")
        {
            LatestMgdl = 55,
            ReportedSensorState = new SensorStateSnapshot(SensorState.Error, null, T0.AddMinutes(11)),
        };
        fixture.Readings.Add(new SensorGlucose { Timestamp = T0, CreatedAt = T0, Mgdl = 55 });

        await fixture.WriteAt(T0, ContextFactWrites.Glucose);
        fixture.InstancesCreated.Should().Be(0, "the sensor reported an error, so the low waits");

        fixture.ReportedSensorState = new SensorStateSnapshot(SensorState.Ok, null, T0.AddMinutes(12));
        await fixture.WriteAt(T0.AddMinutes(1), ContextFactWrites.SensorState);

        fixture.InstancesCreated.Should().Be(1, "the status write evaluated the low on the reading it already had");
    }

    /// <summary>
    /// A wall-clock glucose rule held by a reported state waits for the instant the report lapses,
    /// which the engine gives as its next change, and is evaluated there with no reading.
    /// </summary>
    [NativeFact]
    public async Task A_rule_held_by_a_reported_state_waits_until_the_report_lapses()
    {
        const string lowOrLost = """
            {"operator": "or", "conditions": [
              {"type": "threshold", "threshold": {"direction": "below", "value": 70}},
              {"type": "signal_loss", "signal_loss": {"timeout_minutes": 60}}
            ]}
            """;
        var fixture = new Fixture(AlertConditionType.Composite, lowOrLost)
        {
            LatestMgdl = 55,
            ReportedSensorState = new SensorStateSnapshot(SensorState.Error, null, T0.AddMinutes(7)),
        };
        fixture.Readings.Add(new SensorGlucose { Timestamp = T0, CreatedAt = T0, Mgdl = 55 });

        await fixture.WriteAt(T0, ContextFactWrites.Glucose);
        (await fixture.TrackerRepo.GetTrackerStateAsync(RuleId)).Should().BeEquivalentTo(
            new { NextChangeKnown = true, NextChangeAt = (DateTime?)T0.AddMinutes(7) });
        var evaluated = fixture.Evaluations;

        await fixture.SweepAt(T0.AddMinutes(5));
        fixture.Evaluations.Should().Be(evaluated, "nothing the rule reads moves before the report lapses");

        await fixture.SweepAt(T0.AddMinutes(7.5));
        fixture.Evaluations.Should().Be(evaluated + 1);
        fixture.InstancesCreated.Should().Be(1, "the lapsed report is no state, so the low reads its value");
    }

    [NativeFact]
    public async Task A_rule_without_a_recorded_next_change_is_evaluated()
    {
        var fixture = new Fixture(AlertConditionType.SignalLoss, SignalLoss15) { LastReadingAt = T0 };
        await fixture.TrackerRepo.UpsertTrackerStateAsync(new AlertTrackerState
        {
            AlertRuleId = RuleId,
            State = "idle",
            UpdatedAt = T0,
            NextChangeAt = T0.AddMinutes(15),
            NextChangeKnown = false,
        });

        await fixture.SweepAt(T0.AddMinutes(1));

        fixture.Evaluations.Should().Be(1);
    }

    /// <summary>The edit as <see cref="AlertRuleRearm"/> saves one: the rule and a dropped next change.</summary>
    [NativeFact]
    public async Task A_rule_edit_drops_the_next_change_so_the_sweep_evaluates()
    {
        var fixture = new Fixture(AlertConditionType.SignalLoss, SignalLoss15) { LastReadingAt = T0 };
        await fixture.SweepAt(T0.AddMinutes(1));

        const string edited = """{"timeout_minutes": 5}""";
        fixture.Rules[0] = fixture.Rules[0] with { ConditionParams = edited };
        (await fixture.TrackerRepo.GetRuleAsync(RuleId))!.ConditionParams = edited;
        var state = (await fixture.TrackerRepo.GetTrackerStateAsync(RuleId))!;
        state.NextChangeKnown = false;
        state.NextChangeAt = null;

        await fixture.SweepAt(T0.AddMinutes(6));

        fixture.Evaluations.Should().Be(2);
        fixture.InstancesCreated.Should().Be(1);
    }

    [NativeFact]
    public async Task A_new_process_reads_the_next_change_this_one_recorded()
    {
        var fixture = new Fixture(AlertConditionType.SignalLoss, SignalLoss15) { LastReadingAt = T0 };
        await fixture.SweepAt(T0.AddMinutes(1));

        fixture.Restart();
        await fixture.SweepAt(T0.AddMinutes(5));
        fixture.Evaluations.Should().Be(1);

        await fixture.SweepAt(T0.AddMinutes(15));
        fixture.Evaluations.Should().Be(2);
    }

    private const string NightWindow =
        """{"type": "time_of_day", "time_of_day": {"from": "22:00", "to": "06:00", "timezone": null}}""";

    private const string BackstopRan = "The sweep evaluated every enabled rule";

    [NativeFact]
    public async Task A_sweep_whose_passes_each_measure_from_the_reading_reports_it_once()
    {
        var fixture = new Fixture(AlertConditionType.Composite, Or(NightWindow));
        fixture.Readings.Add(new SensorGlucose { Timestamp = T0, Mgdl = 120 });
        fixture.AutoResolving.Add(new AutoResolveExcursionSnapshot(Guid.NewGuid(), Tenant, fixture.Rules[0]));

        await fixture.SweepAt(T0.AddMinutes(1));

        fixture.Evaluations.Should().Be(1, "the wall-clock pass measured from the reading");
        fixture.ClockNotice.Verify(n => n.ReportReadingAsync(
            It.IsAny<AlertReadingSelection>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [NativeFact]
    public async Task A_sweep_whose_clock_notice_needs_the_reading_reports_it_once()
    {
        var fixture = new Fixture(AlertConditionType.Composite, Or(NightWindow));
        fixture.Readings.Add(new SensorGlucose { Timestamp = T0, Mgdl = 120 });
        fixture.AutoResolving.Add(new AutoResolveExcursionSnapshot(Guid.NewGuid(), Tenant, fixture.Rules[0]));
        fixture.ClockNotice.Setup(n => n.NeedsReadingReport(It.IsAny<DateTime>())).Returns(true);

        await fixture.SweepAt(T0.AddMinutes(1));

        fixture.ClockNotice.Verify(n => n.NeedsReadingReport(It.IsAny<DateTime>()), Times.Once);
        fixture.ClockNotice.Verify(n => n.ReportReadingAsync(
            It.IsAny<AlertReadingSelection>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [NativeFact]
    public async Task A_sweep_whose_clock_notice_needs_no_report_leaves_the_report_to_the_pass_that_measures_the_reading()
    {
        var fixture = new Fixture(AlertConditionType.Composite, Or(NightWindow));
        fixture.Readings.Add(new SensorGlucose { Timestamp = T0, Mgdl = 120 });
        fixture.AutoResolving.Add(new AutoResolveExcursionSnapshot(Guid.NewGuid(), Tenant, fixture.Rules[0]));
        fixture.ClockNotice.Setup(n => n.NeedsReadingReport(It.IsAny<DateTime>())).Returns(false);

        await fixture.SweepAt(T0.AddMinutes(1));

        fixture.ClockNotice.Verify(n => n.ReportReadingAsync(
            It.IsAny<AlertReadingSelection>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [NativeFact]
    public async Task A_sweep_with_no_enabled_rule_and_a_clock_notice_needing_no_report_reports_nothing()
    {
        var fixture = new Fixture(AlertConditionType.Composite, Or(NightWindow));
        fixture.Rules.Clear();
        fixture.Readings.Add(new SensorGlucose { Timestamp = T0, Mgdl = 120 });
        fixture.ClockNotice.Setup(n => n.NeedsReadingReport(It.IsAny<DateTime>())).Returns(false);

        await fixture.SweepAt(T0.AddMinutes(1));

        fixture.ClockNotice.Verify(n => n.ReportReadingAsync(
            It.IsAny<AlertReadingSelection>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [NativeFact]
    public async Task A_sweep_with_no_enabled_rule_reports_the_reading_once_when_the_clock_notice_needs_it()
    {
        var fixture = new Fixture(AlertConditionType.Composite, Or(NightWindow));
        fixture.Rules.Clear();
        fixture.Readings.Add(new SensorGlucose { Timestamp = T0, Mgdl = 120 });
        fixture.ClockNotice.Setup(n => n.NeedsReadingReport(It.IsAny<DateTime>())).Returns(true);

        await fixture.SweepAt(T0.AddMinutes(1));

        fixture.ClockNotice.Verify(n => n.ReportReadingAsync(
            It.IsAny<AlertReadingSelection>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [NativeFact]
    public async Task A_backstop_sweep_reports_the_reading_once()
    {
        var fixture = new Fixture(AlertConditionType.Composite, Or(NightWindow)) { NoEvaluationRecorded = true };
        fixture.Readings.Add(new SensorGlucose { Timestamp = T0, Mgdl = 120 });
        fixture.AutoResolving.Add(new AutoResolveExcursionSnapshot(Guid.NewGuid(), Tenant, fixture.Rules[0]));

        await fixture.SweepAt(T0.AddMinutes(1));

        fixture.Logger.Entries.Should().Contain(e => e.Message.Contains(BackstopRan));
        fixture.ClockNotice.Verify(n => n.ReportReadingAsync(
            It.IsAny<AlertReadingSelection>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [NativeFact]
    public async Task A_backstop_sweep_evaluates_a_wall_clock_rule_once()
    {
        var fixture = new Fixture(AlertConditionType.Composite, Or(NightWindow)) { NoEvaluationRecorded = true };
        fixture.Readings.Add(new SensorGlucose { Timestamp = T0, Mgdl = 120 });

        await fixture.SweepAt(T0.AddMinutes(1));

        fixture.Logger.Entries.Should().Contain(e => e.Message.Contains(BackstopRan));
        fixture.Evaluations.Should().Be(1);
    }

    [NativeFact]
    public async Task A_sweep_with_no_backstop_evaluates_a_wall_clock_rule_in_its_wall_clock_pass()
    {
        var fixture = new Fixture(AlertConditionType.Composite, Or(NightWindow));
        fixture.Readings.Add(new SensorGlucose { Timestamp = T0, Mgdl = 120 });

        await fixture.SweepAt(T0.AddMinutes(1));

        fixture.Logger.Entries.Should().NotContain(e => e.Message.Contains(BackstopRan));
        fixture.Evaluations.Should().Be(1);
    }

    [NativeFact]
    public async Task Without_a_usable_reading_every_sweep_evaluates()
    {
        var fixture = new Fixture(AlertConditionType.SignalLoss, SignalLoss15);
        fixture.Readings.Add(new SensorGlucose { Timestamp = T0, Mgdl = 0 });
        fixture.LastReadingAt = T0;

        await fixture.SweepAt(T0.AddMinutes(1));
        await fixture.SweepAt(T0.AddMinutes(2));

        fixture.Evaluations.Should().Be(2);
    }

    private sealed class Fixture
    {
        private AlertSweepJob _sweep;
        private readonly ServiceProvider _services;
        private readonly CountingEngine _engine;

        public ManualTimeProvider Time { get; } = new();
        public ListLogger<AlertSweepJob> Logger { get; } = new();
        public ListLogger<AlertOrchestrator> OrchestratorLogger { get; } = new();
        public InMemoryTrackerRepository TrackerRepo { get; }
        public DateTime? LastReadingAt { get; set; }
        public double? LatestMgdl { get; set; }
        public DateTime? LastApsCycleAt { get; set; }
        public DateTime? LastSiteChangeAt { get; set; }
        public DateTime? LastSensorStartAt { get; set; }
        public DateTime? LastBolusAt { get; set; }

        /// <summary>Sets the anchor a <c>site_age</c> or <c>sensor_age</c> rule reads.</summary>
        public void SetDeviceAnchor(AlertConditionType type, DateTime at)
        {
            if (type == AlertConditionType.SensorAge)
                LastSensorStartAt = at;
            else
                LastSiteChangeAt = at;
        }

        /// <summary>The state a device status reports, as the enricher adds it.</summary>
        public SensorStateSnapshot? ReportedSensorState { get; set; }
        public int InstancesCreated { get; private set; }
        public int Dispatches { get; private set; }
        public AlertPayload? LastPayload { get; private set; }
        public List<ExcursionTransition> Closed { get; } = [];
        public Dictionary<Guid, ActiveAlertSnapshot> ActiveAlerts { get; } = [];

        /// <summary>The enabled rules each sweep reads; replace one to edit it between sweeps.</summary>
        public List<AlertRuleSnapshot> Rules { get; }

        /// <summary>The canonical stream; when empty, one reading at <see cref="LastReadingAt"/>.</summary>
        public List<SensorGlucose> Readings { get; } = [];

        public Mock<ICanonicalGlucoseService> Canonical { get; } = new();

        public Mock<IClockAnomalyNotice> ClockNotice { get; } = new();

        /// <summary>The excursions the auto-resolve pass reads.</summary>
        public List<AutoResolveExcursionSnapshot> AutoResolving { get; } = [];

        /// <summary>
        /// When set, no reading is recorded as evaluated, so the sweep's backstop evaluates the
        /// newest reading once it is past <see cref="AlertSweepJob.LostEvaluationGrace"/>.
        /// </summary>
        public bool NoEvaluationRecorded { get; set; }

        /// <summary>The context the last evaluation of <see cref="RuleId"/> read.</summary>
        public SensorContext? LastContext => _engine.LastContext;

        public Task SweepAt(DateTime at)
        {
            Time.SetUtcNow(at);
            return _sweep.RunAsync(_services, Tenant, Time.GetUtcNow());
        }

        /// <summary>A usable reading at <paramref name="at"/>, evaluated as the per-reading path does.</summary>
        public async Task ReadingAt(DateTime at)
        {
            Time.SetUtcNow(at);
            LastReadingAt = at;
            var context = new TenantContext(Tenant, "slug", "Slug", true, IsDemo: false);
            await using var scope = TenantJobScope.Open(_services, context, "reading");
            var reading = new SensorGlucose { Timestamp = at, CreatedAt = at, Mgdl = LatestMgdl ?? 120 };
            var trend = await ReadingTrend.ForAsync(reading, Canonical.Object, CancellationToken.None);
            var rules = await scope.ServiceProvider.GetRequiredService<IAlertRepository>()
                .GetEnabledRulesAsync(CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<IAlertOrchestrator>().EvaluateRulesAsync(
                rules, rules.Select(r => r.Id).ToHashSet(), CanonicalAlertEvaluator.ContextFor(reading, trend),
                CancellationToken.None);
        }

        /// <summary>A committed write at <paramref name="at"/> that moved <paramref name="facts"/>, evaluated as its record-changed event is.</summary>
        public async Task WriteAt(DateTime at, params string[] facts)
        {
            Time.SetUtcNow(at);
            var context = new TenantContext(Tenant, "slug", "Slug", true, IsDemo: false);
            await using var scope = TenantJobScope.Open(_services, context, "write");
            await scope.ServiceProvider.GetRequiredService<ICanonicalAlertEvaluator>()
                .EvaluateAsync(new MovedFacts(facts.ToHashSet()), ct: CancellationToken.None);
        }

        /// <summary>A reading as the store returns it: stored at its own time unless a test says otherwise.</summary>
        private static SensorGlucose Stored(SensorGlucose reading)
        {
            if (reading.CreatedAt == default)
                reading.CreatedAt = reading.Timestamp;
            return reading;
        }

        /// <summary>The rule under test's <c>on_unknown</c>, in its snapshot and its stored row.</summary>
        public async Task SetOnUnknown(string onUnknown)
        {
            Rules[0] = Rules[0] with { OnUnknown = onUnknown };
            (await TrackerRepo.GetRuleAsync(RuleId))!.OnUnknown = onUnknown;
        }

        /// <summary>The sweep as a new process runs it, with only what is stored.</summary>
        public void Restart() => _sweep = new AlertSweepJob(Logger);

        /// <summary>Full evaluations of the rule under test, by any path.</summary>
        public int Evaluations => _engine.Evaluations;

        /// <summary>How the next evaluation of the rule under test stores nothing, if it does.</summary>
        public LostEvaluation? LoseNextEvaluation
        {
            set => _engine.LoseNext = value;
        }

        public Fixture(
            AlertConditionType conditionType, string conditionParams,
            params AlertRule[] otherRules)
        {
            var rule = new AlertRule
            {
                Id = RuleId,
                Name = "Wall clock",
                ConditionType = conditionType,
                ConditionParams = conditionParams,
                HysteresisMinutes = 0,
            };
            var snapshot = new AlertRuleSnapshot(
                RuleId, Tenant, rule.Name, conditionType, rule.ConditionParams,
                AlertRuleSeverity.Warning, "{}", 0, false, null);

            var snapshots = otherRules
                .Select(r => new AlertRuleSnapshot(
                    r.Id, Tenant, r.Name, r.ConditionType, r.ConditionParams,
                    AlertRuleSeverity.Warning, "{}", 0, false, null))
                .Prepend(snapshot)
                .ToList();

            Rules = snapshots;
            TrackerRepo = new InMemoryTrackerRepository([rule, .. otherRules]);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<TimeProvider>(Time);

            _engine = new CountingEngine(EngineTestHarness.BuildRustEngine(Time, TrackerRepo));
            services.AddSingleton<IAlertEvaluationEngine>(_engine);
            services.AddSingleton<IAlertTrackerRepository>(TrackerRepo);

            var repository = AlertSweepRuns.Repository(Tenant, () => LastReadingAt);
            repository
                .Setup(x => x.GetEnabledRulesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Rules.ToList());
            repository
                .Setup(x => x.ReadEnabledRulesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new EnabledAlertRules(Rules.ToList(), []));
            repository
                .Setup(x => x.GetAutoResolveExcursionsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => AutoResolving.ToList());
            repository
                .Setup(x => x.EnsureInstanceAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    InstancesCreated++;
                    return new OpenedInstance(Guid.NewGuid(), Suppressed: false);
                });
            services.AddSingleton(repository.Object);
            var rules = new Mock<IAlertRuleRepository>();
            rules
                .Setup(x => x.GetChannelsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Dictionary<Guid, IReadOnlyList<AlertRuleChannelSnapshot>>());
            services.AddSingleton(rules.Object);

            var canonical = Canonical;
            canonical
                .Setup(x => x.GetLatestAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Readings.Count > 0
                    ? Stored(Readings.MaxBy(r => r.Timestamp)!)
                    : LastReadingAt is { } at
                        ? new SensorGlucose { Timestamp = at, CreatedAt = at, Mgdl = LatestMgdl ?? 120 }
                        : null);
            canonical
                .Setup(x => x.GetLatestAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((DateTime notAfter, CancellationToken _) =>
                    Readings.Where(r => r.Timestamp <= notAfter).MaxBy(r => r.Timestamp) is { } bounded
                        ? Stored(bounded)
                        : null);
            canonical
                .Setup(x => x.GetRecentAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((DateTime since, CancellationToken _) =>
                    Readings.Where(r => r.Timestamp >= since).OrderByDescending(r => r.Timestamp).Select(Stored).ToList());
            canonical
                .Setup(x => x.GetStreamPredecessorAsync(It.IsAny<SensorGlucose>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SensorGlucose reading, DateTime since, CancellationToken _) => Readings
                    .Where(r => r.Timestamp < reading.Timestamp && r.Timestamp >= since)
                    .MaxBy(r => r.Timestamp));
            services.AddSingleton(canonical.Object);

            var delivery = new Mock<IAlertDeliveryService>();
            delivery
                .Setup(x => x.DispatchAsync(
                    It.IsAny<Guid>(), It.IsAny<IReadOnlyList<AlertRuleChannelSnapshot>>(),
                    It.IsAny<AlertPayload>(), It.IsAny<CancellationToken>()))
                .Callback((Guid _, IReadOnlyList<AlertRuleChannelSnapshot> _, AlertPayload p, CancellationToken _) =>
                {
                    Dispatches++;
                    LastPayload = p;
                })
                .Returns(Task.CompletedTask);
            services.AddSingleton(delivery.Object);

            var enricher = new Mock<ISensorContextEnricher>();
            enricher
                .Setup(x => x.EnrichAsync(
                    It.IsAny<SensorContext>(), It.IsAny<IEnumerable<AlertRuleSnapshot>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SensorContext c, IEnumerable<AlertRuleSnapshot> _, CancellationToken _) =>
                    c with
                    {
                        ActiveAlerts = ActiveAlerts,
                        LastApsCycleAt = LastApsCycleAt,
                        HasEverApsCycled = LastApsCycleAt is not null,
                        LastSiteChangeAt = LastSiteChangeAt,
                        LastSensorStartAt = LastSensorStartAt,
                        LastBolusAt = LastBolusAt,
                        SensorState = ReportedSensorState ?? c.SensorState,
                    });
            services.AddSingleton(enricher.Object);

            var resolution = new Mock<IExcursionResolutionHandler>();
            resolution
                .Setup(x => x.HandleClosedAsync(It.IsAny<ExcursionTransition>(), Tenant, It.IsAny<CancellationToken>()))
                .Callback((ExcursionTransition t, Guid _, CancellationToken _) => Closed.Add(t))
                .Returns(Task.CompletedTask);
            services.AddSingleton(resolution.Object);

            services.AddSingleton(Mock.Of<IAlertAcknowledgementService>());
            services.AddScoped<ITenantAccessor, HttpContextTenantAccessor>();
            services.AddScoped<IAuditContext, AuditContext>();
            services.AddSingleton<ConditionVersionLog>();
            services.AddSingleton<NotTrueRecordingFloor>();
            services.AddSingleton<IAlertRuleConditionValidator, AlertRuleConditionValidator>();
            services.AddSingleton<ILogger<AlertOrchestrator>>(OrchestratorLogger);
            services.AddScoped<IExcursionOpenedHandler, ExcursionOpenedHandler>();
            services.AddSingleton<ILogger<UnevaluableRuleNotice>>(new ListLogger<UnevaluableRuleNotice>());
            services.AddScoped<IUnevaluableRuleNotice, UnevaluableRuleNotice>();
            services.AddSingleton(NoHeldRules.Notice());
            services.AddSingleton(ClockNotice.Object);
            services.AddSingleton(Mock.Of<IAlertChannelNotice>());
            services.AddSingleton<AlertRuleEvaluationGate>();
            services.AddSingleton<AlertRuleDecisions>();
            services.AddScoped<IAlertOrchestrator, AlertOrchestrator>();
            services.AddSingleton<AlertEvaluationWatermark>();
            services.AddScoped<ICanonicalAlertEvaluator, CanonicalAlertEvaluator>();
            services.AddSingleton<IEvaluatedReadingStore>(
                new UnlessNoneRecorded(EvaluatedReadings.EveryNewest(canonical.Object, Time), () => NoEvaluationRecorded));

            _services = services.BuildServiceProvider();
            _sweep = new AlertSweepJob(Logger);
        }
    }

    private sealed class UnlessNoneRecorded(IEvaluatedReadingStore inner, Func<bool> none) : IEvaluatedReadingStore
    {
        public Task<EvaluatedReading?> GetAsync(CancellationToken ct) =>
            none() ? Task.FromResult<EvaluatedReading?>(null) : inner.GetAsync(ct);

        public Task RecordAsync(EvaluatedReading reading, CancellationToken ct) => inner.RecordAsync(reading, ct);
    }

    /// <summary>Counts full evaluations of <see cref="RuleId"/>.</summary>
    private sealed class CountingEngine(IAlertEvaluationEngine inner) : IAlertEvaluationEngine
    {
        public int Evaluations { get; private set; }

        public SensorContext? LastContext { get; private set; }

        public LostEvaluation? LoseNext { get; set; }

        public Task<AlertEngineEvaluation> EvaluateRuleAsync(
            AlertRuleSnapshot rule, SensorContext context, AlertEngineOptions options, CancellationToken ct)
        {
            if (rule.Id != RuleId)
                return inner.EvaluateRuleAsync(rule, context, options, ct);

            Evaluations++;
            LastContext = context;
            var lost = LoseNext;
            LoseNext = null;
            return lost switch
            {
                LostEvaluation.Failed => throw new InvalidOperationException("The engine failed"),
                LostEvaluation.Dropped => Task.FromResult(new AlertEngineEvaluation
                {
                    Transition = new ExcursionTransition(ExcursionTransitionType.None),
                }),
                _ => inner.EvaluateRuleAsync(rule, context, options, ct),
            };
        }

        public Task<bool> EvaluateSnoozeAsync(
            Guid ruleId, JsonElement conditions, SensorContext context, CancellationToken ct) =>
            inner.EvaluateSnoozeAsync(ruleId, conditions, context, ct);

        public Task<ExcursionTransition> EvaluateAutoResolveAsync(
            AlertRuleSnapshot rule, SensorContext context, CancellationToken ct) =>
            inner.EvaluateAutoResolveAsync(rule, context, ct);
    }
}
