using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Reports;
using Nocturne.API.Reports.Datasets;
using Nocturne.API.Reports.Derived;
using Xunit;

namespace Nocturne.API.Tests.Reports;

public class ReportSpecValidatorTests
{
    internal static readonly ReportDatasetRegistry Registry = new(
    [
        new GlucoseSeriesDataset(), new GlucoseStatisticsDataset(), new EventResponseDataset(), new SettingsEvidenceDataset(),
        new DailyProfileDataset(), new GlucoseDistributionByHourDataset(), new TreatmentsDataset(), new BasalDeliveryDataset(),
        new DeviceStatusDataset(), new GlucoseDistributionDataset(), new DailySummaryDataset(), new PeriodComparisonDataset(),
        new StateSpansDataset(), new EventOutcomesDataset(), new EventOutcomeSpreadDataset(), new EventOccurrenceDataset(),
    ]);

    private static readonly ReportSpecValidator Validator = new(Registry, TimeProvider.System);

    private static ReportSpecValidation Validate(string json, bool useMmol = false) =>
        Validator.Validate(JsonDocument.Parse(json).RootElement, useMmol);

    private static ReportSpecError SingleError(string json)
    {
        var validation = Validate(json);
        validation.IsValid.Should().BeFalse();
        return validation.Errors.Should().ContainSingle(string.Join("\n", validation.Errors)).Subject;
    }

    /// <summary>A report of one chart panel with <paramref name="tracks"/> (JSON of the tracks' contents, comma-separated).</summary>
    internal static string Chart(string tracks, string panelExtras = "", string reportExtras = "") =>
        $$"""{ "version": 1 {{reportExtras}}, "panels": [ { "kind": "chart" {{panelExtras}}, "tracks": [ {{tracks}} ] } ] }""";

    /// <summary>A report of one chart panel of one track of <paramref name="layers"/>.</summary>
    internal static string Layers(string layers, string panelExtras = "", string reportExtras = "") =>
        Chart($$"""{ "layers": [ {{layers}} ] }""", panelExtras, reportExtras);

    internal static string Layer(string dataset, string mark, string? bindings = null) =>
        $$"""{ "dataset": {{dataset}}, "mark": "{{mark}}"{{(bindings is null ? "" : $", \"bindings\": {bindings}")}} }""";

    private const string Glucose = """{ "name": "glucose_series" }""";

    private static string Source(string field, string dataset = Glucose) => $$"""{ "dataset": {{dataset}}, "field": "{{field}}" }""";

    private const string Table = """{ "kind": "table", "dataset": { "name": "glucose_series" } }""";

