using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Evaluators;

[Trait("Category", "Unit")]
public class ConditionNodePayloadsTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void NullPayload_ReturnsEmptyObject()
    {
        // Type set but the matching payload field is null — preserves the silent fail-mode
        // expected by recursive evaluators when a malformed node arrives.
        var node = new ConditionNode("threshold");

        ConditionNodePayloads.SerializeChildPayload(node, Options).Should().Be("{}");
    }

    [Fact]
    public void UnknownType_ReturnsEmptyObject()
    {
        var node = new ConditionNode("does_not_exist");

        ConditionNodePayloads.SerializeChildPayload(node, Options).Should().Be("{}");
    }

    [Fact]
    public void Threshold_SerialisesPayload()
    {
        var node = new ConditionNode("threshold", Threshold: new ThresholdCondition("below", 70m));

        var json = ConditionNodePayloads.SerializeChildPayload(node, Options);

        var roundTripped = JsonSerializer.Deserialize<ThresholdCondition>(json, Options);
        roundTripped.Should().BeEquivalentTo(new ThresholdCondition("below", 70m));
    }

    [Fact]
    public void RateOfChange_SerialisesPayload()
    {
        var node = new ConditionNode("rate_of_change", RateOfChange: new RateOfChangeCondition("falling", 3.0m));

        var json = ConditionNodePayloads.SerializeChildPayload(node, Options);

        JsonSerializer.Deserialize<RateOfChangeCondition>(json, Options)
            .Should().BeEquivalentTo(new RateOfChangeCondition("falling", 3.0m));
    }

    [Fact]
    public void SignalLoss_SerialisesPayload()
    {
        var node = new ConditionNode("signal_loss", SignalLoss: new SignalLossCondition(20));

        var json = ConditionNodePayloads.SerializeChildPayload(node, Options);

        JsonSerializer.Deserialize<SignalLossCondition>(json, Options)
            .Should().BeEquivalentTo(new SignalLossCondition(20));
    }

    [Fact]
    public void Composite_SerialisesPayload()
    {
        var inner = new CompositeCondition("and", new List<ConditionNode>
        {
            new("threshold", Threshold: new ThresholdCondition("below", 70m)),
        });
        var node = new ConditionNode("composite", Composite: inner);

        var json = ConditionNodePayloads.SerializeChildPayload(node, Options);

        var roundTripped = JsonSerializer.Deserialize<CompositeCondition>(json, Options);
        roundTripped!.Operator.Should().Be("and");
        roundTripped.Conditions.Should().HaveCount(1);
    }

    [Fact]
    public void Not_SerialisesPayload()
    {
        var inner = new NotCondition(new ConditionNode("threshold", Threshold: new ThresholdCondition("below", 70m)));
        var node = new ConditionNode("not", Not: inner);

        var json = ConditionNodePayloads.SerializeChildPayload(node, Options);

        var roundTripped = JsonSerializer.Deserialize<NotCondition>(json, Options);
        roundTripped!.Child.Type.Should().Be("threshold");
    }

    [Fact]
    public void Sustained_SerialisesPayload()
    {
        var inner = new SustainedCondition(10, new ConditionNode("threshold", Threshold: new ThresholdCondition("below", 70m)));
        var node = new ConditionNode("sustained", Sustained: inner);

        var json = ConditionNodePayloads.SerializeChildPayload(node, Options);

        var roundTripped = JsonSerializer.Deserialize<SustainedCondition>(json, Options);
        roundTripped!.Minutes.Should().Be(10);
        roundTripped.Child.Type.Should().Be("threshold");
    }

    [Fact]
    public void Staleness_SerialisesPayload()
    {
        var node = new ConditionNode("staleness", Staleness: new StalenessCondition(">=", 15));

        var json = ConditionNodePayloads.SerializeChildPayload(node, Options);

        JsonSerializer.Deserialize<StalenessCondition>(json, Options)
            .Should().BeEquivalentTo(new StalenessCondition(">=", 15));
    }

    [Fact]
    public void Predicted_SerialisesPayload()
    {
        var node = new ConditionNode("predicted", Predicted: new PredictedCondition(">", 200m, 30));

        var json = ConditionNodePayloads.SerializeChildPayload(node, Options);

        JsonSerializer.Deserialize<PredictedCondition>(json, Options)
            .Should().BeEquivalentTo(new PredictedCondition(">", 200m, 30));
    }

    [Fact]
    public void Trend_SerialisesPayload()
    {
        var node = new ConditionNode("trend", Trend: new TrendCondition("rising_fast"));

        var json = ConditionNodePayloads.SerializeChildPayload(node, Options);

        JsonSerializer.Deserialize<TrendCondition>(json, Options)
            .Should().BeEquivalentTo(new TrendCondition("rising_fast"));
    }

    [Fact]
    public void TimeOfDay_SerialisesPayload()
    {
        var node = new ConditionNode("time_of_day", TimeOfDay: new TimeOfDayCondition("22:00", "06:00", "UTC"));

        var json = ConditionNodePayloads.SerializeChildPayload(node, Options);

        JsonSerializer.Deserialize<TimeOfDayCondition>(json, Options)
            .Should().BeEquivalentTo(new TimeOfDayCondition("22:00", "06:00", "UTC"));
    }

    [Fact]
    public void Iob_SerialisesPayload()
    {
        var node = new ConditionNode("iob", Iob: new IobCondition(">", 5m));

        var json = ConditionNodePayloads.SerializeChildPayload(node, Options);

        JsonSerializer.Deserialize<IobCondition>(json, Options)
            .Should().BeEquivalentTo(new IobCondition(">", 5m));
    }

    [Fact]
    public void Cob_SerialisesPayload()
    {
        var node = new ConditionNode("cob", Cob: new CobCondition("<", 30m));

        var json = ConditionNodePayloads.SerializeChildPayload(node, Options);

        JsonSerializer.Deserialize<CobCondition>(json, Options)
            .Should().BeEquivalentTo(new CobCondition("<", 30m));
    }

    [Fact]
    public void Reservoir_SerialisesPayload()
    {
        var node = new ConditionNode("reservoir", Reservoir: new ReservoirCondition("<", 20m));

        var json = ConditionNodePayloads.SerializeChildPayload(node, Options);

        JsonSerializer.Deserialize<ReservoirCondition>(json, Options)
            .Should().BeEquivalentTo(new ReservoirCondition("<", 20m));
    }

    [Fact]
    public void SiteAge_SerialisesPayload()
    {
        var node = new ConditionNode("site_age", SiteAge: new SiteAgeCondition(">=", 72m));

        var json = ConditionNodePayloads.SerializeChildPayload(node, Options);

        JsonSerializer.Deserialize<SiteAgeCondition>(json, Options)
            .Should().BeEquivalentTo(new SiteAgeCondition(">=", 72m));
    }

    [Fact]
    public void SensorAge_SerialisesPayload()
    {
        var node = new ConditionNode("sensor_age", SensorAge: new SensorAgeCondition(">=", 10m));

        var json = ConditionNodePayloads.SerializeChildPayload(node, Options);

        JsonSerializer.Deserialize<SensorAgeCondition>(json, Options)
            .Should().BeEquivalentTo(new SensorAgeCondition(">=", 10m));
    }

    [Fact]
    public void AlertState_SerialisesPayload()
    {
        var alertId = Guid.NewGuid();
        var node = new ConditionNode("alert_state", AlertState: new AlertStateCondition(alertId, "firing", 5));

        var json = ConditionNodePayloads.SerializeChildPayload(node, Options);

        JsonSerializer.Deserialize<AlertStateCondition>(json, Options)
            .Should().BeEquivalentTo(new AlertStateCondition(alertId, "firing", 5));
    }
}
