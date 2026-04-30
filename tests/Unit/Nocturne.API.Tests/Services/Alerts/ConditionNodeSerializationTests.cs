using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

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
}