    [Fact]
    public void Normalises_a_valid_spec_ids_ranges_marks_bindings_and_version()
    {
        var validation = Validate("""
            {
              "version": 1,
              "title": "Last day",
              "range": { "hours": 24 },
              "panels": [
                { "kind": "chart", "tracks": [ { "layers": [ { "dataset": { "name": "glucose_series" } } ] } ] },
                { "id": "p1", "kind": "stat_tiles", "dataset": { "name": "glucose_statistics" },
                  "range": { "from": "2026-10-01T00:00:00Z", "to": "2026-10-15T00:00:00Z" } }
              ]
            }
            """);

        validation.Errors.Should().BeEmpty();
        var spec = validation.Spec!;
        spec.Version.Should().Be(1);
        spec.Panels.Select(p => p.Id).Should().Equal("p2", "p1");

        var chart = spec.Panels[0];
        chart.Range.Should().Be(new ReportRange { Hours = 24 });
        var track = chart.Tracks!.Single();
        track.Id.Should().Be("t1");
        track.Height.Should().Be(1);
        var layer = track.Layers.Single();
        layer.Id.Should().Be("l1");
        layer.Mark.Should().Be(ReportMark.Line);
        layer.Bindings.Should().BeEquivalentTo(new ReportLayerBindings { X = "time", Y = ["glucose"] });
        layer.XKind.Should().Be(ReportXKind.Time);
        layer.Unit.Should().Be("mg/dL");
        layer.HasAxis.Should().BeTrue();

        var stats = spec.Panels[1];
        stats.Range!.From.Should().Be(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        stats.Bindings!.Columns.Should().Equal(new GlucoseStatisticsDataset().Fields.Select(f => f.Name));
    }

    [Fact]
    public void A_lane_of_spans_gets_a_small_height_and_no_axis()
    {
        var spec = Validate(Layers(Layer("""{ "name": "state_spans" }""", "spans"))).Spec!;

        var track = spec.Panels[0].Tracks!.Single();
        track.Height.Should().Be(ReportTrackSpec.LaneHeight);
        track.Layers[0].HasAxis.Should().BeFalse();
        track.Layers[0].Bindings.Should().BeEquivalentTo(new ReportLayerBindings { Start = "start", End = "end", Category = "kind" });
    }

    [Fact]
    public void A_time_of_day_window_that_starts_and_ends_together_is_refused_at_the_window()
    {
        var error = SingleError("""
            { "version": 1, "panels": [ { "kind": "table", "dataset": { "name": "event_response",
              "cohorts": [ { "anchor": "Meal", "timeOfDay": { "fromMinute": 0, "toMinute": 0 } } ] } } ] }
            """);

        error.Path.Should().Be("panels[0].dataset.cohorts[0].timeOfDay");
        error.Message.Should().Contain("holds no events");
    }

    [Fact]
    public void A_panel_without_a_range_gets_its_datasets_default()
    {
        Validate("""{ "version": 1, "panels": [ { "kind": "table", "dataset": { "name": "glucose_statistics" } } ] }""")
            .Spec!.Panels[0].Range.Should().Be(new ReportRange { Hours = 14 * 24 });
        Validate(Layers(Layer("""{ "name": "daily_profile" }""", "band"))).Spec!.Panels[0].Range.Should().Be(new ReportRange { Hours = 14 * 24 });
    }

    [Fact]
    public void Missing_version()
    {
        var error = SingleError($$"""{ "panels": [ {{Table}} ] }""");

        error.Path.Should().Be("version");
        error.Message.Should().Contain("\"version\": 1");
    }

    [Fact]
    public void A_panel_with_no_kind_names_the_kinds()
    {
        var error = SingleError("""{ "version": 1, "panels": [ { "dataset": { "name": "glucose_series" } } ] }""");

        error.Path.Should().Be("panels[0].kind");
        error.Message.Should().Contain("chart, stat_tiles, table, comparison");
    }

    [Fact]
    public void Unknown_dataset_names_the_layer_and_the_choices()
    {
        var error = SingleError(Layers(Layer(Glucose, "line") + "," + Layer("""{ "name": "insulin" }""", "line")));

        error.Path.Should().Be("panels[0].tracks[0].layers[1].dataset.name");
        error.Message.Should().Contain("'insulin' is not a dataset").And.Contain("glucose_series, glucose_statistics");
    }

    [Fact]
    public void Unknown_field_in_bindings_names_the_layer_the_binding_and_the_fields()
    {
        var error = SingleError(Layers(Layer(Glucose, "line", """{ "y": ["glucose", "ketones"] }""")));

        error.Path.Should().Be("panels[0].tracks[0].layers[0].bindings.y");
        error.Message.Should().Contain("'ketones' is not a field").And.Contain("iob");
    }

    [Fact]
    public void A_dataset_no_chart_draws_is_refused_as_a_layer_with_the_kinds_it_takes()
    {
        var error = SingleError(Layers(Layer("""{ "name": "glucose_statistics" }""", "line")));

        error.Path.Should().Be("panels[0].tracks[0].layers[0].dataset.name");
        error.Message.Should().Contain("cannot be drawn in a chart").And.Contain("figures or a table");
    }

    [Fact]
    public void A_mark_the_dataset_does_not_allow_names_the_ones_it_does()
    {
        var error = SingleError(Layers(Layer("""{ "name": "basal_delivery" }""", "markers")));

        error.Path.Should().Be("panels[0].tracks[0].layers[0].mark");
        error.Message.Should().Contain("cannot be drawn as treatment markers").And.Contain("area, line");
    }

    [Fact]
    public void A_kind_of_panel_the_dataset_does_not_allow_is_refused_at_the_kind()
    {
        var error = SingleError("""{ "version": 1, "panels": [ { "kind": "stat_tiles", "dataset": { "name": "daily_summary" } } ] }""");

        error.Path.Should().Be("panels[0].kind");
        error.Message.Should().Contain("cannot be shown as figures").And.Contain("a chart or a table");
    }

    [Fact]
    public void A_chart_panel_refuses_a_panel_dataset_and_a_table_refuses_tracks()
    {
        SingleError("""{ "version": 1, "panels": [ { "kind": "chart", "dataset": { "name": "glucose_series" }, "tracks": [ { "layers": [ { "dataset": { "name": "glucose_series" } } ] } ] } ] }""")
            .Should().BeEquivalentTo(new { Path = "panels[0].dataset", Message = "A chart panel names its data in its layers. Remove 'dataset'." });
        SingleError("""{ "version": 1, "panels": [ { "kind": "table", "dataset": { "name": "glucose_series" }, "grid": 5 } ] }""")
            .Should().BeEquivalentTo(new { Path = "panels[0].grid", Message = "Only a chart panel takes 'grid'. Remove it." });
    }

    [Fact]
    public void More_than_six_panels()
    {
        var error = SingleError($$"""{ "version": 1, "panels": [ {{string.Join(",", Enumerable.Repeat(Table, 7))}} ] }""");

        error.Path.Should().Be("panels");
        error.Message.Should().Contain("1 to 6 panels; this one has 7");
    }

    [Fact]
    public void More_than_four_tracks_or_eight_layers()
    {
        var track = $$"""{ "layers": [ {{Layer(Glucose, "line")}} ] }""";
        SingleError(Chart(string.Join(",", Enumerable.Repeat(track, 5)))).Should().BeEquivalentTo(new
        {
            Path = "panels[0].tracks",
            Message = "A chart has 1 to 4 tracks; this one has 5.",
        });

        var nine = string.Join(",", Enumerable.Repeat(Layer(Glucose, "line"), 9));
        var error = SingleError(Layers(nine));
        error.Path.Should().Be("panels[0]");
        error.Message.Should().Contain("at most 8 layers; this one has 9");
    }

    [Fact]
    public void A_report_reads_at_most_sixteen_datasets_counted_after_the_cache()
    {
        // Eight layers of one query over one range read once: the cache counts it once.
        Validate(Layers(string.Join(",", Enumerable.Repeat(Layer(Glucose, "line"), 8)))).Errors.Should().BeEmpty();

        string Treatments(string types) => Layer($$"""{ "name": "treatments", "types": [{{types}}] }""", "markers");
        string Spans(string kind) => Layer($$"""{ "name": "state_spans", "kinds": ["{{kind}}"] }""", "spans");
        string Panel(int hours) => $$"""{ "kind": "chart", "range": { "hours": {{hours}} }, "tracks": [ { "layers": [ {{string.Join(",",
            Layer(Glucose, "line"), Treatments("\"bolus\""), Treatments("\"carbs\""), Treatments("\"note\""), Treatments("\"bolus\", \"carbs\""),
            Spans("pump_mode"), Spans("profile"), Layer("""{ "name": "basal_delivery" }""", "area"))}} ] } ] }""";
        var panels = new[] { Panel(6), Panel(12), """{ "kind": "table", "dataset": { "name": "glucose_series" }, "range": { "hours": 48 } }""" };
        var error = SingleError($$"""{ "version": 1, "panels": [ {{string.Join(",", panels)}} ] }""");

        error.Path.Should().Be("panels");
        error.Message.Should().Contain("This report reads 17 datasets; a report reads at most 16.");
    }

    [Fact]
    public void A_derived_layers_sources_count_as_reads_and_share_with_a_visible_layer()
    {
        var spec = Layers(Layer(Glucose, "line") + "," +
            $$"""{ "derived": { "op": "difference", "a": {{Source("glucose")}}, "b": {{Source("glucoseMgdl")}} } }""");

        Validate(spec).Errors.Should().BeEmpty();
    }

    [Fact]
    public void Range_over_the_datasets_maximum_names_the_panel_and_the_limit()
    {
        var error = SingleError("""
            { "version": 1, "range": { "from": "2026-06-01T00:00:00Z", "to": "2026-09-09T00:00:00Z" }, "panels": [
              { "kind": "table", "dataset": { "name": "glucose_series" } } ] }
            """);

        error.Path.Should().Be("panels[0].range");
        error.Message.Should().Contain("at most 90 days").And.Contain("100 days");
    }

    [Fact]
    public void Lines_of_different_units_belong_in_different_layers()
    {
        var error = SingleError(Layers(Layer(Glucose, "line", """{ "y": ["glucose", "iob"] }""")));

        error.Path.Should().Be("panels[0].tracks[0].layers[0].bindings.y");
        error.Message.Should().Contain("different units");
    }

    [Fact]
    public void A_track_takes_two_units_and_refuses_a_third_at_the_layer_naming_them()
    {
        var spec = Layers(string.Join(",",
            Layer(Glucose, "line", """{ "y": ["glucose"] }"""),
            Layer(Glucose, "line", """{ "y": ["iob"] }"""),
            Layer(Glucose, "line", """{ "y": ["cob"] }""")));

        var validation = Validate(spec, useMmol: true);
        var error = validation.Errors.Should().ContainSingle().Subject;
        error.Path.Should().Be("panels[0].tracks[0].layers[2]");
        error.Message.Should().Be(
            "This track has a left axis in mmol/L and a right axis in U; this layer is in g. A track has at most two units: put this layer on another track.");
    }

    [Fact]
    public void Markers_and_spans_take_no_axis_so_they_add_no_unit()
    {
        var spec = Layers(string.Join(",",
            Layer(Glucose, "line", """{ "y": ["glucose"] }"""),
            Layer(Glucose, "line", """{ "y": ["iob"] }"""),
            Layer("""{ "name": "treatments" }""", "markers")));

        Validate(spec).Errors.Should().BeEmpty();
    }

    [Fact]
    public void A_count_with_no_unit_is_a_unit()
    {
        var error = SingleError(Layers(string.Join(",",
            Layer("""{ "name": "daily_profile" }""", "line", """{ "y": ["glucoseP50"] }"""),
            Layer("""{ "name": "daily_profile" }""", "line", """{ "y": ["iob"] }"""),
            Layer("""{ "name": "daily_profile" }""", "line", """{ "y": ["readings"] }"""))));

        error.Message.Should().Contain("this layer is in no unit");
    }

    [Fact]
    public void Every_layer_shares_the_first_layers_horizontal_axis()
    {
        var error = SingleError(Layers(Layer(Glucose, "line") + "," + Layer("""{ "name": "daily_profile" }""", "line")));

        error.Path.Should().Be("panels[0].tracks[0].layers[1].bindings.x");
        error.Message.Should().Contain("lies along time of day").And.Contain("first layer lies along time");
    }

    [Fact]
    public void A_layer_on_a_later_track_is_checked_against_the_first()
    {
        var error = SingleError(Chart(
            $$"""{ "layers": [ {{Layer(Glucose, "line")}} ] }, { "layers": [ {{Layer("""{ "name": "daily_summary" }""", "line")}} ] }"""));

        error.Path.Should().Be("panels[0].tracks[1].layers[0].bindings.x");
        error.Message.Should().Contain("lies along days");
    }

    [Fact]
    public void A_layers_own_range_is_refused_on_a_time_chart_and_taken_on_a_time_of_day_chart()
    {
        SingleError(Layers("""{ "dataset": { "name": "glucose_series" }, "range": { "hours": 3 } }"""))
            .Path.Should().Be("panels[0].tracks[0].layers[0].range");

        var spec = Validate(Layers(string.Join(",",
            Layer("""{ "name": "daily_profile" }""", "line"),
            """{ "dataset": { "name": "daily_profile" }, "mark": "line", "range": { "hours": 1440 } }""")));
        spec.Errors.Should().BeEmpty();
        spec.Spec!.Panels[0].Tracks![0].Layers[1].Range.Should().Be(new ReportRange { Hours = 1440 });
    }

    [Fact]
    public void A_layers_own_range_is_checked_against_its_dataset_at_the_layers_range()
    {
        var error = SingleError(Layers(string.Join(",",
            Layer("""{ "name": "daily_profile" }""", "line"),
            """{ "dataset": { "name": "daily_profile" }, "mark": "line", "range": { "from": "2026-06-01T00:00:00Z", "to": "2026-09-09T00:00:00Z" } }""")));

        error.Path.Should().Be("panels[0].tracks[0].layers[1].range");
        error.Message.Should().Contain("covers at most 90 days");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1441)]
    public void A_grid_outside_0_to_1440_is_refused(int grid)
    {
        SingleError(Layers(Layer(Glucose, "line"), $", \"grid\": {grid}")).Path.Should().Be("panels[0].grid");
    }

