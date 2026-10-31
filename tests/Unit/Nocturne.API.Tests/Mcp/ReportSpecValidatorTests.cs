using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Mcp.Reports;
using Nocturne.API.Mcp.Reports.Datasets;
using Xunit;

namespace Nocturne.API.Tests.Mcp;

public class ReportSpecValidatorTests
{
    internal static readonly ReportDatasetRegistry Registry = new(
    [
        new GlucoseSeriesDataset(), new GlucoseStatisticsDataset(), new EventResponseDataset(), new SettingsEvidenceDataset(),
        new DailyProfileDataset(), new GlucoseDistributionByHourDataset(), new TreatmentsDataset(), new BasalDeliveryDataset(),
        new DeviceStatusDataset(), new GlucoseDistributionDataset(), new DailySummaryDataset(),
    ]);

    private static readonly ReportSpecValidator Validator = new(Registry, TimeProvider.System);

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
                { "dataset": { "name": "glucose_series" }, "primitive": "time_series", "bindings": { "y": ["glucose"] } },
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
            { "version": 1, "panels": [ { "dataset": { "name": "glucose_series" }, "primitive": "histogram" } ] }
            """);

        error.Path.Should().Be("panels[0].primitive");
        error.Message.Should().Contain("'histogram' is not a primitive");
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
            { "version": 1, "range": { "from": "2026-06-01T00:00:00Z", "to": "2026-09-09T00:00:00Z" }, "panels": [
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
            """).Path.Should().Be("panels[0].dataset.intervalMinutes");
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
    public void A_panel_range_starting_after_now_names_its_from()
    {
        var now = DateTimeOffset.UtcNow;
        string At(double hours) => now.AddHours(hours).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);

        var error = SingleError($$"""
            { "version": 1, "panels": [ { "dataset": { "name": "glucose_series" }, "primitive": "table",
              "range": { "from": "{{At(2)}}", "to": "{{At(10)}}" } } ] }
            """);

        error.Path.Should().Be("panels[0].range.from");
        error.Message.Should().Contain("'from'");
    }

    [Fact]
    public void Not_an_object()
    {
        SingleError("[]").Path.Should().Be("spec");
        Validator.Validate(default).Errors.Should().ContainSingle().Which.Path.Should().Be("spec");
    }

    [Theory]
    [InlineData("65", "panels[0].dataset.intervalMinutes")]
    [InlineData("0", "panels[0].dataset.intervalMinutes")]
    [InlineData("\"5\"", "panels[0].dataset.intervalMinutes")]
    [InlineData("5.5", "panels[0].dataset.intervalMinutes")]
    public void Interval_minutes_outside_1_to_60_or_not_a_whole_number_is_refused(string value, string path)
    {
        var error = SingleError($$"""
            { "version": 1, "panels": [ { "dataset": { "name": "glucose_series", "intervalMinutes": {{value}} }, "primitive": "table" } ] }
            """);

        error.Path.Should().Be(path);
    }

    [Fact]
    public void A_single_string_for_a_list_binding_is_refused()
    {
        var error = SingleError("""
            { "version": 1, "panels": [
              { "dataset": { "name": "glucose_series" }, "primitive": "time_series", "bindings": { "y": "glucose" } } ] }
            """);

        error.Path.Should().Be("panels[0].bindings.y");
        error.Message.Should().Contain("[\"glucose\"]");
    }

    [Fact]
    public void The_unit_mismatch_names_the_display_unit()
    {
        var spec = JsonDocument.Parse("""
            { "version": 1, "panels": [
              { "dataset": { "name": "glucose_series" }, "primitive": "time_series", "bindings": { "y": ["glucose", "iob"] } } ] }
            """).RootElement;

        Validator.Validate(spec, useMmol: true).Errors.Single().Message.Should().Contain("'glucose' (mmol/L)").And.Contain("'iob' (U)");
        Validator.Validate(spec, useMmol: false).Errors.Single().Message.Should().Contain("'glucose' (mg/dL)");
    }

    private const string Table = """{ "dataset": { "name": "glucose_series" }, "primitive": "table" }""";

    /// <summary>
    /// Malformed specs, each with the path its error must name. The first nine threw before the
    /// validator read the JSON kind first.
    /// </summary>
    public static TheoryData<string, string> Malformed => new()
    {
        { $$"""{ "version": "1", "panels": [{{Table}}] }""", "version" },
        { $$"""{ "version": 1, "range": { "hours": "6" }, "panels": [{{Table}}] }""", "range.hours" },
        { $$"""{ "version": 1, "range": { "hours": 1000000000 }, "panels": [{{Table}}] }""", "range.hours" },
        { $$"""{ "version": 1, "range": { "hours": 2147483647 }, "panels": [{{Table}}] }""", "range.hours" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "glucose_series" }, "primitive": "time_series", "bindings": { "x": [], "y": ["glucose"] } } ] }""", "panels[0].bindings.x" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "glucose_series" }, "primitive": "time_series", "bindings": { "x": ["time", "time"], "y": ["glucose"] } } ] }""", "panels[0].bindings.x" },
        { $$"""{ "version": 1, "range": { "from": "0001-01-01T00:00:00Z", "to": "2026-10-01T00:00:00Z" }, "panels": [{{Table}}] }""", "range.from" },
        { $$"""{ "version": 1, "range": { "from": "2026-10-01T00:00:00Z", "to": "9999-12-30T00:00:00Z" }, "panels": [{{Table}}] }""", "range.to" },
        { $$"""{ "version": 1, "range": { "from": "1999-12-31T00:00:00Z", "to": "2000-01-02T00:00:00Z" }, "panels": [{{Table}}] }""", "range.from" },
        { $$"""{ "version": 1.5, "panels": [{{Table}}] }""", "version" },
        { $$"""{ "version": true, "panels": [{{Table}}] }""", "version" },
        { $$"""{ "version": 99999999999999999999, "panels": [{{Table}}] }""", "version" },
        { """{ "version": 1, "panels": {} }""", "panels" },
        { """{ "version": 1, "panels": "all" }""", "panels" },
        { """{ "version": 1, "panels": [ 1 ] }""", "panels[0]" },
        { """{ "version": 1, "panels": [ null ] }""", "panels[0]" },
        { """{ "version": 1, "panels": [ { "dataset": "glucose_series", "primitive": "table" } ] }""", "panels[0].dataset" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": 7 }, "primitive": "table" } ] }""", "panels[0].dataset.name" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "glucose_series" }, "primitive": 3 } ] }""", "panels[0].primitive" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "glucose_series", "intervalMinutes": {} }, "primitive": "table" } ] }""", "panels[0].dataset.intervalMinutes" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "glucose_series", "intervalMinutes": 5, "intervalMinutes": 90 }, "primitive": "table" } ] }""", "panels[0].dataset.intervalMinutes" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "glucose_series", "foo": 1 }, "primitive": "table" } ] }""", "panels[0].dataset.foo" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "glucose_series" }, "primitive": "table", "bindings": [] } ] }""", "panels[0].bindings" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "glucose_series" }, "primitive": "table", "bindings": { "columns": [1, 2] } } ] }""", "panels[0].bindings.columns" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "glucose_series" }, "primitive": "time_series", "bindings": { "x": 5, "y": ["glucose"] } } ] }""", "panels[0].bindings.x" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "glucose_series" }, "primitive": "time_series", "bindings": { "y": ["glucose"], "band": "targetLow" } } ] }""", "panels[0].bindings.band" },
        { $$"""{ "version": 1, "range": { "from": 5, "to": 6 }, "panels": [{{Table}}] }""", "range.from" },
        { $$"""{ "version": 1, "range": { "hours": -3 }, "panels": [{{Table}}] }""", "range.hours" },
        { $$"""{ "version": 1, "range": [], "panels": [{{Table}}] }""", "range" },
        { $$"""{ "version": 1, "title": 42, "panels": [{{Table}}] }""", "title" },
        { $$"""{ "version": 1, "title": "{{new string('t', 201)}}", "panels": [{{Table}}] }""", "title" },
        { """{ "version": 1, "panels": [ { "id": 7, "dataset": { "name": "glucose_series" }, "primitive": "table" } ] }""", "panels[0].id" },
        { """{ "version": 1, "panels": [ { "id": "this-id-is-far-too-long-for-a-panel-x", "dataset": { "name": "glucose_series" }, "primitive": "table" } ] }""", "panels[0].id" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "glucose_series" }, "primitive": "table", "range": { "hours": 6, "to": "x" } } ] }""", "panels[0].range" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "event_response", "cohorts": [ { "anchor": "Lunch" } ] }, "primitive": "range_bands" } ] }""", "panels[0].dataset.cohorts[0].anchor" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "event_response", "cohorts": [ { "anchor": "Meal", "dayOfWeek": ["Funday"] } ] }, "primitive": "range_bands" } ] }""", "panels[0].dataset.cohorts[0].dayOfWeek[0]" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "event_response", "cohorts": [ { "anchor": "Meal", "foods": ["pasta"] } ] }, "primitive": "range_bands" } ] }""", "panels[0].dataset.cohorts[0].foods[0]" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "event_response", "cohorts": [ { "anchor": "Meal", "colour": "red" } ] }, "primitive": "range_bands" } ] }""", "panels[0].dataset.cohorts[0].colour" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "event_response", "cohorts": "meals" }, "primitive": "range_bands" } ] }""", "panels[0].dataset.cohorts" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "event_response", "cohorts": [] }, "primitive": "range_bands" } ] }""", "panels[0].dataset.cohorts" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "event_response", "cohorts": [ { "anchor": "Meal" }, { "anchor": "Meal" }, { "anchor": "Meal" }, { "anchor": "Meal" } ] }, "primitive": "range_bands" } ] }""", "panels[0].dataset.cohorts" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "event_response", "cohorts": [ { "anchor": "CorrectionBolus", "foodCategory": "Pasta" } ] }, "primitive": "range_bands" } ] }""", "panels[0].dataset.cohorts[0].foods" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "event_response", "cohorts": [ { "anchor": "Meal", "timeOfDay": { "fromMinute": 1500, "toMinute": 60 } } ] }, "primitive": "range_bands" } ] }""", "panels[0].dataset.cohorts[0].timeOfDay" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "event_response", "minutesAfter": 301 }, "primitive": "range_bands" } ] }""", "panels[0].dataset.minutesAfter" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "event_response", "comparisonRange": { "from": "2026-10-10", "to": "2026-10-01" } }, "primitive": "range_bands" } ] }""", "panels[0].dataset.comparisonRange.to" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "event_response" }, "primitive": "range_bands", "bindings": { "y": ["glucoseP50", "glucoseP90"], "band": ["glucoseP10", "glucoseP90"] } } ] }""", "panels[0].bindings.y" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "event_response" }, "primitive": "range_bands", "bindings": { "y": ["glucoseP50"], "band": ["glucoseP10", "glucoseP25", "glucoseP75"] } } ] }""", "panels[0].bindings.band" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "event_response" }, "primitive": "range_bands", "bindings": { "y": ["glucoseP50"], "band": ["glucoseP10", "iobP50"] } } ] }""", "panels[0].bindings.band" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "event_response" }, "primitive": "range_bands", "bindings": { "y": ["glucoseP50"], "band": ["glucoseP10", "glucoseP90"], "series": "minute" } } ] }""", "panels[0].bindings.series" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "event_response" }, "primitive": "table", "bindings": { "columns": ["glucoseP50"] } } ] }""", "panels[0].bindings.columns" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "settings_evidence", "setting": "carbs" }, "primitive": "table" } ] }""", "panels[0].dataset.setting" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "glucose_series" }, "primitive": "range_bands" } ] }""", "panels[0].primitive" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "glucose_series" }, "primitive": "table", "bindings": { "series": "direction" } } ] }""", "panels[0].bindings.series" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "glucose_distribution" }, "primitive": "bar", "bindings": { "x": "band", "y": ["percent"], "stacked": "yes" } } ] }""", "panels[0].bindings.stacked" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "glucose_series" }, "primitive": "time_series", "bindings": { "y": ["glucose"], "stacked": true } } ] }""", "panels[0].bindings.stacked" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "glucose_distribution" }, "primitive": "bar", "bindings": { "x": "band", "y": ["percent"], "orientation": "sideways" } } ] }""", "panels[0].bindings.orientation" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "glucose_distribution" }, "primitive": "bar", "bindings": { "x": "band", "y": ["percent"], "orientation": 1 } } ] }""", "panels[0].bindings.orientation" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "glucose_series" }, "primitive": "time_series", "bindings": { "y": ["glucose"], "orientation": "horizontal" } } ] }""", "panels[0].bindings.orientation" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "glucose_distribution" }, "primitive": "bar", "bindings": { "x": "period", "y": ["veryLow", "minutes"] } } ] }""", "panels[0].bindings.y" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "event_response" }, "primitive": "glucose_chart", "bindings": { "y": ["glucoseP50"], "band": ["glucoseP25"] } } ] }""", "panels[0].bindings.band" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "glucose_distribution" }, "primitive": "bar", "bindings": { "x": "percent", "y": ["percent"] } } ] }""", "panels[0].bindings.x" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "glucose_distribution" }, "primitive": "bar", "bindings": { "x": "band", "y": ["percent", "minutes"] } } ] }""", "panels[0].bindings.y" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "glucose_distribution" }, "primitive": "bar", "bindings": { "x": "band", "y": ["range"] } } ] }""", "panels[0].bindings.y" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "glucose_distribution" }, "primitive": "bar", "bindings": { "x": "band", "y": [] } } ] }""", "panels[0].bindings.y" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "glucose_distribution" }, "primitive": "bar", "bindings": { "x": "band", "y": ["percent"], "band": ["percent", "percent"] } } ] }""", "panels[0].bindings.band" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "glucose_distribution" }, "primitive": "bar", "bindings": { "x": "band", "y": ["percent"], "series": "band" } } ] }""", "panels[0].bindings.series" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "glucose_distribution", "bins": 10 }, "primitive": "bar" } ] }""", "panels[0].dataset.bins" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "glucose_distribution" }, "primitive": "time_series" } ] }""", "panels[0].primitive" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "daily_summary" }, "primitive": "stat_tiles" } ] }""", "panels[0].primitive" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "daily_summary" }, "primitive": "bar", "bindings": { "x": "day", "y": ["timeInRange"] } } ] }""", "panels[0].bindings.x" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "daily_summary" }, "primitive": "bar", "bindings": { "x": "date", "y": ["timeInRange", "totalInsulin"] } } ] }""", "panels[0].bindings.y" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "treatments", "types": ["insulin"] }, "primitive": "table" } ] }""", "panels[0].dataset.types[0]" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "treatments", "types": "bolus" }, "primitive": "table" } ] }""", "panels[0].dataset.types" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "treatments", "types": [] }, "primitive": "table" } ] }""", "panels[0].dataset.types" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "treatments", "minCarbs": -1 }, "primitive": "table" } ] }""", "panels[0].dataset.minCarbs" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "treatments", "minCarbs": "10" }, "primitive": "table" } ] }""", "panels[0].dataset.minCarbs" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "treatments", "minInsulin": 500 }, "primitive": "table" } ] }""", "panels[0].dataset.minInsulin" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "treatments" }, "primitive": "table", "range": { "hours": 400 } } ] }""", "panels[0].range" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "treatments" }, "primitive": "time_series", "bindings": { "y": ["insulin", "carbs"] } } ] }""", "panels[0].bindings.y" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "treatments" }, "primitive": "bar" } ] }""", "panels[0].primitive" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "glucose_series", "markers": ["bolus"] }, "primitive": "table" } ] }""", "panels[0].dataset.markers" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "glucose_series", "markers": ["bolus"] }, "primitive": "time_series", "bindings": { "y": ["glucose"] }, "range": { "hours": 96 } } ] }""", "panels[0].dataset.markers" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "glucose_series", "markers": ["insulin"] }, "primitive": "time_series", "bindings": { "y": ["glucose"] } } ] }""", "panels[0].dataset.markers[0]" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "glucose_series", "markers": ["carbs", "carbs"] }, "primitive": "time_series", "bindings": { "y": ["glucose"] } } ] }""", "panels[0].dataset.markers" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "glucose_series", "markers": "bolus" }, "primitive": "time_series", "bindings": { "y": ["glucose"] } } ] }""", "panels[0].dataset.markers" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "basal_delivery" }, "primitive": "table" } ] }""", "panels[0].primitive" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "basal_delivery" }, "primitive": "time_series", "bindings": { "y": ["delivered", "origin"] } } ] }""", "panels[0].bindings.y" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "device_status" }, "primitive": "time_series", "range": { "hours": 960 } } ] }""", "panels[0].range" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "device_status" }, "primitive": "time_series", "bindings": { "y": ["reservoir", "pumpBattery"] } } ] }""", "panels[0].bindings.y" },
        { """{ "version": 1, "panels": [ { "dataset": { "name": "device_status" }, "primitive": "stat_tiles", "bindings": { "columns": ["time"] } } ] }""", "panels[0].bindings.columns" },
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void A_malformed_spec_is_an_error_naming_where_never_an_exception(string json, string path)
    {
        var validation = Validate(json);

        validation.IsValid.Should().BeFalse();
        validation.Errors.Select(e => e.Path).Should().Contain(path, string.Join("\n", validation.Errors));
        foreach (var error in validation.Errors)
        {
            error.Message.Should().NotBeNullOrWhiteSpace();
            error.Message.Should().NotContain("System.").And.NotContain("Nocturne.").And.NotContain("Int32");
        }
    }
}
