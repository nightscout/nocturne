using System.Net;
using System.Text;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Parity.V2;
using Xunit;

namespace Nocturne.API.Tests.Integration.Infrastructure;

public class ResponseComparerReloadLagToleranceTests
{
    private static readonly ResponseComparer Tolerant =
        new(ComparisonOptions.Default.WithReloadLagTolerance());

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static async Task<bool> MatchesAsync(ResponseComparer comparer, string expected, string actual) =>
        (await comparer.CompareAsync(Json(expected), Json(actual))).IsMatch;

    [Theory]
    [InlineData("""{"state":{"iob":3.89}}""", """{"state":{"iob":3.88}}""")]
    [InlineData("""{"state":{"bwp":-3.7}}""", """{"state":{"bwp":-3.69}}""")]
    [InlineData("""{"iob":{"display":"3.89"}}""", """{"iob":{"display":"3.88"}}""")]
    [InlineData("""{"iob":{"displayLine":"IOB: 3.89U"}}""", """{"iob":{"displayLine":"IOB: 3.88U"}}""")]
    public async Task A_difference_of_one_hundredth_on_an_IOB_path_matches(string expected, string actual)
    {
        (await MatchesAsync(Tolerant, expected, actual)).Should().BeTrue();
        (await MatchesAsync(Tolerant, actual, expected)).Should().BeTrue();
    }

    [Theory]
    [InlineData("""{"state":{"iob":3.89}}""", """{"state":{"iob":3.87}}""")]
    [InlineData("""{"state":{"bwp":-3.7}}""", """{"state":{"bwp":-3.68}}""")]
    [InlineData("""{"iob":{"display":"3.89"}}""", """{"iob":{"display":"3.87"}}""")]
    [InlineData("""{"iob":{"displayLine":"IOB: 3.89U"}}""", """{"iob":{"displayLine":"IOB: 3.87U"}}""")]
    public async Task A_difference_above_one_hundredth_on_an_IOB_path_does_not_match(string expected, string actual)
    {
        (await MatchesAsync(Tolerant, expected, actual)).Should().BeFalse();
    }

    private static readonly ResponseComparer Properties = new(PropertiesParityTests.Options);

    [Theory]
    [InlineData("""{"cob":{"display":26.3}}""", """{"cob":{"display":26.2}}""")]
    [InlineData("""{"cob":{"displayLine":"COB: 26.3g"}}""", """{"cob":{"displayLine":"COB: 26.2g"}}""")]
    [InlineData("""{"cob":{"cob":26.253333}}""", """{"cob":{"cob":26.233333}}""")]
    [InlineData("""{"cob":{"treatmentCOB":{"cob":26.253333}}}""", """{"cob":{"treatmentCOB":{"cob":26.243333}}}""")]
    public async Task COB_within_the_reload_lag_matches_in_the_properties_comparison(string expected, string actual)
    {
        (await MatchesAsync(Properties, expected, actual)).Should().BeTrue();
        (await MatchesAsync(Properties, actual, expected)).Should().BeTrue();
    }

    [Theory]
    [InlineData("""{"cob":{"display":26.3}}""", """{"cob":{"display":26.1}}""")]
    [InlineData("""{"cob":{"displayLine":"COB: 26.3g"}}""", """{"cob":{"displayLine":"COB: 26.1g"}}""")]
    [InlineData("""{"cob":{"cob":26.253333}}""", """{"cob":{"cob":26.223333}}""")]
    [InlineData("""{"cob":{"treatmentCOB":{"cob":26.253333}}}""", """{"cob":{"treatmentCOB":{"cob":26.2}}}""")]
    [InlineData("""{"cob":{"rawCarbImpact":0.5}}""", """{"cob":{"rawCarbImpact":0.51}}""")]
    [InlineData("""{"cob":{"treatmentCOB":{"isDecaying":1}}}""", """{"cob":{"treatmentCOB":{"isDecaying":0}}}""")]
    public async Task COB_beyond_the_reload_lag_does_not_match_in_the_properties_comparison(string expected, string actual)
    {
        (await MatchesAsync(Properties, expected, actual)).Should().BeFalse();
    }

    [Theory]
    [InlineData("""{"state":{"cob":3.89}}""", """{"state":{"cob":3.88}}""")]
    [InlineData("""{"iob":{"basaliob":3.89}}""", """{"iob":{"basaliob":3.88}}""")]
    [InlineData("""{"state":{"battery":75}}""", """{"state":{"battery":74.99}}""")]
    public async Task A_difference_of_one_hundredth_off_the_IOB_paths_does_not_match(string expected, string actual)
    {
        (await MatchesAsync(Tolerant, expected, actual)).Should().BeFalse();
    }