    [Fact]
    public void A_grid_on_a_day_or_category_chart_is_refused()
    {
        SingleError(Layers(Layer("""{ "name": "daily_summary" }""", "line"), ", \"grid\": 60"))
            .Should().BeEquivalentTo(new { Path = "panels[0].grid", Message = "A chart over days has one slot each day. Remove the grid." });
        SingleError(Layers(Layer("""{ "name": "glucose_distribution" }""", "bars"), ", \"grid\": 60"))
            .Path.Should().Be("panels[0].grid");
    }

    [Fact]
    public void A_field_with_no_aggregate_cannot_be_folded_onto_a_grid()
    {
        Validate(Layers(Layer("""{ "name": "event_response" }""", "band"), ", \"grid\": 15")).Errors.Should().BeEmpty();

        var registry = new ReportDatasetRegistry([new LooseDataset()]);
        var validator = new ReportSpecValidator(registry, TimeProvider.System);
        var spec = JsonDocument.Parse(Layers(Layer("""{ "name": "loose" }""", "line"), ", \"grid\": 15")).RootElement;
        var error = validator.Validate(spec).Errors.Should().ContainSingle().Subject;
        error.Path.Should().Be("panels[0].tracks[0].layers[0].bindings");
        error.Message.Should().Contain("'Level' cannot be folded onto the grid");
        validator.Validate(JsonDocument.Parse(Layers(Layer("""{ "name": "loose" }""", "line"))).RootElement).Errors.Should().BeEmpty();
    }

