using FluentAssertions;
using Nocturne.API.Services.Alerts.Conditions;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Conditions;

[Trait("Category", "Unit")]
public class AutoResolveSuggestionTests
{
    private static void Opposite(string condition, string? expected)
    {
        var suggestion = AutoResolveSuggestion.For(condition);
        if (expected is null)
            suggestion.Should().BeNull();
        else
            ConditionTreeEquality.Same(suggestion, expected).Should().BeTrue(suggestion);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("""{"type":"unknown","unknown":{}}""")]
    [InlineData("""{"type":"threshold"}""")]
    public void NoTreeOrOneItCannotReadHasNoOpposite(string? condition) =>
        AutoResolveSuggestion.For(condition).Should().BeNull();

    [Theory]
    [InlineData("below", "above")]
    [InlineData("above", "below")]
    public void AGlucoseLevelClosesOnTheOtherSideOfTheSameLevel(string direction, string opposite) =>
        Opposite(
            $$$"""{"type":"threshold","threshold":{"direction":"{{{direction}}}","value":70.0}}""",
            $$$"""{"type":"threshold","threshold":{"direction":"{{{opposite}}}","value":70.0}}""");

    [Fact]
    public void AnOppositeCarriesNoClearMarginSinceAResolveTreeReadsNone() =>
        Opposite(
            """
            {"type":"composite","composite":{"operator":"and","conditions":[
                {"type":"threshold","threshold":{"direction":"below","value":70,"clear_margin":5}},
                {"type":"iob","iob":{"operator":">","value":2,"clear_margin":1}},
                {"type":"rate_of_change","rate_of_change":{"direction":"falling","rate":2,"clear_margin":1}}]}}
            """,
            """
            {"type":"composite","composite":{"operator":"or","conditions":[
                {"type":"threshold","threshold":{"direction":"above","value":70}},
                {"type":"iob","iob":{"operator":"<=","value":2}},
                {"type":"not","not":{"child":{"type":"rate_of_change","rate_of_change":{"direction":"falling","rate":2}}}}]}}
            """);

    [Theory]
    [InlineData("<", ">=")]
    [InlineData("<=", ">")]
    [InlineData(">", "<=")]
    [InlineData(">=", "<")]
    public void AComparisonIsTurnedRoundAndKeepsItsOtherFields(string op, string opposite)
    {
        Opposite(
            $$$"""{"type":"reservoir","reservoir":{"operator":"{{{op}}}","value":20}}""",
            $$$"""{"type":"reservoir","reservoir":{"operator":"{{{opposite}}}","value":20}}""");
        Opposite(
            $$$"""{"type":"tracker_age","tracker_age":{"tracker_definition_id":"a","operator":"{{{op}}}","minutes":600}}""",
            $$$"""{"type":"tracker_age","tracker_age":{"tracker_definition_id":"a","operator":"{{{opposite}}}","minutes":600}}""");
        Opposite(
            $$$"""{"type":"transmitter_battery","transmitter_battery":{"metric":"voltage_a","operator":"{{{op}}}","value":300}}""",
            $$$"""{"type":"transmitter_battery","transmitter_battery":{"metric":"voltage_a","operator":"{{{opposite}}}","value":300}}""");
    }

    [Fact]
    public void AComparisonItDoesNotKnowHasNoOpposite() =>
        Opposite("""{"type":"iob","iob":{"operator":"==","value":2}}""", null);

    [Fact]
    public void AForecastClosesOnItsNegationBecauseTheTurnedComparisonIsTrueTogetherWithIt()
    {
        const string forecast = """{"type":"predicted","predicted":{"operator":"<=","value":55,"within_minutes":20}}""";
        Opposite(forecast, $$$"""{"type":"not","not":{"child": {{{forecast}}} }}""");
    }

    [Fact]
    public void ANegationClosesOnWhatItNegates() =>
        Opposite(
            """{"type":"not","not":{"child":{"type":"threshold","threshold":{"direction":"below","value":70}}}}""",
            """{"type":"threshold","threshold":{"direction":"below","value":70}}""");

    [Fact]
    public void ARateClosesOnItsNegationBecauseASlowRateIsTrueInsideTheFlatTrend()
    {
        const string rate = """{"type":"rate_of_change","rate_of_change":{"direction":"falling","rate":1}}""";
        Opposite(rate, $$$"""{"type":"not","not":{"child": {{{rate}}} }}""");
    }

    [Fact]
    public void ATrendClosesOnTheFlatBucket_AndTheFlatBucketHasNoOpposite()
    {
        const string flat = """{"type":"trend","trend":{"bucket":"flat"}}""";
        Opposite("""{"type":"trend","trend":{"bucket":"falling_fast"}}""", flat);
        Opposite(flat, null);
    }

