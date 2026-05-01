using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Core.Models.Tests.AlertModels;

/// <summary>
/// Round-trip JSON serialization tests for the recursive <see cref="ConditionNode"/> tree.
/// Mirrors the JSON options used by the runtime evaluators.
/// </summary>
[Trait("Category", "Unit")]
public class ConditionNodeSerializationTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private static T RoundTrip<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, Options);
        return JsonSerializer.Deserialize<T>(json, Options)!;
    }

    [Fact]
    public void Threshold_RoundTrips()
    {
        var node = new ConditionNode("threshold", Threshold: new ThresholdCondition("below", 70m));

        var result = RoundTrip(node);

        result.Type.Should().Be("threshold");
        result.Threshold.Should().NotBeNull();
        result.Threshold!.Direction.Should().Be("below");
        result.Threshold.Value.Should().Be(70m);
    }

    [Fact]
    public void RateOfChange_RoundTrips()
    {
        var node = new ConditionNode("rate_of_change", RateOfChange: new RateOfChangeCondition("falling", 3m));

        var result = RoundTrip(node);

        result.RateOfChange.Should().NotBeNull();
        result.RateOfChange!.Direction.Should().Be("falling");
        result.RateOfChange.Rate.Should().Be(3m);
    }

    [Fact]
    public void SignalLoss_RoundTrips()
    {
        var node = new ConditionNode("signal_loss", SignalLoss: new SignalLossCondition(20));

        var result = RoundTrip(node);

        result.SignalLoss.Should().NotBeNull();
        result.SignalLoss!.TimeoutMinutes.Should().Be(20);
    }

    [Fact]
    public void Not_RoundTrips()
    {
        var inner = new ConditionNode("threshold", Threshold: new ThresholdCondition("above", 180m));
        var node = new ConditionNode("not", Not: new NotCondition(inner));

        var result = RoundTrip(node);

        result.Type.Should().Be("not");
        result.Not.Should().NotBeNull();
        result.Not!.Child.Type.Should().Be("threshold");
        result.Not.Child.Threshold!.Direction.Should().Be("above");
        result.Not.Child.Threshold.Value.Should().Be(180m);
    }

    [Fact]
    public void Sustained_RoundTrips()
    {
        var inner = new ConditionNode("threshold", Threshold: new ThresholdCondition("below", 70m));
        var node = new ConditionNode("sustained", Sustained: new SustainedCondition(15, inner));

        var result = RoundTrip(node);

        result.Sustained.Should().NotBeNull();
        result.Sustained!.Minutes.Should().Be(15);
        result.Sustained.Child.Threshold!.Value.Should().Be(70m);
    }

    [Fact]
    public void Staleness_RoundTrips()
    {
        var node = new ConditionNode("staleness", Staleness: new StalenessCondition(">=", 25));

        var result = RoundTrip(node);

        result.Staleness.Should().NotBeNull();
        result.Staleness!.Operator.Should().Be(">=");
        result.Staleness.Value.Should().Be(25);
    }

    [Fact]
    public void Predicted_RoundTrips()
    {
        var node = new ConditionNode("predicted", Predicted: new PredictedCondition("<", 70m, 30));

        var result = RoundTrip(node);

        result.Predicted.Should().NotBeNull();
        result.Predicted!.Operator.Should().Be("<");
        result.Predicted.Value.Should().Be(70m);
        result.Predicted.WithinMinutes.Should().Be(30);
    }

    [Fact]
    public void Trend_RoundTrips()
    {
        var node = new ConditionNode("trend", Trend: new TrendCondition("falling_fast"));

        var result = RoundTrip(node);

        result.Trend.Should().NotBeNull();
        result.Trend!.Bucket.Should().Be("falling_fast");
    }

    [Fact]
    public void TimeOfDay_RoundTrips()
    {
        var node = new ConditionNode("time_of_day",
            TimeOfDay: new TimeOfDayCondition("22:00", "07:00", "Europe/London"));

        var result = RoundTrip(node);

        result.TimeOfDay.Should().NotBeNull();
        result.TimeOfDay!.From.Should().Be("22:00");
        result.TimeOfDay.To.Should().Be("07:00");
        result.TimeOfDay.Timezone.Should().Be("Europe/London");
    }

    [Fact]
    public void TimeOfDay_NullTimezone_RoundTrips()
    {
        var node = new ConditionNode("time_of_day",
            TimeOfDay: new TimeOfDayCondition("22:00", "07:00", null));

        var result = RoundTrip(node);

        result.TimeOfDay!.Timezone.Should().BeNull();
    }

    [Fact]
    public void Iob_RoundTrips()
    {
        var node = new ConditionNode("iob", Iob: new IobCondition(">", 2.5m));

        var result = RoundTrip(node);

        result.Iob.Should().NotBeNull();
        result.Iob!.Operator.Should().Be(">");
        result.Iob.Value.Should().Be(2.5m);
    }

    [Fact]
    public void Cob_RoundTrips()
    {
        var node = new ConditionNode("cob", Cob: new CobCondition("<=", 40m));

        var result = RoundTrip(node);

        result.Cob.Should().NotBeNull();
        result.Cob!.Operator.Should().Be("<=");
        result.Cob.Value.Should().Be(40m);
    }

    [Fact]
    public void Reservoir_RoundTrips()
    {
        var node = new ConditionNode("reservoir", Reservoir: new ReservoirCondition("<", 20m));

        var result = RoundTrip(node);

        result.Reservoir.Should().NotBeNull();
        result.Reservoir!.Operator.Should().Be("<");
        result.Reservoir.Value.Should().Be(20m);
    }

    [Fact]
    public void SiteAge_RoundTrips()
    {
        var node = new ConditionNode("site_age", SiteAge: new SiteAgeCondition(">=", 72m));

        var result = RoundTrip(node);

        result.SiteAge.Should().NotBeNull();
        result.SiteAge!.Operator.Should().Be(">=");
        result.SiteAge.Value.Should().Be(72m);
    }

    [Fact]
    public void SensorAge_RoundTrips()
    {
        var node = new ConditionNode("sensor_age", SensorAge: new SensorAgeCondition(">", 9.5m));

        var result = RoundTrip(node);

        result.SensorAge.Should().NotBeNull();
        result.SensorAge!.Operator.Should().Be(">");
        result.SensorAge.Value.Should().Be(9.5m);
    }

    [Fact]
    public void AlertState_RoundTrips()
    {
        var alertId = Guid.NewGuid();
        var node = new ConditionNode("alert_state",
            AlertState: new AlertStateCondition(alertId, "firing", 10));

        var result = RoundTrip(node);

        result.AlertState.Should().NotBeNull();
        result.AlertState!.AlertId.Should().Be(alertId);
        result.AlertState.State.Should().Be("firing");
        result.AlertState.ForMinutes.Should().Be(10);
    }

    [Fact]
    public void AlertState_NullForMinutes_RoundTrips()
    {
        var alertId = Guid.NewGuid();
        var node = new ConditionNode("alert_state",
            AlertState: new AlertStateCondition(alertId, "unacknowledged", null));

        var result = RoundTrip(node);

        result.AlertState!.ForMinutes.Should().BeNull();
    }

    [Fact]
    public void LoopStale_RoundTrips()
    {
        var node = new ConditionNode("loop_stale", LoopStale: new LoopStaleCondition(">", 10));

        var result = RoundTrip(node);

        result.Type.Should().Be("loop_stale");
        result.LoopStale.Should().NotBeNull();
        result.LoopStale!.Operator.Should().Be(">");
        result.LoopStale.Minutes.Should().Be(10);
        result.LoopEnactionStale.Should().BeNull();
    }

    [Fact]
    public void LoopEnactionStale_RoundTrips()
    {
        var node = new ConditionNode("loop_enaction_stale",
            LoopEnactionStale: new LoopEnactionStaleCondition(">=", 15));

        var result = RoundTrip(node);

        result.LoopEnactionStale.Should().NotBeNull();
        result.LoopEnactionStale!.Operator.Should().Be(">=");
        result.LoopEnactionStale.Minutes.Should().Be(15);
    }

    [Fact]
    public void PumpSuspended_RoundTrips()
    {
        var node = new ConditionNode("pump_suspended",
            PumpSuspended: new PumpSuspendedCondition(true, 30));

        var result = RoundTrip(node);

        result.PumpSuspended.Should().NotBeNull();
        result.PumpSuspended!.IsActive.Should().BeTrue();
        result.PumpSuspended.ForMinutes.Should().Be(30);
    }

    [Fact]
    public void PumpSuspended_WireUsesIsActiveField()
    {
        var node = new ConditionNode("pump_suspended",
            PumpSuspended: new PumpSuspendedCondition(true, null));

        var json = JsonSerializer.Serialize(node, Options);

        json.Should().Contain("\"is_active\":true");
    }

    [Fact]
    public void PumpBattery_RoundTrips()
    {
        var node = new ConditionNode("pump_battery", PumpBattery: new PumpBatteryCondition("<", 20m));

        var result = RoundTrip(node);

        result.PumpBattery.Should().NotBeNull();
        result.PumpBattery!.Operator.Should().Be("<");
        result.PumpBattery.Value.Should().Be(20m);
    }

    [Fact]
    public void TempBasal_RoundTrips()
    {
        var node = new ConditionNode("temp_basal",
            TempBasal: new TempBasalCondition(TempBasalMetric.PercentOfScheduled, ">", 150m));

        var result = RoundTrip(node);

        result.TempBasal.Should().NotBeNull();
        result.TempBasal!.Metric.Should().Be(TempBasalMetric.PercentOfScheduled);
        result.TempBasal.Operator.Should().Be(">");
        result.TempBasal.Value.Should().Be(150m);
    }

    [Fact]
    public void TempBasal_MetricWireIsSnakeCase()
    {
        var node = new ConditionNode("temp_basal",
            TempBasal: new TempBasalCondition(TempBasalMetric.PercentOfScheduled, ">", 150m));

        var json = JsonSerializer.Serialize(node, Options);

        json.Should().Contain("\"metric\":\"percent_of_scheduled\"");
    }

    [Fact]
    public void UploaderBattery_RoundTrips()
    {
        var node = new ConditionNode("uploader_battery",
            UploaderBattery: new UploaderBatteryCondition("<=", 15m));

        var result = RoundTrip(node);

        result.UploaderBattery.Should().NotBeNull();
        result.UploaderBattery!.Operator.Should().Be("<=");
        result.UploaderBattery.Value.Should().Be(15m);
    }

    [Fact]
    public void OverrideActive_RoundTrips()
    {
        var node = new ConditionNode("override_active",
            OverrideActive: new OverrideActiveCondition(true, 60));

        var result = RoundTrip(node);

        result.OverrideActive.Should().NotBeNull();
        result.OverrideActive!.IsActive.Should().BeTrue();
        result.OverrideActive.ForMinutes.Should().Be(60);
    }

    [Fact]
    public void SensitivityRatio_RoundTrips()
    {
        var node = new ConditionNode("sensitivity_ratio",
            SensitivityRatio: new SensitivityRatioCondition("<", 0.8m));

        var result = RoundTrip(node);

        result.SensitivityRatio.Should().NotBeNull();
        result.SensitivityRatio!.Operator.Should().Be("<");
        result.SensitivityRatio.Value.Should().Be(0.8m);
    }

    [Fact]
    public void Composite_OfNotSustainedLoopStale_RoundTrips()
    {
        // composite { not { sustained { loop_stale } } } — validates wrapper recursion
        // continues to round-trip with new leaf kinds.
        var loopStale = new ConditionNode("loop_stale", LoopStale: new LoopStaleCondition(">", 10));
        var sustained = new ConditionNode("sustained", Sustained: new SustainedCondition(5, loopStale));
        var not = new ConditionNode("not", Not: new NotCondition(sustained));
        var composite = new ConditionNode("composite",
            Composite: new CompositeCondition("and", new List<ConditionNode> { not }));

        var result = RoundTrip(composite);

        result.Type.Should().Be("composite");
        result.Composite!.Conditions.Should().HaveCount(1);
        var notNode = result.Composite.Conditions[0];
        notNode.Type.Should().Be("not");
        var sustainedNode = notNode.Not!.Child;
        sustainedNode.Type.Should().Be("sustained");
        sustainedNode.Sustained!.Minutes.Should().Be(5);
        var leaf = sustainedNode.Sustained.Child;
        leaf.Type.Should().Be("loop_stale");
        leaf.LoopStale.Should().NotBeNull();
        leaf.LoopStale!.Operator.Should().Be(">");
        leaf.LoopStale.Minutes.Should().Be(10);
    }

    [Fact]
    public void Composite_OfNotSustainedThreshold_RoundTrips()
    {
        // composite { not { sustained { threshold } } }
        var threshold = new ConditionNode("threshold", Threshold: new ThresholdCondition("below", 70m));
        var sustained = new ConditionNode("sustained", Sustained: new SustainedCondition(15, threshold));
        var not = new ConditionNode("not", Not: new NotCondition(sustained));
        var composite = new ConditionNode("composite",
            Composite: new CompositeCondition("and", new List<ConditionNode> { not }));

        var result = RoundTrip(composite);

        result.Type.Should().Be("composite");
        result.Composite.Should().NotBeNull();
        result.Composite!.Operator.Should().Be("and");
        result.Composite.Conditions.Should().HaveCount(1);

        var notNode = result.Composite.Conditions[0];
        notNode.Type.Should().Be("not");
        notNode.Not.Should().NotBeNull();

        var sustainedNode = notNode.Not!.Child;
        sustainedNode.Type.Should().Be("sustained");
        sustainedNode.Sustained.Should().NotBeNull();
        sustainedNode.Sustained!.Minutes.Should().Be(15);

        var thresholdNode = sustainedNode.Sustained.Child;
        thresholdNode.Type.Should().Be("threshold");
        thresholdNode.Threshold!.Direction.Should().Be("below");
        thresholdNode.Threshold.Value.Should().Be(70m);
    }

    [Fact]
    public void Composite_MultiChild_OrThresholdAndPredicted_RoundTrips()
    {
        // composite { or, [ threshold(below, 70), predicted(<, 70, withinMinutes 20) ] }
        var threshold = new ConditionNode("threshold", Threshold: new ThresholdCondition("below", 70m));
        var predicted = new ConditionNode("predicted", Predicted: new PredictedCondition("<", 70m, 20));
        var composite = new ConditionNode("composite",
            Composite: new CompositeCondition("or", new List<ConditionNode> { threshold, predicted }));

        var result = RoundTrip(composite);

        result.Type.Should().Be("composite");
        result.Composite.Should().NotBeNull();
        result.Composite!.Operator.Should().Be("or");
        result.Composite.Conditions.Should().HaveCount(2);

        var first = result.Composite.Conditions[0];
        first.Type.Should().Be("threshold");
        first.Threshold.Should().NotBeNull();
        first.Threshold!.Direction.Should().Be("below");
        first.Threshold.Value.Should().Be(70m);

        var second = result.Composite.Conditions[1];
        second.Type.Should().Be("predicted");
        second.Predicted.Should().NotBeNull();
        second.Predicted!.Operator.Should().Be("<");
        second.Predicted.Value.Should().Be(70m);
        second.Predicted.WithinMinutes.Should().Be(20);
    }
}