    private sealed record LooseParameters : ReportDatasetParameters
    {
        public override string DatasetName => "loose";
    }

    /// <summary>A dataset with a number field that declares no aggregate.</summary>
    private sealed class LooseDataset : IReportDataset
    {
        public string Name => "loose";
        public string Label => "Loose";
        public string Description => "Loose";
        public Type ParametersType => typeof(LooseParameters);
        public string RequiredScope => Nocturne.Core.Models.Authorization.Scope.GlucoseRead;
        public IReadOnlyList<ReportField> Fields { get; } =
        [
            new("time", "Time", ReportFieldKind.Time, null, "A time."),
            new("level", "Level", ReportFieldKind.Number, null, "A level."),
        ];
        public IReadOnlyList<ReportPanelKind> PanelKinds { get; } = [];
        public IReadOnlyList<ReportMark> Marks { get; } = [ReportMark.Line];
        public TimeSpan DefaultRange => TimeSpan.FromHours(1);
        public TimeSpan MaxRange => TimeSpan.FromDays(1);
        public int PointBudget => 1;
        public IEnumerable<(string Field, string Message)> Validate(ReportDatasetParameters parameters) => [];
        public Task<ReportDatasetOutput> RunAsync(ReportDatasetRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public void Every_reading_is_refused_on_a_chart_with_a_layer_of_two_sources()
    {
        var error = SingleError(Layers(
            $$"""{ "derived": { "op": "scatter", "a": {{Source("iob")}}, "b": {{Source("glucose")}} } }""", ", \"grid\": 0"));

        error.Path.Should().Be("panels[0].grid");
        error.Message.Should().Contain("Send a grid of 1 minute or more");
    }

    [Fact]
    public void Every_reading_is_taken_on_a_chart_of_one_source()
    {
        Validate(Layers($$"""{ "derived": { "op": "rolling_mean", "a": {{Source("glucose")}}, "minutes": 60 } }""", ", \"grid\": 0"))
            .Errors.Should().BeEmpty();
    }

    [Fact]
    public void A_scatter_lies_along_values_and_a_lag_correlation_along_lags()
    {
        var spec = Validate($$"""
            { "version": 1, "panels": [
              { "kind": "chart", "grid": 15, "tracks": [ { "layers": [ { "derived": { "op": "scatter", "a": {{Source("iob")}}, "b": {{Source("glucose")}} } } ] } ] },
              { "kind": "chart", "grid": 15, "tracks": [ { "layers": [ { "derived": { "op": "lag_correlation", "a": {{Source("iob")}}, "b": {{Source("glucose")}} } } ] } ] } ] }
            """).Spec!;

        var scatter = spec.Panels[0].Tracks![0].Layers[0];
        scatter.XKind.Should().Be(ReportXKind.Value);
        scatter.Mark.Should().Be(ReportMark.Points);
        scatter.Bindings.Should().BeEquivalentTo(new ReportLayerBindings { X = "a", Y = ["b"], Fit = "fit" });
        scatter.Unit.Should().Be("mg/dL");
        var lag = spec.Panels[1].Tracks![0].Layers[0];
        lag.XKind.Should().Be(ReportXKind.Lag);
        lag.Unit.Should().BeNull();
    }

    [Fact]
    public void A_difference_needs_both_sources_in_one_unit()
    {
        var error = SingleError(Layers($$"""{ "derived": { "op": "difference", "a": {{Source("glucose")}}, "b": {{Source("iob")}} } }"""));

        error.Path.Should().Be("panels[0].tracks[0].layers[0].derived.b.field");
        error.Message.Should().Contain("a is in mg/dL, b in U");
    }

    [Fact]
    public void A_ratio_takes_the_unit_of_a_over_b()
    {
        Validate(Layers($$"""{ "derived": { "op": "ratio", "a": {{Source("glucose")}}, "b": {{Source("iob")}} } }"""))
            .Spec!.Panels[0].Tracks![0].Layers[0].Unit.Should().Be("mg/dL/U");
    }

    [Fact]
    public void Two_sources_must_lie_along_one_axis()
    {
        var error = SingleError(Layers($$"""{ "derived": { "op": "difference", "a": {{Source("glucose")}}, "b": {{Source("glucoseP50", """{ "name": "daily_profile" }""")}} } }"""));

        error.Path.Should().Be("panels[0].tracks[0].layers[0].derived.b");
        error.Message.Should().Contain("a lies along time, b along time of day");
    }

    [Fact]
    public void A_lag_correlation_needs_sources_over_time()
    {
        var p50 = Source("glucoseP50", """{ "name": "daily_profile" }""");
        var error = SingleError(Layers($$"""{ "derived": { "op": "lag_correlation", "a": {{p50}}, "b": {{p50}} } }"""));

        error.Path.Should().Be("panels[0].tracks[0].layers[0].derived");
        error.Message.Should().Contain("needs sources along time");
    }

    [Fact]
    public void A_source_field_must_be_a_number_with_an_aggregate()
    {
        var error = SingleError(Layers($$"""{ "derived": { "op": "rolling_mean", "a": {{Source("direction")}} } }"""));

        error.Path.Should().Be("panels[0].tracks[0].layers[0].derived.a.field");
        error.Message.Should().Contain("'direction' cannot be a source").And.Contain("glucose, glucoseMgdl");
    }

    [Fact]
    public void A_source_may_be_derived_once_and_no_deeper()
    {
        var difference = $$"""{ "op": "difference", "a": {{Source("glucose")}}, "b": {{Source("glucoseMgdl")}} }""";
        var trend = $$"""{ "derived": { "op": "trend", "confidence": true, "a": { "derived": {{difference}}, "field": "value" } } }""";
        var spec = Validate(Layers(trend));
        spec.Errors.Should().BeEmpty();
        spec.Spec!.Panels[0].Tracks![0].Layers[0].Mark.Should().Be(ReportMark.Line);

        var deep = $$"""{ "op": "rolling_mean", "a": { "derived": {{difference}}, "field": "value" } }""";
        var error = SingleError(Layers($$"""{ "derived": { "op": "trend", "a": { "derived": {{deep}}, "field": "value" } } }"""));
        error.Path.Should().Be("panels[0].tracks[0].layers[0].derived.a.derived.a.derived");
    }

    [Fact]
    public void Op_settings_are_read_and_checked_at_their_path()
    {
        SingleError(Layers($$"""{ "derived": { "op": "rolling_mean", "a": {{Source("glucose")}}, "minutes": 5 } }"""))
            .Path.Should().Be("panels[0].tracks[0].layers[0].derived.minutes");
        SingleError(Layers($$"""{ "derived": { "op": "rolling_mean", "a": {{Source("glucose")}}, "minutes": "an hour" } }"""))
            .Path.Should().Be("panels[0].tracks[0].layers[0].derived.minutes");
        SingleError(Layers($$"""{ "derived": { "op": "rolling_mean", "a": {{Source("glucose")}}, "b": {{Source("glucose")}} } }"""))
            .Path.Should().Be("panels[0].tracks[0].layers[0].derived.b");
        SingleError(Layers("""{ "derived": { "op": "median" } }""")).Path.Should().Be("panels[0].tracks[0].layers[0].derived.op");
        SingleError(Layers("""{ "derived": { "op": "difference", "a": { "dataset": { "name": "glucose_series" }, "field": "glucose" } } }"""))
            .Path.Should().Be("panels[0].tracks[0].layers[0].derived.b");
    }

    [Fact]
    public void A_band_trend_needs_its_confidence_interval()
    {
        var error = SingleError(Layers($$"""{ "derived": { "op": "trend", "a": {{Source("glucose")}} }, "mark": "band" }"""));

        error.Path.Should().Be("panels[0].tracks[0].layers[0].mark");
    }

    [Fact]
    public void A_layer_takes_a_dataset_or_a_derived_series_not_both()
    {
        SingleError(Layers("""{ "dataset": { "name": "glucose_series" }, "derived": { "op": "trend" } }""")).Path.Should().Be("panels[0].tracks[0].layers[0]");
        SingleError(Layers("""{ "mark": "line" }""")).Path.Should().Be("panels[0].tracks[0].layers[0]");
    }

    [Fact]
    public void A_markers_layer_takes_only_marker_fields()
    {
        var error = SingleError(Layers(Layer("""{ "name": "treatments" }""", "markers", """{ "y": ["rate"] }""")));

        error.Path.Should().Be("panels[0].tracks[0].layers[0].bindings.y");
        error.Message.Should().Contain("has no marker").And.Contain("insulin, injectionUnits, carbs, bgCheck");
    }

    [Fact]
    public void Dataset_parameters_are_checked_by_the_dataset()
    {
        SingleError("""{ "version": 1, "panels": [ { "kind": "table", "dataset": { "name": "treatments", "minCarbs": -1 } } ] }""")
            .Path.Should().Be("panels[0].dataset.minCarbs");
        SingleError(Layers(Layer("""{ "name": "daily_profile", "dayOfWeek": ["Sat", "Sat"] }""", "band")))
            .Path.Should().Be("panels[0].tracks[0].layers[0].dataset.dayOfWeek");
    }

    [Fact]
    public void An_unknown_parameter_is_refused()
    {
        SingleError("""{ "version": 1, "panels": [ { "kind": "table", "dataset": { "name": "glucose_series", "intervalMinutes": 5 } } ] }""")
            .Path.Should().Be("panels[0].dataset.intervalMinutes");
    }

    [Fact]
    public void Lists_every_error_not_only_the_first()
    {
        var validation = Validate("""
            { "panels": [
              { "kind": "table", "dataset": { "name": "nope" } },
              { "kind": "chart", "tracks": [ { "layers": [ { "dataset": { "name": "glucose_series" }, "bindings": { "y": "x", "columns": ["time"] } } ] } ] },
              { "id": "a b", "kind": "table", "dataset": { "name": "glucose_series" }, "colour": "red" } ] }
            """);

        validation.Errors.Select(e => e.Path).Should().BeEquivalentTo(
            "version",
            "panels[0].dataset.name",
            "panels[1].tracks[0].layers[0].bindings.columns",
            "panels[2].colour",
            "panels[2].id");
    }

    [Fact]
    public void Duplicate_ids_are_refused()
    {
        SingleError($$"""{ "version": 1, "panels": [ { "id": "a", "kind": "table", "dataset": { "name": "glucose_series" } }, { "id": "a", "kind": "table", "dataset": { "name": "glucose_series" } } ] }""")
            .Path.Should().Be("panels[1].id");
        SingleError(Layers("""{ "id": "x", "dataset": { "name": "glucose_series" } }, { "id": "x", "dataset": { "name": "glucose_series" } }"""))
            .Path.Should().Be("panels[0].tracks[0].layers[1].id");
    }

    [Theory]
    [InlineData("""{ "hours": 0 }""", "range.hours")]
    [InlineData("""{ "hours": 6, "from": "2026-10-01T00:00:00Z" }""", "range")]
    [InlineData("""{ "from": "2026-10-02T00:00:00Z", "to": "2026-10-01T00:00:00Z" }""", "range.to")]
    [InlineData("""{ "from": "yesterday", "to": "2026-10-01T00:00:00Z" }""", "range.from")]
    public void Malformed_ranges(string range, string path)
    {
        SingleError($$"""{ "version": 1, "range": {{range}}, "panels": [ {{Table}} ] }""").Path.Should().Be(path);
    }

    [Fact]
    public void Not_an_object()
    {
        SingleError("[]").Path.Should().Be("spec");
        Validator.Validate(default).Errors.Should().ContainSingle().Which.Path.Should().Be("spec");
    }

    [Fact]
    public void Every_template_and_op_default_validates()
    {
        foreach (var op in ReportDerivedOps.All)
        {
            var b = op.SourceCount == 2 ? $", \"b\": {Source("glucose")}" : "";
            var a = op.Name == "difference" ? Source("glucoseMgdl") : Source("glucose");
            var json = Layers($$"""{ "derived": { "op": "{{op.Name}}", "a": {{a}}{{b}} } }""", ", \"grid\": 15");
            Validate(json).Errors.Should().BeEmpty(op.Name);
        }
    }

    /// <summary>Every setting field of every dataset, as a source of every op, in each place a source goes.</summary>
    public static TheoryData<string, string, string> SettingSources()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var dataset in Registry.All)
            foreach (var field in dataset.FieldsFor(ReportPanelKind.Chart).Where(f => f.Setting != ReportSetting.None && f.Aggregate is not null))
                foreach (var op in ReportDerivedOps.All)
                {
                    data.Add(op.Name, $"{dataset.Name}.{field.Name}", "a");
                    if (op.SourceCount == 2)
                        data.Add(op.Name, $"{dataset.Name}.{field.Name}", "b");
                }
        return data;
    }