    [Fact]
    public void AStateFlips_AndDropsHowLongItWasHeld() =>
        Opposite(
            """{"type":"pump_suspended","pump_suspended":{"is_active":true,"for_minutes":30}}""",
            """{"type":"pump_suspended","pump_suspended":{"is_active":false}}""");

    [Fact]
    public void AnotherAlertNotAcknowledgedClosesOnItAcknowledged() =>
        Opposite(
            """{"type":"alert_state","alert_state":{"alert_id":"r","state":"unacknowledged","for_minutes":10}}""",
            """{"type":"alert_state","alert_state":{"alert_id":"r","state":"acknowledged","for_minutes":10}}""");

    [Theory]
    [InlineData("firing")]
    [InlineData("acknowledged")]
    public void AnotherAlertStartedOrAcknowledgedClosesOnItsNegationBecauseBothAreTrueOfOneAlert(string state)
    {
        var other = $$$"""{"type":"alert_state","alert_state":{"alert_id":"r","state":"{{{state}}}"}}""";
        Opposite(other, $$$"""{"type":"not","not":{"child": {{{other}}} }}""");
    }

    [Fact]
    public void AnOrWithAConditionThatHasNoOppositeHasNone()
    {
        const string low = """{"type":"threshold","threshold":{"direction":"below","value":70}}""";
        const string lost = """{"type":"signal_loss","signal_loss":{"timeout_minutes":20}}""";
        const string iob = """{"type":"iob","iob":{"operator":">","value":2}}""";

        Opposite($$$"""{"type":"composite","composite":{"operator":"or","conditions":[ {{{low}}} , {{{lost}}} ]}}""", null);
        Opposite(
            $$$"""{"type":"composite","composite":{"operator":"and","conditions":[{"type":"composite","composite":{"operator":"or","conditions":[ {{{low}}} , {{{lost}}} ]}}, {{{iob}}} ]}}""",
            """{"type":"iob","iob":{"operator":"<=","value":2}}""");
    }

    /// <summary>
    /// The engine closes an alert on the evaluation that finds its resolve tree true, so an
    /// opposite that can be true together with its condition closes the alert as it starts. Every
    /// opposite of one condition is therefore one of the forms that cannot be: the negation of
    /// the condition, the same condition with its comparison, side or switch turned round, or
    /// another value of a fact that has one value at a time.
    /// </summary>
    [Theory]
    [InlineData("""{"type":"threshold","threshold":{"direction":"below","value":70}}""", "direction")]
    [InlineData("""{"type":"rate_of_change","rate_of_change":{"direction":"rising","rate":0.5}}""", "not")]
    [InlineData("""{"type":"trend","trend":{"bucket":"rising"}}""", "bucket")]
    [InlineData("""{"type":"predicted","predicted":{"operator":"<=","value":55,"within_minutes":20}}""", "not")]
    [InlineData("""{"type":"predicted","predicted":{"operator":"<","value":70,"within_minutes":30,"min_of":["iob","zt"]}}""", "not")]
    [InlineData("""{"type":"eventual_glucose","eventual_glucose":{"operator":"<","value":80}}""", "operator")]
    [InlineData("""{"type":"carbs_required","carbs_required":{"operator":">=","value":10}}""", "operator")]
    [InlineData("""{"type":"staleness","staleness":{"operator":">","value":15}}""", "operator")]
    [InlineData("""{"type":"iob","iob":{"operator":">","value":2}}""", "operator")]
    [InlineData("""{"type":"cob","cob":{"operator":">","value":20}}""", "operator")]
    [InlineData("""{"type":"reservoir","reservoir":{"operator":"<=","value":20}}""", "operator")]
    [InlineData("""{"type":"site_age","site_age":{"operator":">=","value":72}}""", "operator")]
    [InlineData("""{"type":"sensor_age","sensor_age":{"operator":">=","value":10}}""", "operator")]
    [InlineData("""{"type":"pump_battery","pump_battery":{"operator":"<=","value":20}}""", "operator")]
    [InlineData("""{"type":"uploader_battery","uploader_battery":{"operator":"<=","value":20}}""", "operator")]
    [InlineData("""{"type":"transmitter_battery","transmitter_battery":{"metric":"voltage_a","operator":"<","value":300}}""", "operator")]
    [InlineData("""{"type":"sensitivity_ratio","sensitivity_ratio":{"operator":">","value":1.2}}""", "operator")]
    [InlineData("""{"type":"tracker_age","tracker_age":{"tracker_definition_id":"a","operator":">=","minutes":600}}""", "operator")]
    [InlineData("""{"type":"pump_suspended","pump_suspended":{"is_active":true}}""", "is_active")]
    [InlineData("""{"type":"override_active","override_active":{"is_active":true}}""", "is_active")]
    [InlineData("""{"type":"do_not_disturb","do_not_disturb":{"is_active":false}}""", "is_active")]
    [InlineData("""{"type":"alert_state","alert_state":{"alert_id":"r","state":"firing"}}""", "not")]
    [InlineData("""{"type":"alert_state","alert_state":{"alert_id":"r","state":"acknowledged"}}""", "not")]
    [InlineData("""{"type":"alert_state","alert_state":{"alert_id":"r","state":"unacknowledged"}}""", "state")]
    public void AnOppositeIsNeverTrueTogetherWithItsCondition(string condition, string differsIn)
    {
        var firing = System.Text.Json.Nodes.JsonNode.Parse(condition)!.AsObject();
        var type = firing["type"]!.GetValue<string>();
        var opposite = AutoResolveSuggestion.For(firing);

        opposite.Should().NotBeNull();
        if (differsIn == "not")
        {
            opposite!["type"]!.GetValue<string>().Should().Be("not");
            ConditionTreeEquality.Same(opposite["not"]!["child"]!.ToJsonString(), condition).Should().BeTrue();
            return;
        }

        opposite!["type"]!.GetValue<string>().Should().Be(type);
        var before = firing[type]!.AsObject();
        var after = opposite[type]!.AsObject();
        after[differsIn]!.ToJsonString().Should().NotBe(before[differsIn]!.ToJsonString());
        foreach (var (name, value) in before.Where(p => p.Key != differsIn))
            after[name]!.ToJsonString().Should().Be(value!.ToJsonString(), $"the opposite reads the same {name}");
    }