    [Theory]
    [InlineData("""{"iob":{"display":"0"}}""", """{"iob":{"display":"0.00"}}""")]
    [InlineData("""{"iob":{"displayLine":"IOB: 0U"}}""", """{"iob":{"displayLine":"IOB: 0.01U"}}""")]
    [InlineData("""{"state":{"iob":null}}""", """{"state":{"iob":0.01}}""")]
    [InlineData("""{"state":{"iob":3.89}}""", """{"state":{"iob":"3.88"}}""")]
    [InlineData("""{"state":{"iob":1e-2}}""", """{"state":{"iob":0.01}}""")]
    public async Task A_change_of_form_on_an_IOB_path_does_not_match(string expected, string actual)
    {
        (await MatchesAsync(Tolerant, expected, actual)).Should().BeFalse();
        (await MatchesAsync(Tolerant, actual, expected)).Should().BeFalse();
    }

    [Fact]
    public async Task A_difference_in_the_text_around_the_number_does_not_match()
    {
        (await MatchesAsync(
            Tolerant,
            """{"iob":{"displayLine":"IOB: 3.89U"}}""",
            """{"iob":{"displayLine":"COB: 3.89g"}}""")).Should().BeFalse();
    }

    [Theory]
    [InlineData("""{"cage":{"minFractions":4}}""", """{"cage":{"minFractions":5}}""")]
    [InlineData("""{"iage":{"minFractions":0}}""", """{"iage":{"minFractions":1}}""")]
    [InlineData("""{"bage":{"minFractions":59}}""", """{"bage":{"minFractions":58}}""")]
    [InlineData("""{"sage":{"Sensor Start":{"minFractions":2}}}""", """{"sage":{"Sensor Start":{"minFractions":3}}}""")]
    [InlineData("""{"sage":{"Sensor Change":{"minFractions":2}}}""", """{"sage":{"Sensor Change":{"minFractions":3}}}""")]
    public async Task A_difference_of_one_minute_on_a_device_age_path_matches(string expected, string actual)
    {
        (await MatchesAsync(Tolerant, expected, actual)).Should().BeTrue();
        (await MatchesAsync(Tolerant, actual, expected)).Should().BeTrue();
    }

    [Theory]
    [InlineData("""{"cage":{"minFractions":4}}""", """{"cage":{"minFractions":6}}""")]
    [InlineData("""{"iage":{"minFractions":0}}""", """{"iage":{"minFractions":2}}""")]
    [InlineData("""{"bage":{"minFractions":59}}""", """{"bage":{"minFractions":57}}""")]
    [InlineData("""{"sage":{"Sensor Start":{"minFractions":2}}}""", """{"sage":{"Sensor Start":{"minFractions":4}}}""")]
    public async Task A_difference_above_one_minute_on_a_device_age_path_does_not_match(string expected, string actual)
    {
        (await MatchesAsync(Tolerant, expected, actual)).Should().BeFalse();
    }

    [Theory]
    [InlineData("""{"cage":{"age":47}}""", """{"cage":{"age":48}}""")]
    [InlineData("""{"cage":{"hours":23}}""", """{"cage":{"hours":0}}""")]
    [InlineData("""{"sage":{"Sensor Start":{"age":120}}}""", """{"sage":{"Sensor Start":{"age":121}}}""")]
    [InlineData("""{"sage":{"Sensor Start":{"hours":0}}}""", """{"sage":{"Sensor Start":{"hours":1}}}""")]
    [InlineData("""{"iage":{"days":1}}""", """{"iage":{"days":2}}""")]
    [InlineData("""{"bage":{"display":"5d0h"}}""", """{"bage":{"display":"5d1h"}}""")]
    [InlineData("""{"cage":{"details":{"minFractions":4}}}""", """{"cage":{"details":{"minFractions":5}}}""")]
    public async Task A_difference_of_one_off_the_minute_paths_does_not_match(string expected, string actual)
    {
        (await MatchesAsync(Tolerant, expected, actual)).Should().BeFalse();
    }

    [Fact]
    public async Task A_difference_of_one_minute_in_a_multi_property_body_matches()
    {
        (await MatchesAsync(
            Tolerant,
            """{"iob":{"display":"3.89"},"cage":{"minFractions":4},"sage":{"Sensor Start":{"minFractions":2}}}""",
            """{"iob":{"display":"3.88"},"cage":{"minFractions":5},"sage":{"Sensor Start":{"minFractions":3}}}""")).Should().BeTrue();
    }

    [Fact]
    public async Task Options_without_the_tolerance_compare_every_path_exactly()
    {
        var exact = new ResponseComparer(ComparisonOptions.Default);

        (await MatchesAsync(exact, """{"state":{"iob":3.89}}""", """{"state":{"iob":3.88}}""")).Should().BeFalse();
        (await MatchesAsync(exact, """{"cage":{"minFractions":4}}""", """{"cage":{"minFractions":5}}""")).Should().BeFalse();
    }

    [Fact]
    public async Task The_tolerance_survives_the_options_copies_the_tests_chain()
    {
        var chained = new ResponseComparer(
            ComparisonOptions.Default.WithReloadLagTolerance().WithIgnoredFields("serverTime").WithPresenceOnlyFields("activity"));

        (await MatchesAsync(chained, """{"state":{"iob":3.89}}""", """{"state":{"iob":3.88}}""")).Should().BeTrue();
    }
}