    [Theory]
    [MemberData(nameof(SettingSources))]
    public void No_op_takes_a_profile_setting_as_a_source(string op, string source, string at)
    {
        var (dataset, field) = (source.Split('.')[0], source.Split('.')[1]);
        var setting = Source(field, $$"""{ "name": "{{dataset}}" }""");
        var data = Source("glucose");
        var sources = ReportDerivedOps.Find(op)!.SourceCount == 1 ? $"\"a\": {setting}"
            : at == "a" ? $"\"a\": {setting}, \"b\": {data}" : $"\"a\": {data}, \"b\": {setting}";

        var errors = Validate(Layers($$"""{ "derived": { "op": "{{op}}", {{sources}} } }""", ", \"grid\": 15")).Errors;

        var reason = Registry.Find(dataset)!.Fields.Single(f => f.Name == field).Setting switch
        {
            ReportSetting.Profile => "is a value of the profile's settings, which a derived series is never compared with",
            ReportSetting.ProfileRelative => "is worked out against the profile's scheduled basal",
            _ => "is a fixed limit that is the same for everyone, not data",
        };
        errors.Should().Contain(e => e.Path == $"panels[0].tracks[0].layers[0].derived.{at}.field"
            && e.Message.Contains(reason) && e.Message.Contains("A derived series uses the data only."));
    }

