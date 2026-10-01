using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

[Trait("Category", "Unit")]
public class ConditionTimeZonesTests
{
    private const string Windows = "AUS Eastern Standard Time";
    private const string Iana = "Australia/Sydney";

    private static string TimeOfDay(string timezone, string key = "timezone") =>
        $$"""{"from":"22:00","to":"06:00","{{key}}":"{{timezone}}"}""";

    private static string? ZoneAt(string json, params string[] path)
    {
        var node = JsonNode.Parse(json);
        foreach (var segment in path)
            node = int.TryParse(segment, out var i) ? node![i] : node![segment];
        return node?.GetValue<string>();
    }

    [Theory]
    [InlineData(Windows)]
    [InlineData("aus eastern standard time")]
    public void A_windows_id_converts_to_iana(string id)
    {
        TimeZoneHelper.ToIanaIdIfWindows(id).Should().Be(Iana);
    }

    [Theory]
    [InlineData(Iana)]
    [InlineData("ETC/GMT-2")]
    [InlineData("Not/AZone")]
    public void Any_other_id_is_left_alone(string id)
    {
        TimeZoneHelper.ToIanaIdIfWindows(id).Should().Be(id);
    }

    [Fact]
    public void A_time_of_day_rule_body_is_rewritten()
    {
        var json = ConditionTimeZones.CanonicaliseRule(AlertConditionType.TimeOfDay, TimeOfDay(Windows));

        ZoneAt(json, "timezone").Should().Be(Iana);
        ZoneAt(json, "from").Should().Be("22:00");
    }

    [Fact]
    public void A_mis_cased_timezone_key_is_rewritten_in_place()
    {
        var json = ConditionTimeZones.CanonicaliseRule(AlertConditionType.TimeOfDay, TimeOfDay(Windows, key: "Timezone"));

        ZoneAt(json, "Timezone").Should().Be(Iana);
    }

    [Fact]
    public void Nested_time_of_day_leaves_are_rewritten()
    {
        var leaf = """{"type":"time_of_day","time_of_day":""" + TimeOfDay(Windows) + "}";
        var body = """{"operator":"and","conditions":[{"type":"threshold","threshold":{"direction":"below","value":70}},"""
            + """{"type":"not","not":{"child":""" + leaf + "}},"
            + """{"type":"sustained","sustained":{"minutes":5,"child":""" + leaf + "}}]}";

        var json = ConditionTimeZones.CanonicaliseRule(AlertConditionType.Composite, body);

        ZoneAt(json, "conditions", "1", "not", "child", "time_of_day", "timezone").Should().Be(Iana);
        ZoneAt(json, "conditions", "2", "sustained", "child", "time_of_day", "timezone").Should().Be(Iana);
    }

    [Fact]
    public void A_body_with_nothing_to_rewrite_is_returned_unchanged()
    {
        const string body = """{ "operator": "and", "conditions": [{"type":"time_of_day","time_of_day":{"from":"22:00","to":"06:00","timezone":"Australia/Sydney"}}] }""";

        ConditionTimeZones.CanonicaliseRule(AlertConditionType.Composite, body).Should().BeSameAs(body);
    }

    [Fact]
    public void Unparseable_json_is_returned_unchanged()
    {
        const string body = "{ time_of_day";

        ConditionTimeZones.CanonicaliseRule(AlertConditionType.Composite, body).Should().BeSameAs(body);
    }

    [Fact]
    public void A_full_node_is_rewritten()
    {
        var json = ConditionTimeZones.CanonicaliseNode($$$"""{"type":"time_of_day","time_of_day":{{{TimeOfDay(Windows)}}}}""");

        ZoneAt(json, "time_of_day", "timezone").Should().Be(Iana);
    }

    [Fact]
    public void Smart_snooze_conditions_are_rewritten()
    {
        var config = $$$"""{"snooze":{"smartSnooze":true,"conditions":[{"type":"time_of_day","time_of_day":{{{TimeOfDay(Windows)}}}}]}}""";

        var json = ConditionTimeZones.CanonicaliseClientConfiguration(config);

        ZoneAt(json, "snooze", "conditions", "0", "time_of_day", "timezone").Should().Be(Iana);
    }

    [Fact]
    public void An_unresolvable_zone_is_reported_at_its_condition_path()
    {
        var body = $$$"""{"operator":"or","conditions":[{"type":"threshold","threshold":{"direction":"below","value":70}},{"type":"time_of_day","time_of_day":{{{TimeOfDay("Not/AZone")}}}}]}""";

        ConditionTimeZones.UnresolvedInRule(AlertConditionType.Composite, body)
            .Should().Equal(new ConditionTimeZones.UnresolvedZone("composite[1].time_of_day"));
    }

    [Theory]
    [InlineData(Windows)]
    [InlineData(Iana)]
    [InlineData("ETC/GMT-2")]
    public void A_resolvable_zone_is_not_reported(string id)
    {
        ConditionTimeZones.UnresolvedInRule(AlertConditionType.TimeOfDay, TimeOfDay(id)).Should().BeEmpty();
    }

    [Fact]
    public void Auto_resolve_and_snooze_zones_are_pathed_under_their_roots()
    {
        var node = $$$"""{"type":"time_of_day","time_of_day":{{{TimeOfDay("Not/AZone")}}}}""";

        ConditionTimeZones.UnresolvedInNode(node, "auto_resolve")
            .Should().Equal(new ConditionTimeZones.UnresolvedZone("auto_resolve"));
        ConditionTimeZones.UnresolvedInConditionList(JsonDocument.Parse($"[{node}]").RootElement, "snooze")
            .Should().Equal(new ConditionTimeZones.UnresolvedZone("snooze[0].time_of_day"));
    }
}
