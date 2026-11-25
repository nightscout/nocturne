using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Nocturne.API.Reports;
using Xunit;

namespace Nocturne.API.Tests.Reports;

/// <summary>A panel's <c>group</c>: its shape checked, kept on the normalised spec, and read by nothing.</summary>
public class ReportPanelGroupTests
{
    private const string Chart = """{ "kind": "chart", "tracks": [ { "layers": [ { "dataset": { "name": "glucose_series" }, "mark": "line" } ] } ] }""";
    private const string Tiles = """{ "kind": "stat_tiles", "dataset": { "name": "glucose_statistics" } }""";

    private static string Spec(params string[] panels) =>
        $$"""{ "version": 1, "range": { "hours": 24 }, "panels": [ {{string.Join(", ", panels)}} ] }""";

    private static string Grouped(string panel, string group) => panel.Insert(1, $" \"group\": {group},");

    private static ReportSpecValidation Validate(string json) =>
        RecordDatasetTests.Validator.Validate(JsonDocument.Parse(json).RootElement);

    private static string Normalised(ReportSpecValidation validation) =>
        JsonSerializer.Serialize(validation.Spec, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Fact]
    public void A_group_is_kept_on_chart_and_dataset_panels_and_normalises_twice_the_same()
    {
        var validation = Validate(Spec(Grouped(Chart, "\"lows-by-meal\""), Grouped(Tiles, "\"lows-by-meal\""), Chart));

        validation.Errors.Should().BeEmpty();
        validation.Spec!.Panels.Select(p => p.Group).Should().Equal("lows-by-meal", "lows-by-meal", null);
        var once = Normalised(validation);
        Normalised(Validate(once)).Should().Be(once);
        JsonNode.Parse(once)!["panels"]![2]!.AsObject().ContainsKey("group").Should().BeFalse("a panel without a group normalises as before");
    }

    [Fact]
    public void A_group_changes_no_read()
    {
        var with = Validate(Spec(Grouped(Chart, "\"set\""), Grouped(Tiles, "\"set\"")));
        var without = Validate(Spec(Chart, Tiles));

        with.Reads.Should().Be(without.Reads);
        JsonNode.Parse(Normalised(with))!["panels"]!.AsArray().Select(p => { p!.AsObject().Remove("group"); return p.ToJsonString(); })
            .Should().Equal(JsonNode.Parse(Normalised(without))!["panels"]!.AsArray().Select(p => p!.ToJsonString()));
    }

    [Fact]
    public void The_same_group_again_after_another_panel_is_allowed()
    {
        Validate(Spec(Grouped(Chart, "\"a\""), Chart, Grouped(Chart, "\"a\""))).Errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("\"has space\"")]
    [InlineData("\"dot.ted\"")]
    [InlineData("\"a-group-whose-name-runs-past-forty-letters\"")]
    [InlineData("3")]
    [InlineData("[\"a\"]")]
    public void A_group_of_another_shape_is_refused(string group)
    {
        Validate(Spec(Grouped(Chart, group))).Errors.Should().ContainSingle().Which.Path.Should().Be("panels[0].group");
    }

    [Fact]
    public void An_empty_group_is_no_group_as_an_empty_id_is_no_id()
    {
        Validate(Spec(Grouped(Chart, "\"\""))).Spec!.Panels.Single().Group.Should().BeNull();
    }

    [Fact]
    public void Forty_letters_is_the_longest_group()
    {
        Validate(Spec(Grouped(Chart, $"\"{new string('g', ReportSpec.MaxGroupLength)}\""))).Errors.Should().BeEmpty();
    }
}