    private const string TwoCohorts = """{ "name": "event_response", "cohorts": [ { "anchor": "Meal" }, { "anchor": "CorrectionBolus" } ] }""";

    [Fact]
    public void A_source_of_data_in_groups_names_the_group_it_reads()
    {
        var missing = SingleError(Layers($$"""{ "derived": { "op": "rolling_mean", "a": {{Source("glucoseP50", TwoCohorts)}} } }"""));
        missing.Path.Should().Be("panels[0].tracks[0].layers[0].derived.a.series");
        missing.Message.Should().Contain("This data comes in 2 groups").And.Contain("1 to 2");

        SingleError(Layers($$"""{ "derived": { "op": "rolling_mean", "a": { "dataset": {{TwoCohorts}}, "field": "glucoseP50", "series": 3 } } }"""))
            .Path.Should().Be("panels[0].tracks[0].layers[0].derived.a.series");
        SingleError(Layers($$"""{ "derived": { "op": "rolling_mean", "a": { "dataset": {{Glucose}}, "field": "glucose", "series": 1 } } }"""))
            .Message.Should().Contain("no groups");

        var spec = Validate(Layers($$"""{ "derived": { "op": "rolling_mean", "a": { "dataset": {{TwoCohorts}}, "field": "glucoseP50", "series": 2 } } }"""));
        spec.Errors.Should().BeEmpty();
        spec.Spec!.Panels[0].Tracks![0].Layers[0].Derived!.Sources[0].Series.Should().Be(2);
    }

    [Fact]
    public void Every_reading_cannot_be_shown_when_two_sources_pair_inside_a_source()
    {
        var difference = $$"""{ "op": "difference", "a": {{Source("glucose")}}, "b": {{Source("glucoseMgdl")}} }""";

        var error = SingleError(Layers($$"""{ "derived": { "op": "trend", "a": { "derived": {{difference}}, "field": "value" } } }""", ", \"grid\": 0"));

        error.Path.Should().Be("panels[0].grid");
        error.Message.Should().Contain("Difference at panels[0].tracks[0].layers[0].derived.a.derived pairs two sources");
    }

