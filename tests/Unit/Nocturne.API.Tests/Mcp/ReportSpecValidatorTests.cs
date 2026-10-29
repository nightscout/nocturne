using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Mcp.Reports;
using Nocturne.API.Mcp.Reports.Datasets;
using Xunit;

namespace Nocturne.API.Tests.Mcp;

public class ReportSpecValidatorTests
{
    internal static readonly ReportDatasetRegistry Registry = new([new GlucoseSeriesDataset(), new GlucoseStatisticsDataset()]);

    private static readonly ReportSpecValidator Validator = new(Registry);

    private static ReportSpecValidation Validate(string json) => Validator.Validate(JsonDocument.Parse(json).RootElement);

    private static ReportSpecError SingleError(string json)
    {
        var validation = Validate(json);
        validation.IsValid.Should().BeFalse();
        return validation.Errors.Should().ContainSingle().Subject;
    }

    [Fact]
    public void Normalises_a_valid_spec_ids_ranges_bindings_and_version()
    {
        var validation = Validate("""
            {
              "version": 1,
              "title": "Last day",
              "range": { "hours": 24 },
              "panels": [
                { "dataset": { "name": "glucose_series" }, "primitive": "time_series", "bindings": { "y": "glucose" } },
                { "id": "p1", "dataset": { "name": "glucose_statistics" }, "primitive": "stat_tiles",
                  "range": { "from": "2026-10-01T00:00:00Z", "to": "2026-10-15T00:00:00Z" } }
              ]
            }
            """);

        validation.Errors.Should().BeEmpty();
        var spec = validation.Spec!;
        spec.Version.Should().Be(1);
        spec.Panels.Select(p => p.Id).Should().Equal("p2", "p1");

        var series = spec.Panels[0];
        series.Dataset.Should().BeOfType<GlucoseSeriesParameters>().Which.IntervalMinutes.Should().Be(5);
        series.Range.Should().Be(new ReportRange { Hours = 24 });
        series.Bindings.Should().BeEquivalentTo(new ReportBindings { X = "time", Y = ["glucose"] });

        var stats = spec.Panels[1];
        stats.Range!.From.Should().Be(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        stats.Bindings!.Columns.Should().Equal(new GlucoseStatisticsDataset().Fields.Select(f => f.Name));
    }

    [Fact]
    public void A_panel_without_a_range_gets_its_datasets_default()
    {
        var spec = Validate("""
            { "version": 1, "panels": [ { "dataset": { "name": "glucose_statistics" }, "primitive": "table" } ] }
            """).Spec!;

        spec.Panels[0].Range.Should().Be(new ReportRange { Hours = 14 * 24 });
    }

    [Fact]
    public void Missing_version()
    {
        var error = SingleError("""{ "panels": [ { "dataset": { "name": "glucose_series" }, "primitive": "table" } ] }""");

        error.Path.Should().Be("version");
        error.Message.Should().Contain("\"version\": 1");
    }

    [Fact]
    public void Unsupported_version()
    {
        SingleError("""{ "version": 2, "panels": [ { "dataset": { "name": "glucose_series" }, "primitive": "table" } ] }""")
            .Path.Should().Be("version");
    }

    [Fact]
    public void Unknown_dataset_names_the_panel_and_the_choices()
    {
        var error = SingleError("""
            { "version": 1, "panels": [
              { "dataset": { "name": "glucose_series" }, "primitive": "table" },
              { "dataset": { "name": "insulin" }, "primitive": "table" } ] }
            """);

        error.Path.Should().Be("panels[1].dataset.name");
        error.Message.Should().Contain("'insulin' is not a dataset").And.Contain("glucose_series, glucose_statistics");
    }

    [Fact]
    public void Unknown_field_in_bindings_names_the_panel_the_binding_and_the_fields()
    {
        var error = SingleError("""
            { "version": 1, "panels": [
              { "dataset": { "name": "glucose_series" }, "primitive": "time_series", "bindings": { "y": ["glucose", "ketones"] } } ] }
            """);

        error.Path.Should().Be("panels[0].bindings.y");
        error.Message.Should().Contain("'ketones' is not a field of glucose_series").And.Contain("iob");
    }

    [Fact]
    public void Primitive_not_allowed_for_the_dataset()
    {
        var error = SingleError("""
            { "version": 1, "panels": [ { "dataset": { "name": "glucose_statistics" }, "primitive": "time_series" } ] }
            """);

        error.Path.Should().Be("panels[0].primitive");
        error.Message.Should().Contain("glucose_statistics cannot be drawn as time_series").And.Contain("stat_tiles, table");
    }

    [Fact]
    public void Unknown_primitive()
    {
        var error = SingleError("""
            { "version": 1, "panels": [ { "dataset": { "name": "glucose_series" }, "primitive": "range_bands" } ] }
            """);

        error.Path.Should().Be("panels[0].primitive");
        error.Message.Should().Contain("'range_bands' is not a primitive");
    }

    [Fact]
    public void More_than_six_panels()
    {
        var panel = """{ "dataset": { "name": "glucose_series" }, "primitive": "table" }""";
        var error = SingleError($$"""{ "version": 1, "panels": [ {{string.Join(",", Enumerable.Repeat(panel, 7))}} ] }""");

        error.Path.Should().Be("panels");
        error.Message.Should().Contain("1 to 6 panels; this one has 7");
    }

    [Fact]
    public void Range_over_the_datasets_maximum_names_the_panel_and_the_limit()
    {
        var error = SingleError("""
            { "version": 1, "range": { "hours": 2400 }, "panels": [
              { "dataset": { "name": "glucose_series" }, "primitive": "table" } ] }
            """);

        error.Path.Should().Be("panels[0].range");
        error.Message.Should().Contain("at most 90 days").And.Contain("100 days");
    }

    [Fact]
    public void The_glucose_chart_has_a_shorter_maximum_than_the_series()
    {
        var error = SingleError("""
            { "version": 1, "panels": [
              { "dataset": { "name": "glucose_series" }, "primitive": "glucose_chart", "range": { "hours": 168 } } ] }
            """);

        error.Path.Should().Be("panels[0].range");
        error.Message.Should().Contain("glucose_chart covers at most 3 days");
    }

    [Fact]
    public void Lines_of_different_units_belong_in_different_panels()
    {
        var error = SingleError("""
            { "version": 1, "panels": [
              { "dataset": { "name": "glucose_series" }, "primitive": "time_series", "bindings": { "y": ["glucose", "iob"] } } ] }
            """);

        error.Path.Should().Be("panels[0].bindings.y");
        error.Message.Should().Contain("different units");
    }

    [Fact]
    public void Dataset_parameters_are_checked_by_the_dataset()
    {
        var error = SingleError("""
            { "version": 1, "panels": [
              { "dataset": { "name": "glucose_series", "intervalMinutes": 90 }, "primitive": "table" } ] }
            """);

        error.Path.Should().Be("panels[0].dataset.intervalMinutes");
    }

    [Fact]
    public void An_unknown_parameter_is_refused()
    {
        SingleError("""
            { "version": 1, "panels": [
              { "dataset": { "name": "glucose_statistics", "intervalMinutes": 5 }, "primitive": "table" } ] }
            """).Path.Should().Be("panels[0].dataset");
    }

    [Fact]
    public void Lists_every_error_not_only_the_first()
    {
        var validation = Validate("""
            { "panels": [
              { "dataset": { "name": "nope" }, "primitive": "table" },
              { "dataset": { "name": "glucose_series" }, "primitive": "time_series", "bindings": { "y": "x", "columns": ["time"] } },
              { "id": "a b", "dataset": { "name": "glucose_series" }, "primitive": "table", "colour": "red" } ] }
            """);

        validation.Errors.Select(e => e.Path).Should().BeEquivalentTo(
            "version",
            "panels[0].dataset.name",
            "panels[1].bindings.columns",
            "panels[1].bindings.y",
            "panels[2].colour",
            "panels[2].id");
    }

    [Fact]
    public void Duplicate_ids_are_refused()
    {
        SingleError("""
            { "version": 1, "panels": [
              { "id": "a", "dataset": { "name": "glucose_series" }, "primitive": "table" },
              { "id": "a", "dataset": { "name": "glucose_series" }, "primitive": "table" } ] }
            """).Path.Should().Be("panels[1].id");
    }

    [Theory]
    [InlineData("""{ "hours": 0 }""", "range.hours")]
    [InlineData("""{ "hours": 6, "from": "2026-10-01T00:00:00Z" }""", "range")]
    [InlineData("""{ "from": "2026-10-02T00:00:00Z", "to": "2026-10-01T00:00:00Z" }""", "range.to")]
    [InlineData("""{ "from": "yesterday", "to": "2026-10-01T00:00:00Z" }""", "range.from")]
    public void Malformed_ranges(string range, string path)
    {
        SingleError($$"""{ "version": 1, "range": {{range}}, "panels": [ { "dataset": { "name": "glucose_series" }, "primitive": "table" } ] }""")
            .Path.Should().Be(path);
    }

    [Fact]
    public void Not_an_object()
    {
        SingleError("[]").Path.Should().Be("spec");
        Validator.Validate(default).Errors.Should().ContainSingle().Which.Path.Should().Be("spec");
    }
}
