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
    public void ARateClosesOnAFlatTrend_AndAFlatTrendHasNoOpposite()
    {
        const string flat = """{"type":"trend","trend":{"bucket":"flat"}}""";
        Opposite("""{"type":"rate_of_change","rate_of_change":{"direction":"falling","rate":3}}""", flat);
        Opposite("""{"type":"trend","trend":{"bucket":"falling_fast"}}""", flat);
        Opposite(flat, null);
    }

    [Fact]
    public void AStateFlips_AndDropsHowLongItWasHeld() =>
        Opposite(
            """{"type":"pump_suspended","pump_suspended":{"is_active":true,"for_minutes":30}}""",
            """{"type":"pump_suspended","pump_suspended":{"is_active":false}}""");

    [Fact]
    public void AnotherAlertStartedClosesOnItAcknowledged() =>
        Opposite(
            """{"type":"alert_state","alert_state":{"alert_id":"r","state":"firing","for_minutes":10}}""",
            """{"type":"alert_state","alert_state":{"alert_id":"r","state":"acknowledged","for_minutes":10}}""");

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