    [Theory]
    [InlineData("rolling_mean", 15, 60, "60")]
    [InlineData("band_sd", 100, 60, "120")]
    [InlineData("rolling_mean", 45, 30, "60")]
    public void A_window_that_is_not_a_whole_number_of_the_grids_slots_is_refused(string op, int minutes, int grid, string suggested)
    {
        var error = SingleError(Layers($$"""{ "derived": { "op": "{{op}}", "minutes": {{minutes}}, "a": {{Source("glucose")}} } }""",
            $", \"grid\": {grid}", """, "range": { "hours": 48 }"""));

        error.Path.Should().Be("panels[0].tracks[0].layers[0].derived.minutes");
        error.Message.Should().Contain($"The grid here is {grid} minutes").And.Contain($"such as {suggested}");
        Validate(Layers($$"""{ "derived": { "op": "{{op}}", "minutes": {{grid * 2}}, "a": {{Source("glucose")}} } }""",
            $", \"grid\": {grid}", """, "range": { "hours": 48 }""")).Errors.Should().BeEmpty();
    }

    [Fact]
    public void A_window_on_a_day_axis_is_in_whole_days()
    {
        var daily = """{ "name": "daily_summary" }""";
        var error = SingleError(Layers($$"""{ "derived": { "op": "rolling_mean", "a": {{Source("timeInRange", daily)}}, "minutes": 60 } }""",
            ", \"range\": { \"hours\": 720 }"));

        error.Path.Should().Be("panels[0].tracks[0].layers[0].derived.minutes");
        error.Message.Should().Contain("whole days");
        Validate(Layers($$"""{ "derived": { "op": "band_sd", "a": {{Source("timeInRange", daily)}}, "minutes": 10080 } }""",
            ", \"range\": { \"hours\": 720 }")).Errors.Should().BeEmpty();
    }

    private static string L(string bindings, string dataset = Glucose, string mark = "line") => Layers(Layer(dataset, mark, bindings));