    [Theory]
    [InlineData("""{"type":"loop_stale","loop_stale":{"operator":">","minutes":30}}""")]
    [InlineData("""{"type":"signal_loss","signal_loss":{"timeout_minutes":20}}""")]
    [InlineData("""{"type":"time_of_day","time_of_day":{"from":"22:00","to":"07:00"}}""")]
    [InlineData("""{"type":"temp_basal","temp_basal":{"metric":"rate","operator":">=","value":1}}""")]
    public void AConditionWithNoClearOppositeHasNone(string condition) => Opposite(condition, null);

    [Fact]
    public void AWaitIsDropped() =>
        Opposite(
            """{"type":"sustained","sustained":{"minutes":15,"child":{"type":"threshold","threshold":{"direction":"above","value":250}}}}""",
            """{"type":"threshold","threshold":{"direction":"below","value":250}}""");

    [Fact]
    public void AnAndOfConditionsClosesWhenAnyOneIsNoLongerTrue_AndAnOrWhenNoneIs()
    {
        const string low = """{"type":"threshold","threshold":{"direction":"below","value":70}}""";
        const string notLow = """{"type":"threshold","threshold":{"direction":"above","value":70}}""";
        const string iob = """{"type":"iob","iob":{"operator":">","value":2}}""";
        const string notIob = """{"type":"iob","iob":{"operator":"<=","value":2}}""";

        Opposite(
            $$$"""{"type":"composite","composite":{"operator":"and","conditions":[ {{{low}}} , {{{iob}}} ]}}""",
            $$$"""{"type":"composite","composite":{"operator":"or","conditions":[ {{{notLow}}} , {{{notIob}}} ]}}""");
        Opposite(
            $$$"""{"type":"composite","composite":{"operator":"or","conditions":[ {{{low}}} , {{{iob}}} ]}}""",
            $$$"""{"type":"composite","composite":{"operator":"and","conditions":[ {{{notLow}}} , {{{notIob}}} ]}}""");
    }

    [Fact]
    public void AGroupLeavesOutWhatHasNoOpposite_AndOneLeftStandsAlone()
    {
        const string hours = """{"type":"time_of_day","time_of_day":{"from":"22:00","to":"07:00"}}""";
        Opposite(
            $$$"""{"type":"composite","composite":{"operator":"and","conditions":[{"type":"sensor_age","sensor_age":{"operator":">=","value":10}}, {{{hours}}} ]}}""",
            """{"type":"sensor_age","sensor_age":{"operator":"<","value":10}}""");
        Opposite($$$"""{"type":"composite","composite":{"operator":"and","conditions":[ {{{hours}}} ]}}""", null);
    }
}
