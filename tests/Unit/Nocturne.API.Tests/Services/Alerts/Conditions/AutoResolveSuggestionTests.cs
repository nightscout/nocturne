using FluentAssertions;
using Nocturne.API.Services.Alerts.Conditions;
using Nocturne.Core.Models.Alerts;
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

    private static string Root(string node) => ConditionSchema.Stamp(node);

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("""{"schema_version":2,"type":"unknown","unknown":{}}""")]
    [InlineData("""{"schema_version":2,"type":"threshold"}""")]
    public void NoTreeOrOneItCannotReadHasNoOpposite(string? condition) =>
        AutoResolveSuggestion.For(condition).Should().BeNull();

    [Theory]
    [InlineData("<", ">")]
    [InlineData(">", "<")]
    [InlineData("<=", ">")]
    [InlineData(">=", "<")]
    public void AGlucoseLevelClosesOnTheOtherSideOfTheSameLevel(string op, string opposite) =>
        Opposite(
            $$$"""{"schema_version":2,"type":"threshold","threshold":{"operator":"{{{op}}}","value":70.0}}""",
            $$$"""{"schema_version":2,"type":"threshold","threshold":{"operator":"{{{opposite}}}","value":70.0}}""");

    [Fact]
    public void AnOppositeCarriesNoClearMarginSinceAResolveTreeReadsNone() =>
        Opposite(
            """
            {"schema_version":2,"type":"composite","composite":{"operator":"and","conditions":[
                {"type":"threshold","threshold":{"operator":"<","value":70,"clear_margin":5}},
                {"type":"iob","iob":{"operator":">","value":2,"clear_margin":1}},
                {"type":"rate_of_change","rate_of_change":{"operator":"<=","value":-2,"clear_margin":1}}]}}
            """,
            """
            {"schema_version":2,"type":"composite","composite":{"operator":"or","conditions":[
                {"type":"threshold","threshold":{"operator":">","value":70}},
                {"type":"iob","iob":{"operator":"<=","value":2}},
                {"type":"not","not":{"child":{"type":"rate_of_change","rate_of_change":{"operator":"<=","value":-2}}}}]}}
            """);

    [Theory]
    [InlineData("<", ">=")]
    [InlineData("<=", ">")]
    [InlineData(">", "<=")]
    [InlineData(">=", "<")]
    public void AComparisonIsTurnedRoundAndKeepsItsOtherFields(string op, string opposite)
    {
        Opposite(
            $$$"""{"schema_version":2,"type":"reservoir","reservoir":{"operator":"{{{op}}}","value":20}}""",
            $$$"""{"schema_version":2,"type":"reservoir","reservoir":{"operator":"{{{opposite}}}","value":20}}""");
        Opposite(
            $$$"""{"schema_version":2,"type":"tracker_age","tracker_age":{"tracker_definition_id":"a","operator":"{{{op}}}","minutes":600}}""",
            $$$"""{"schema_version":2,"type":"tracker_age","tracker_age":{"tracker_definition_id":"a","operator":"{{{opposite}}}","minutes":600}}""");
        Opposite(
            $$$"""{"schema_version":2,"type":"transmitter_battery","transmitter_battery":{"metric":"voltage_a","operator":"{{{op}}}","value":300}}""",
            $$$"""{"schema_version":2,"type":"transmitter_battery","transmitter_battery":{"metric":"voltage_a","operator":"{{{opposite}}}","value":300}}""");
    }

    [Fact]
    public void AComparisonItDoesNotKnowHasNoOpposite() =>
        Opposite("""{"schema_version":2,"type":"iob","iob":{"operator":"==","value":2}}""", null);

    [Fact]
    public void AForecastClosesOnItsNegationBecauseTheTurnedComparisonIsTrueTogetherWithIt()
    {
        const string forecast = """{"type":"predicted","predicted":{"operator":"<=","value":55,"within_minutes":20}}""";
        Opposite(Root(forecast), $$$"""{"schema_version":2,"type":"not","not":{"child": {{{forecast}}} }}""");
    }

    [Fact]
    public void ANegationClosesOnWhatItNegates() =>
        Opposite(
            """{"schema_version":2,"type":"not","not":{"child":{"type":"threshold","threshold":{"operator":"<","value":70}}}}""",
            """{"schema_version":2,"type":"threshold","threshold":{"operator":"<","value":70}}""");

    [Fact]
    public void ARateClosesOnItsNegationBecauseASlowRateIsTrueInsideTheFlatTrend()
    {
        const string rate = """{"type":"rate_of_change","rate_of_change":{"operator":"<=","value":-1}}""";
        Opposite(Root(rate), $$$"""{"schema_version":2,"type":"not","not":{"child": {{{rate}}} }}""");
    }

    [Fact]
    public void ATrendClosesOnTheFlatBucket_AndTheFlatBucketHasNoOpposite()
    {
        const string flat = """{"schema_version":2,"type":"trend","trend":{"bucket":"flat"}}""";
        Opposite("""{"schema_version":2,"type":"trend","trend":{"bucket":"falling_fast"}}""", flat);
        Opposite(flat, null);
    }

    [Fact]
    public void AStateFlips_AndDropsHowLongItWasHeld() =>
        Opposite(
            """{"schema_version":2,"type":"pump_suspended","pump_suspended":{"is_active":true,"for_minutes":30}}""",
            """{"schema_version":2,"type":"pump_suspended","pump_suspended":{"is_active":false}}""");

    [Fact]
    public void AnotherAlertNotAcknowledgedClosesOnItAcknowledged() =>
        Opposite(
            """{"schema_version":2,"type":"alert_state","alert_state":{"alert_id":"r","state":"unacknowledged","for_minutes":10}}""",
            """{"schema_version":2,"type":"alert_state","alert_state":{"alert_id":"r","state":"acknowledged","for_minutes":10}}""");

    [Theory]
    [InlineData("firing")]
    [InlineData("acknowledged")]
    public void AnotherAlertStartedOrAcknowledgedClosesOnItsNegationBecauseBothAreTrueOfOneAlert(string state)
    {
        var other = $$$"""{"type":"alert_state","alert_state":{"alert_id":"r","state":"{{{state}}}"}}""";
        Opposite(Root(other), $$$"""{"schema_version":2,"type":"not","not":{"child": {{{other}}} }}""");
    }

    [Fact]
    public void AnOrWithAConditionThatHasNoOppositeHasNone()
    {
        const string low = """{"type":"threshold","threshold":{"operator":"<","value":70}}""";
        const string lost = """{"type":"signal_loss","signal_loss":{"timeout_minutes":20}}""";
        const string iob = """{"type":"iob","iob":{"operator":">","value":2}}""";

        Opposite($$$"""{"schema_version":2,"type":"composite","composite":{"operator":"or","conditions":[ {{{low}}} , {{{lost}}} ]}}""", null);
        Opposite(
            $$$"""{"schema_version":2,"type":"composite","composite":{"operator":"and","conditions":[{"type":"composite","composite":{"operator":"or","conditions":[ {{{low}}} , {{{lost}}} ]}}, {{{iob}}} ]}}""",
            """{"schema_version":2,"type":"iob","iob":{"operator":"<=","value":2}}""");
    }

    /// <summary>
    /// The engine closes an alert on the evaluation that finds its resolve tree true, so an
    /// opposite that can be true together with its condition closes the alert as it starts. Every
    /// opposite of one condition is therefore one of the forms that cannot be: the negation of
    /// the condition, the same condition with its comparison, side or switch turned round, or
    /// another value of a fact that has one value at a time.
    /// </summary>
    [Theory]
    [InlineData("""{"schema_version":2,"type":"threshold","threshold":{"operator":"<","value":70}}""", "operator")]
    [InlineData("""{"schema_version":2,"type":"rate_of_change","rate_of_change":{"operator":">=","value":0.5}}""", "not")]
    [InlineData("""{"schema_version":2,"type":"trend","trend":{"bucket":"rising"}}""", "bucket")]
    [InlineData("""{"schema_version":2,"type":"predicted","predicted":{"operator":"<=","value":55,"within_minutes":20}}""", "not")]
    [InlineData("""{"schema_version":2,"type":"predicted","predicted":{"operator":"<","value":70,"within_minutes":30,"min_of":["iob","zt"]}}""", "not")]
    [InlineData("""{"schema_version":2,"type":"eventual_glucose","eventual_glucose":{"operator":"<","value":80}}""", "operator")]
    [InlineData("""{"schema_version":2,"type":"carbs_required","carbs_required":{"operator":">=","value":10}}""", "operator")]
    [InlineData("""{"schema_version":2,"type":"glucose_change","glucose_change":{"operator":"<=","value":-30,"window_minutes":15}}""", "operator")]
    [InlineData("""{"schema_version":2,"type":"glucose_extreme","glucose_extreme":{"of":"max","operator":">","value":250,"window_minutes":60}}""", "operator")]
    [InlineData("""{"schema_version":2,"type":"staleness","staleness":{"operator":">","minutes":15}}""", "operator")]
    [InlineData("""{"schema_version":2,"type":"iob","iob":{"operator":">","value":2}}""", "operator")]
    [InlineData("""{"schema_version":2,"type":"cob","cob":{"operator":">","value":20}}""", "operator")]
    [InlineData("""{"schema_version":2,"type":"reservoir","reservoir":{"operator":"<=","value":20}}""", "operator")]
    [InlineData("""{"schema_version":2,"type":"site_age","site_age":{"operator":">=","minutes":4320}}""", "operator")]
    [InlineData("""{"schema_version":2,"type":"sensor_age","sensor_age":{"operator":">=","minutes":14400}}""", "operator")]
    [InlineData("""{"schema_version":2,"type":"pump_battery","pump_battery":{"operator":"<=","value":20}}""", "operator")]
    [InlineData("""{"schema_version":2,"type":"uploader_battery","uploader_battery":{"operator":"<=","value":20}}""", "operator")]
    [InlineData("""{"schema_version":2,"type":"transmitter_battery","transmitter_battery":{"metric":"voltage_a","operator":"<","value":300}}""", "operator")]
    [InlineData("""{"schema_version":2,"type":"sensitivity_ratio","sensitivity_ratio":{"operator":">","value":1.2}}""", "operator")]
    [InlineData("""{"schema_version":2,"type":"tracker_age","tracker_age":{"tracker_definition_id":"a","operator":">=","minutes":600}}""", "operator")]
    [InlineData("""{"schema_version":2,"type":"pump_suspended","pump_suspended":{"is_active":true}}""", "is_active")]
    [InlineData("""{"schema_version":2,"type":"override_active","override_active":{"is_active":true}}""", "is_active")]
    [InlineData("""{"schema_version":2,"type":"do_not_disturb","do_not_disturb":{"is_active":false}}""", "is_active")]
    [InlineData("""{"schema_version":2,"type":"alert_state","alert_state":{"alert_id":"r","state":"firing"}}""", "not")]
    [InlineData("""{"schema_version":2,"type":"alert_state","alert_state":{"alert_id":"r","state":"acknowledged"}}""", "not")]
    [InlineData("""{"schema_version":2,"type":"alert_state","alert_state":{"alert_id":"r","state":"unacknowledged"}}""", "state")]
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
    [InlineData("""{"schema_version":2,"type":"loop_stale","loop_stale":{"operator":">","minutes":30}}""")]
    [InlineData("""{"schema_version":2,"type":"signal_loss","signal_loss":{"timeout_minutes":20}}""")]
    [InlineData("""{"schema_version":2,"type":"time_of_day","time_of_day":{"from":"22:00","to":"07:00"}}""")]
    [InlineData("""{"schema_version":2,"type":"temp_basal","temp_basal":{"metric":"rate","operator":">=","value":1}}""")]
    public void AConditionWithNoClearOppositeHasNone(string condition) => Opposite(condition, null);

    /// <summary>
    /// A count closes when its tally no longer meets its comparison: the tally's comparison turns,
    /// and the one each reading meets stays as it was.
    /// </summary>
    [Theory]
    [InlineData("count")]
    [InlineData("percent")]
    public void ACountClosesOnItsTallyTurnedRound(string tally) =>
        Opposite(
            $$$$"""{"schema_version":2,"type":"glucose_count","glucose_count":{"reading":{"operator":"<","value":70},"window_minutes":60,"{{{{tally}}}}":{"operator":">=","value":3}}}""",
            $$$$"""{"schema_version":2,"type":"glucose_count","glucose_count":{"reading":{"operator":"<","value":70},"window_minutes":60,"{{{{tally}}}}":{"operator":"<","value":3}}}""");

    [Theory]
    [InlineData("""{"schema_version":2,"type":"glucose_count","glucose_count":{"reading":{"operator":"<","value":70},"window_minutes":60,"count":{"operator":"==","value":3}}}""")]
    [InlineData("""{"schema_version":2,"type":"glucose_count","glucose_count":{"reading":{"operator":"<","value":70},"window_minutes":60}}""")]
    public void ACountWithNoTallyToTurnHasNoOpposite(string condition) => Opposite(condition, null);

    [Fact]
    public void AWaitIsDropped() =>
        Opposite(
            """{"schema_version":2,"type":"sustained","sustained":{"minutes":15,"child":{"type":"threshold","threshold":{"operator":">","value":250}}}}""",
            """{"schema_version":2,"type":"threshold","threshold":{"operator":"<","value":250}}""");

    [Fact]
    public void AnAndOfConditionsClosesWhenAnyOneIsNoLongerTrue_AndAnOrWhenNoneIs()
    {
        const string low = """{"type":"threshold","threshold":{"operator":"<","value":70}}""";
        const string notLow = """{"type":"threshold","threshold":{"operator":">","value":70}}""";
        const string iob = """{"type":"iob","iob":{"operator":">","value":2}}""";
        const string notIob = """{"type":"iob","iob":{"operator":"<=","value":2}}""";

        Opposite(
            $$$"""{"schema_version":2,"type":"composite","composite":{"operator":"and","conditions":[ {{{low}}} , {{{iob}}} ]}}""",
            $$$"""{"schema_version":2,"type":"composite","composite":{"operator":"or","conditions":[ {{{notLow}}} , {{{notIob}}} ]}}""");
        Opposite(
            $$$"""{"schema_version":2,"type":"composite","composite":{"operator":"or","conditions":[ {{{low}}} , {{{iob}}} ]}}""",
            $$$"""{"schema_version":2,"type":"composite","composite":{"operator":"and","conditions":[ {{{notLow}}} , {{{notIob}}} ]}}""");
    }

    [Fact]
    public void AGroupLeavesOutWhatHasNoOpposite_AndOneLeftStandsAlone()
    {
        const string hours = """{"type":"time_of_day","time_of_day":{"from":"22:00","to":"07:00"}}""";
        Opposite(
            $$$"""{"schema_version":2,"type":"composite","composite":{"operator":"and","conditions":[{"type":"sensor_age","sensor_age":{"operator":">=","minutes":14400}}, {{{hours}}} ]}}""",
            """{"schema_version":2,"type":"sensor_age","sensor_age":{"operator":"<","minutes":14400}}""");
        Opposite($$$"""{"schema_version":2,"type":"composite","composite":{"operator":"and","conditions":[ {{{hours}}} ]}}""", null);
    }
}