    /// <summary>Malformed specs, each with the path its error must name.</summary>
    public static TheoryData<string, string> Malformed => new()
    {
        { $$"""{ "version": "1", "panels": [{{Table}}] }""", "version" },
        { $$"""{ "version": 1, "range": { "hours": "6" }, "panels": [{{Table}}] }""", "range.hours" },
        { $$"""{ "version": 1, "range": { "hours": 2147483647 }, "panels": [{{Table}}] }""", "range.hours" },
        { L("""{ "x": [], "y": ["glucose"] }"""), "panels[0].tracks[0].layers[0].bindings.x" },
        { L("""{ "x": 5, "y": ["glucose"] }"""), "panels[0].tracks[0].layers[0].bindings.x" },
        { L("""{ "x": "glucose", "y": ["glucose"] }"""), "panels[0].tracks[0].layers[0].bindings.x" },
        { L("""{ "y": "glucose" }"""), "panels[0].tracks[0].layers[0].bindings.y" },
        { L("""{ "y": ["direction"] }"""), "panels[0].tracks[0].layers[0].bindings.y" },
        { L("""{ "y": ["glucose", "glucose"] }"""), "panels[0].tracks[0].layers[0].bindings.y" },
        { L("""{ "y": ["glucose"], "stacked": true }"""), "panels[0].tracks[0].layers[0].bindings.stacked" },
        { L("""{ "y": ["glucose"], "edges": ["targetLow", "targetHigh"] }"""), "panels[0].tracks[0].layers[0].bindings.edges" },
        { L("""{ "y": ["glucose"], "colour": "red" }"""), "panels[0].tracks[0].layers[0].bindings.colour" },
        { L("[]"), "panels[0].tracks[0].layers[0].bindings" },
        { L("""{ "edges": ["targetLow"] }""", Glucose, "band"), "panels[0].tracks[0].layers[0].bindings.edges" },
        { L("""{ "edges": ["targetLow", "iob"] }""", Glucose, "band"), "panels[0].tracks[0].layers[0].bindings.edges" },
        { L("""{ "edges": ["targetLow", "targetHigh"], "y": ["glucose", "glucoseMgdl"] }""", Glucose, "band"), "panels[0].tracks[0].layers[0].bindings.y" },
        { L("""{ "y": ["glucoseP50"], "edges": ["glucoseP10", "glucoseP90"], "series": "minute" }""", """{ "name": "event_response" }""", "band"), "panels[0].tracks[0].layers[0].bindings.series" },
        { L("""{ "x": "band", "y": ["percent"], "stacked": "yes" }""", """{ "name": "glucose_distribution" }""", "bars"), "panels[0].tracks[0].layers[0].bindings.stacked" },
        { L("""{ "x": "band", "y": ["percent"], "orientation": "sideways" }""", """{ "name": "glucose_distribution" }""", "bars"), "panels[0].tracks[0].layers[0].bindings.orientation" },
        { L("""{ "x": "period", "y": ["veryLow", "minutes"] }""", """{ "name": "glucose_distribution" }""", "bars"), "panels[0].tracks[0].layers[0].bindings.y" },
        { L("""{ "x": "band", "y": ["range"] }""", """{ "name": "glucose_distribution" }""", "bars"), "panels[0].tracks[0].layers[0].bindings.y" },
        { L("""{ "start": "start", "end": "kind", "category": "kind" }""", """{ "name": "state_spans" }""", "spans"), "panels[0].tracks[0].layers[0].bindings.end" },
        { L("""{ "start": "start", "end": "end", "category": "kind", "x": "start" }""", """{ "name": "state_spans" }""", "spans"), "panels[0].tracks[0].layers[0].bindings.x" },
        { Layers("""{ "dataset": { "name": "glucose_series" }, "mark": "histogram" }"""), "panels[0].tracks[0].layers[0].mark" },
        { Layers("""{ "dataset": { "name": "glucose_series" }, "mark": 3 }"""), "panels[0].tracks[0].layers[0].mark" },
        { Layers("""{ "dataset": "glucose_series" }"""), "panels[0].tracks[0].layers[0].dataset" },
        { Layers("1"), "panels[0].tracks[0].layers[0]" },
        { Chart("1"), "panels[0].tracks[0]" },
        { Chart("""{ "layers": [] }"""), "panels[0].tracks[0].layers" },
        { Chart("""{ "layers": [ { "dataset": { "name": "glucose_series" } } ], "height": 9 }"""), "panels[0].tracks[0].height" },
        { Chart("""{ "layers": [ { "dataset": { "name": "glucose_series" } } ], "lines": 2 }"""), "panels[0].tracks[0].lines" },
        { """{ "version": 1, "panels": [ { "kind": "chart" } ] }""", "panels[0].tracks" },
        { """{ "version": 1, "panels": [ { "kind": "chart", "tracks": {} } ] }""", "panels[0].tracks" },
        { """{ "version": 1, "panels": [ { "kind": 3, "dataset": { "name": "glucose_series" } } ] }""", "panels[0].kind" },
        { """{ "version": 1, "panels": [ { "kind": "histogram", "dataset": { "name": "glucose_series" } } ] }""", "panels[0].kind" },
        { Layers(Layer(Glucose, "line"), ", \"grid\": \"five\""), "panels[0].grid" },
        { Layers("""{ "derived": "trend" }"""), "panels[0].tracks[0].layers[0].derived" },
        { Layers("""{ "derived": { "op": 1 } }"""), "panels[0].tracks[0].layers[0].derived.op" },
        { Layers("""{ "derived": { "op": "trend", "a": { "field": "glucose" } } }"""), "panels[0].tracks[0].layers[0].derived.a" },
        { Layers("""{ "derived": { "op": "trend", "a": { "dataset": { "name": "glucose_series" } } } }"""), "panels[0].tracks[0].layers[0].derived.a.field" },
        { Layers("""{ "derived": { "op": "trend", "a": { "dataset": { "name": "glucose_distribution" }, "field": "percent" } } }"""), "panels[0].tracks[0].layers[0].derived.a.dataset.name" },
        { Layers("""{ "derived": { "op": "trend", "a": { "dataset": { "name": "glucose_series" }, "field": "glucose", "colour": 1 } } }"""), "panels[0].tracks[0].layers[0].derived.a.colour" },
        { Layers("""{ "derived": { "op": "trend", "a": { "dataset": { "name": "glucose_series" }, "field": "glucose" }, "colour": 1 } }"""), "panels[0].tracks[0].layers[0].derived.colour" },
        { Layers("""{ "derived": { "op": "trend", "a": { "derived": { "op": "scatter", "a": { "dataset": { "name": "glucose_series" }, "field": "iob" }, "b": { "dataset": { "name": "glucose_series" }, "field": "glucose" } }, "field": "b" } } }"""), "panels[0].tracks[0].layers[0].derived.a.derived.op" },
        { Layers("""{ "derived": { "op": "trend", "a": { "derived": { "op": "difference", "a": { "dataset": { "name": "glucose_series" }, "field": "glucose" }, "b": { "dataset": { "name": "glucose_series" }, "field": "glucoseMgdl" } }, "field": "x" } } }"""), "panels[0].tracks[0].layers[0].derived.a.field" },
        { """{ "version": 1, "panels": [ { "kind": "table", "dataset": { "name": "event_response", "cohorts": [ { "anchor": "Lunch" } ] } } ] }""", "panels[0].dataset.cohorts[0].anchor" },
        { """{ "version": 1, "panels": [ { "kind": "table", "dataset": { "name": "event_response", "cohorts": [] } } ] }""", "panels[0].dataset.cohorts" },
        { """{ "version": 1, "panels": [ { "kind": "table", "dataset": { "name": "event_response" }, "bindings": { "columns": ["glucoseP50"] } } ] }""", "panels[0].bindings.columns" },
        { """{ "version": 1, "panels": [ { "kind": "table", "dataset": { "name": "glucose_series" }, "bindings": { "columns": [1, 2] } } ] }""", "panels[0].bindings.columns" },
        { """{ "version": 1, "panels": [ { "kind": "table", "dataset": { "name": "glucose_series" }, "bindings": { "y": ["glucose"] } } ] }""", "panels[0].bindings.y" },
        { """{ "version": 1, "panels": [ { "kind": "table", "dataset": { "name": "glucose_series" }, "bindings": [] } ] }""", "panels[0].bindings" },
        { """{ "version": 1, "panels": [ { "kind": "stat_tiles", "dataset": { "name": "device_status" }, "bindings": { "columns": ["time"] } } ] }""", "panels[0].bindings.columns" },
        { """{ "version": 1, "panels": [ { "kind": "table", "dataset": { "name": "treatments", "types": ["insulin"] } } ] }""", "panels[0].dataset.types[0]" },
        { """{ "version": 1, "panels": [ { "kind": "table", "dataset": { "name": "treatments" }, "range": { "hours": 400 } } ] }""", "panels[0].range" },
        { """{ "version": 1, "panels": [ { "kind": "table", "dataset": { "name": "state_spans", "kinds": ["mode"] } } ] }""", "panels[0].dataset.kinds[0]" },
        { """{ "version": 1, "panels": [ { "id": 7, "kind": "table", "dataset": { "name": "glucose_series" } } ] }""", "panels[0].id" },
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
