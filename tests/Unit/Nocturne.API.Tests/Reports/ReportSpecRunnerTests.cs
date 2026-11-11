using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.API.Reports;
using Nocturne.API.Reports.Datasets;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Xunit;
using static Nocturne.API.Tests.Reports.ReportSpecValidatorTests;

namespace Nocturne.API.Tests.Reports;

public class ReportSpecRunnerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static ReportSpec Spec(string json, IReportDatasetRegistry? registry = null) =>
        new ReportSpecValidator(registry ?? Registry, new FixedTime(Now)).Validate(JsonDocument.Parse(json).RootElement) is { IsValid: true } v
            ? v.Spec!
            : throw new InvalidOperationException("The test spec is not valid: "
                + string.Join("; ", new ReportSpecValidator(registry ?? Registry, new FixedTime(Now)).Validate(JsonDocument.Parse(json).RootElement).Errors));

    /// <summary>One reading every <paramref name="spacingMinutes"/> minutes over the window, with IOB, COB and basal series, a bolus and spans.</summary>
    private static DashboardChartData ChartData(long from, long to, int spacingMinutes = 5)
    {
        var data = new DashboardChartData
        {
            Thresholds = new ChartThresholdsDto { Low = 70, High = 180, VeryLow = 54, VeryHigh = 250, GlucoseYMax = 300 },
        };
        for (var t = from; t <= to; t += spacingMinutes * 60_000)
        {
            var i = (t - from) / (spacingMinutes * 60_000);
            data.GlucoseData.Add(new GlucosePointDto { Time = t, Sgv = 100 + i % 50, Direction = "Flat" });
            data.IobSeries.Add(new TimeSeriesPoint { Timestamp = t, Value = 1.5 });
            data.CobSeries.Add(new TimeSeriesPoint { Timestamp = t, Value = 20 });
            data.BasalSeries.Add(new BasalPoint { Timestamp = t, Rate = 0.8 });
        }
        data.BolusMarkers.Add(new BolusMarkerDto { Time = from, Insulin = 2 });
        data.PumpModeSpans.Add(new ChartStateSpanDto { Id = "m", State = "Automatic", StartMills = from - 3_600_000, EndMills = null, Color = ChartColor.PumpModeAutomatic });
        data.OverrideSpans.Add(new ChartStateSpanDto { Id = "o", State = "Exercise", StartMills = from + 600_000, EndMills = from + 1_200_000, Color = ChartColor.Override });
        return data;
    }

    private static (ReportSpecRunner Runner, ReportRunContext Context, ReportReadLog ReadLog, Mock<IChartDataService> Chart) Arrange(
        IReportDatasetRegistry registry, params string[] scopes) => ArrangeWith(registry, 5, false, scopes);

    private static (ReportSpecRunner Runner, ReportRunContext Context, ReportReadLog ReadLog, Mock<IChartDataService> Chart) ArrangeWith(
        IReportDatasetRegistry registry, int spacingMinutes, bool clamped, params string[] scopes)
    {
        var chartData = new Mock<IChartDataService>();
        chartData
            .Setup(c => c.GetDashboardChartDataAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long from, long to, int _, bool _, CancellationToken _) => ChartData(from, to, spacingMinutes));

        var services = new ServiceCollection()
            .AddSingleton(chartData.Object)
            .AddSingleton<IStatisticsService>(new StatisticsService(TimeProvider.System))
            .AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = new DefaultHttpContext() })
            .BuildServiceProvider();

        var readLog = new ReportReadLog();
        var context = new ReportRunContext(false, scopes.ToHashSet(), services, readLog, clamped);
        return (new ReportSpecRunner(registry, new FixedTime(Now)), context, readLog, chartData);
    }

    [Fact]
    public async Task Fields_the_token_cannot_read_are_dropped_and_named()
    {
        var (runner, context, _, _) = Arrange(Registry, Scope.GlucoseRead);
        var spec = Spec("""
            { "version": 1, "range": { "hours": 3 }, "panels": [
              { "kind": "table", "dataset": { "name": "glucose_series" }, "bindings": { "columns": ["time", "glucose", "iob", "cob"] } } ] }
            """);

        var panel = (await runner.RunAsync(spec, context, CancellationToken.None)).Panels.Single();

        panel.Status.Should().Be(ReportPanelStatus.Ok);
        panel.Fields.Select(f => f.Name).Should().Equal("time", "glucose");
        panel.Rows.Should().NotBeEmpty().And.OnlyContain(r => r.Keys.SequenceEqual(new[] { "time", "glucose" }));
        panel.Omitted.Select(o => o.Field).Should().Equal("iob", "cob", "basalRate");
        panel.Summary.Should().Contain("Fields omitted: iob, cob, basalRate (scope not granted).");
    }

    [Fact]
    public async Task With_the_scope_the_fields_carry_the_series_at_each_reading()
    {
        var (runner, context, readLog, _) = Arrange(Registry, Scope.GlucoseRead, Scope.TreatmentsRead);
        var spec = Spec("""
            { "version": 1, "range": { "hours": 1 }, "panels": [
              { "kind": "table", "dataset": { "name": "glucose_series" }, "bindings": { "columns": ["glucose", "iob", "cob", "basalRate"] } } ] }
            """);

        var panel = (await runner.RunAsync(spec, context, CancellationToken.None)).Panels.Single();

        panel.Omitted.Should().BeEmpty();
        panel.Rows[0].Should().Contain(new KeyValuePair<string, object?>("iob", 1.5))
            .And.Contain(new KeyValuePair<string, object?>("cob", 20.0))
            .And.Contain(new KeyValuePair<string, object?>("basalRate", 0.8));
        readLog.Datasets.Should().Contain(["glucose", "treatments"]);
    }

    [Fact]
    public async Task A_long_range_is_downsampled_within_the_point_budget()
    {
        var (runner, context, _, _) = Arrange(Registry, Scope.GlucoseRead);
        var spec = Spec(Layers(Layer("""{ "name": "glucose_series" }""", "line"), reportExtras: """, "range": { "hours": 720 }"""));

        var layer = (await runner.RunAsync(spec, context, CancellationToken.None)).Panels.Single().Tracks[0].Layers[0];

        layer.Rows.Count.Should().BeLessThanOrEqualTo(GlucoseSeriesDataset.Budget).And.BeGreaterThan(GlucoseSeriesDataset.Budget / 2);
        layer.Resolution!.IntervalMinutes.Should().Be(22, "30 days of minutes over 2000 points, rounded up");
        layer.Resolution.SourceRows.Should().Be(30 * 288 + 1);
        layer.Resolution.Downsampled.Should().BeTrue();
        layer.Summary.Should().Contain($"Readings: {30 * 288 + 1}.", "the summary covers every reading, not the sampled rows");
    }

    [Fact]
    public async Task A_history_clamped_caller_gets_the_last_24_hours_and_is_told()
    {
        var (runner, context, _, _) = ArrangeWith(Registry, 5, true, Scope.GlucoseRead);
        var spec = Spec("""
            { "version": 1, "range": { "hours": 72 }, "panels": [
              { "kind": "chart", "tracks": [ { "layers": [ { "dataset": { "name": "glucose_series" } } ] } ] },
              { "kind": "table", "dataset": { "name": "glucose_series" }, "range": { "hours": 2 } } ] }
            """);

        var result = await runner.RunAsync(spec, context, CancellationToken.None);

        var clipped = result.Panels[0];
        clipped.Window.From.Should().Be(Now.AddHours(-24).ToUnixTimeMilliseconds());
        clipped.Limited.Should().BeTrue();
        clipped.Tracks[0].Layers[0].Summary.Should().StartWith("Glucose from 2026-10-28 12:00").And.Contain("Readings: 289.");
        clipped.Summary.Should().EndWith(ReportSpecRunner.LimitedNote);
        result.Panels[1].Limited.Should().BeFalse("a range inside the last day is not cut");
        result.Warnings.Should().ContainSingle().Which.Should().Be($"Panel p1: {ReportSpecRunner.LimitedNote}");
    }

    [Fact]
    public async Task A_history_clamped_caller_asking_only_for_older_data_gets_no_panel_data()
    {
        var (runner, context, _, _) = ArrangeWith(Registry, 5, true, Scope.GlucoseRead);
        var spec = Spec($$"""
            { "version": 1, "range": { "from": "{{Now.AddDays(-5):O}}", "to": "{{Now.AddDays(-4):O}}" }, "panels": [
              { "kind": "table", "dataset": { "name": "glucose_series" } } ] }
            """);

        var panel = (await runner.RunAsync(spec, context, CancellationToken.None)).Panels.Single();

        panel.Status.Should().Be(ReportPanelStatus.NotPermitted);
        panel.Rows.Should().BeEmpty();
    }

    [Fact]
    public void Downsampling_keeps_real_readings_one_per_slot()
    {
        var readings = Enumerable.Range(0, 12).Select(i => new GlucosePointDto { Time = i * 60_000L, Sgv = 100 + i }).ToList();

        var kept = GlucoseSeriesDataset.Downsample(readings, 0, 5);

        kept.Select(r => r.Sgv).Should().Equal(100, 105, 110);
    }

    private static string GlucoseChart(int hours) => ReportTemplatesJson(ReportTemplates.GlucoseSpec(hours));

    private static string ReportTemplatesJson(object spec) => JsonSerializer.Serialize(spec);

    [Fact]
    public async Task The_four_track_glucose_chart_reads_each_query_once_and_sides_its_units()
    {
        var (runner, context, readLog, chart) = Arrange(Registry, Scope.GlucoseRead, Scope.TreatmentsRead, Scope.DevicesRead, Scope.TherapyRead);

        var panel = (await runner.RunAsync(Spec(GlucoseChart(3)), context, CancellationToken.None)).Panels.Single();

        panel.Status.Should().Be(ReportPanelStatus.Ok);
        panel.Tracks.Select(t => t.Id).Should().Equal("basal", "glucose", "onboard", "states");
        chart.Verify(c => c.GetDashboardChartDataAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<int>(), false, It.IsAny<CancellationToken>()),
            Times.Once, "glucose_series and state_spans share one chart payload per range");
        context.Cache.Reads.Should().Be(4, "basal, treatments, state spans, and one glucose_series query for the band, the line, IOB and COB");

        var onboard = panel.Tracks[2].Layers;
        onboard.Select(l => (l.Unit, l.Side)).Should().Equal(("U", ReportAxisSide.Left), ("g", ReportAxisSide.Right));
        var glucose = panel.Tracks[1].Layers;
        glucose.Select(l => l.Side).Should().Equal(ReportAxisSide.Left, ReportAxisSide.Left, ReportAxisSide.None);
        glucose[2].Status.Should().Be(ReportPanelStatus.Failed, "the treatments repositories are not registered in this test");
        panel.Sentence.Should().Contain("Not shown:").And.Contain("Boluses and carbs (an error occurred when it was read)");

        var spans = panel.Tracks[3].Layers.Single();
        spans.Rows.Should().HaveCount(2);
        spans.Rows.Should().Contain(r => (string?)r["label"] == "Automatic" && (long)r["start"]! == Now.AddHours(-3).ToUnixTimeMilliseconds());
        readLog.Datasets.Should().Contain(["glucose", "treatments", "pump_modes"]);
    }

    [Fact]
    public async Task A_glucose_only_viewer_draws_glucose_and_is_told_what_is_not_shown()
    {
        var (runner, context, _, _) = Arrange(Registry, Scope.GlucoseRead);

        var panel = (await runner.RunAsync(Spec(GlucoseChart(3)), context, CancellationToken.None)).Panels.Single();

        panel.Status.Should().Be(ReportPanelStatus.Ok);
        var layers = panel.Tracks.SelectMany(t => t.Layers).ToDictionary(l => l.Id);
        layers["glucose"].Status.Should().Be(ReportPanelStatus.Ok);
        layers["glucose"].Rows.Should().NotBeEmpty();
        layers["basal"].Status.Should().Be(ReportPanelStatus.NotPermitted);
        layers["spans"].Status.Should().Be(ReportPanelStatus.NotPermitted);
        layers["treatments"].Status.Should().Be(ReportPanelStatus.NotPermitted);
        layers["iob"].Status.Should().Be(ReportPanelStatus.NotPermitted);
        layers["iob"].Sentence.Should().Contain("treatments permission");
        panel.Sentence.Should().Contain("Basal (your access does not include it)");
    }

    [Fact]
    public async Task A_later_layer_with_the_same_colour_on_a_track_gets_a_series_hue()
    {
        var (runner, context, _, _) = Arrange(Registry, Scope.GlucoseRead, Scope.TreatmentsRead);
        var spec = Spec(Layers(string.Join(",",
            Layer("""{ "name": "glucose_series" }""", "line"),
            """{ "dataset": { "name": "glucose_series" }, "mark": "points", "bindings": { "y": ["glucose"] } }""",
            Layer("""{ "name": "glucose_series" }""", "line", """{ "y": ["iob"] }"""))));

        var layers = (await runner.RunAsync(spec, context, CancellationToken.None)).Panels[0].Tracks[0].Layers;

        layers.Select(l => l.Color).Should().Equal(null, ChartColor.Profile, null);
    }

    [Fact]
    public void Hues_skip_any_already_taken_on_the_track()
    {
        static ReportLayerResult Layer(string id, ChartColor? color) => new()
        {
            Id = id,
            Title = id,
            Mark = ReportMark.Line,
            Bindings = new ReportLayerBindings { X = "x", Y = ["v"] },
            XKind = ReportXKind.Time,
            Side = ReportAxisSide.Left,
            Status = ReportPanelStatus.Ok,
            Window = new ReportWindow(0, 1),
            Fields = [new("v", "V", ReportFieldKind.Number, null, "v", Color: color)],
        };

        var hued = ReportSpecRunner.Hue([Layer("a", ChartColor.Profile), Layer("b", ChartColor.Carbs), Layer("c", ChartColor.Carbs), Layer("d", ChartColor.Carbs)]);

        hued.Select(l => l.Color).Should().Equal(null, null, ChartColor.Chart3, ChartColor.Override);
    }

    [Fact]
    public void A_markers_layer_carries_its_markers_from_the_rows()
    {
        var markers = ReportSpecRunner.Markers(
            [
                new Dictionary<string, object?> { ["time"] = 1000L, ["insulin"] = 2.5, ["carbs"] = null, ["automatic"] = "yes" },
                new Dictionary<string, object?> { ["time"] = 2000L, ["insulin"] = null, ["carbs"] = 30.0 },
            ],
            new ReportLayerBindings { X = "time", Y = ["insulin", "carbs"] },
            new TreatmentsDataset().Fields);

        markers.Should().Equal(
            new ReportMarker(1000, ReportMarkerKind.Bolus, 2.5, true),
            new ReportMarker(2000, ReportMarkerKind.Carbs, 30));
    }

    [Fact]
    public async Task A_gridded_layer_is_folded_by_each_fields_aggregate()
    {
        var (runner, context, _, _) = Arrange(Registry, Scope.GlucoseRead, Scope.TreatmentsRead);
        var spec = Spec(Layers(Layer("""{ "name": "glucose_series" }""", "line"), ", \"grid\": 60", """, "range": { "hours": 3 }"""));

        var layer = (await runner.RunAsync(spec, context, CancellationToken.None)).Panels[0].Tracks[0].Layers[0];

        layer.Rows.Should().HaveCount(3);
        layer.Rows[0]["time"].Should().Be(Now.AddHours(-3).ToUnixTimeMilliseconds());
        layer.Rows[0]["glucose"].Should().Be(105.5, "the mean of the twelve readings 100 to 111 in the first hour");
        layer.Summary.Should().EndWith("Folded onto a grid of 60 minutes.");
        layer.Context.Should().Be("Glucose, mean per 1 hour");
    }

    [Fact]
    public void A_folded_layers_context_names_each_fields_aggregate_and_the_step()
    {
        ReportField Field(string label, ReportAggregate aggregate) => new(label, label, ReportFieldKind.Number, "U/h", label, Aggregate: aggregate);

        ReportSpecRunner.FoldWords([Field("Basal rate", ReportAggregate.Last)], 1440).Should().Be("Basal rate, last value per 1 day");
        ReportSpecRunner.FoldWords([Field("Bolus", ReportAggregate.Sum), Field("Peak", ReportAggregate.Max)], 15)
            .Should().Be("Bolus, total per 15 minutes; Peak, highest value per 15 minutes");
    }

    [Fact]
    public async Task The_glucose_line_leads_the_glucose_chart_whatever_the_tracks_heights()
    {
        var (runner, context, _, _) = Arrange(Registry, Scope.GlucoseRead, Scope.TreatmentsRead, Scope.DevicesRead, Scope.TherapyRead);

        var panel = (await runner.RunAsync(Spec(GlucoseChart(3)), context, CancellationToken.None)).Panels.Single();

        var layers = panel.Tracks.SelectMany(t => t.Layers).ToDictionary(l => l.Id);
        layers["glucose"].Sentence.Should().NotBeNull();
        panel.Sentence.Should().StartWith(layers["glucose"].Sentence!, "the glucose line leads, not the basal track above it");
        panel.Title.Should().Be("Glucose");
        panel.Context.Should().Contain("reading").And.NotContain("rate change", "the panel's range line is its lead layer's, not its first track's");
        panel.Facts.Should().Equal(layers["glucose"].Facts);
        layers["target"].Sentence.Should().BeNull("the range edges are not the readings the sentence is about");
    }

    private static ReportLayerResult Result(string id, ReportMark mark, string? sentence = null, bool glucose = false, bool settings = false) => new()
    {
        Id = id,
        Title = id,
        Mark = mark,
        Bindings = new ReportLayerBindings { X = "time", Y = ["v"] },
        XKind = ReportXKind.Time,
        Side = ReportAxisSide.Left,
        Status = ReportPanelStatus.Ok,
        Window = new ReportWindow(0, 1),
        Fields = [new("v", "V", ReportFieldKind.Number, null, "v", GlucoseScale: glucose)],
        Sentence = sentence,
        SettingsSentence = settings,
    };

    /// <summary>The reviewer's a1 to a4: the lead is chosen by what a layer draws, never by its track's height.</summary>
    public static TheoryData<string, string[], string> Leads => new()
    {
        { "a large basal track above the glucose line", ["basal:area:Basal changes.", "target:band", "glucose:line:Glucose sentence.:glucose"], "glucose" },
        { "a tallest on-board track with no sentence", ["iob:line", "cob:line", "basal:area:Basal changes."], "basal" },
        { "a tallest spans lane", ["spans:spans:2 spans.", "iob:line"], "iob" },
        { "a tall markers track", ["markers:markers:Boluses.", "bars:bars"], "bars" },
        { "a settings comparison never heads the panel", ["profile:line:Delivered basal was 1.06 times the scheduled basal.:settings", "iob:line:IOB."], "iob" },
    };

    [Theory]
    [MemberData(nameof(Leads))]
    public void The_lead_is_chosen_by_what_a_layer_draws(string because, string[] layers, string lead)
    {
        var results = layers.Select(spec =>
        {
            var parts = spec.Split(':');
            var mark = Enum.Parse<ReportMark>(parts[1], ignoreCase: true);
            var sentence = parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null;
            return Result(parts[0], mark, sentence, parts.Contains("glucose"), parts.Contains("settings"));
        }).ToList();

        ReportSpecRunner.Lead(results)!.Id.Should().Be(lead, because);
    }

    [Fact]
    public void A_figure_or_sentence_about_two_fields_needs_a_layer_that_draws_both()
    {
        var output = new ReportDatasetOutput
        {
            Rows = [],
            Summary = "",
            Facts = [ReportFacts.Count("Above schedule", 4).Of("delivered", "scheduled").AgainstSettings(), ReportFacts.Count("Changes", 9).Of("delivered"),
                ReportFacts.Count("Untagged", 1)],
            Sentence = "Delivered basal was 1.06 times the scheduled basal.",
            SentenceAbout = ["deliveredBasal", "scheduledBasal"],
            SentenceComparesSettings = true,
        };

        var scheduledOnly = ReportSpecRunner.About(output, ["scheduledBasal"]);
        scheduledOnly.Sentence.Should().BeNull();
        ReportSpecRunner.About(output, ["scheduled"]).Facts.Should().BeEmpty("the comparison needs the delivered basal drawn too, and an untagged figure is never a chart's");
        var both = ReportSpecRunner.About(output, ["deliveredBasal", "scheduledBasal"]);
        both.Sentence.Should().NotBeNull();
        both.SettingsSentence.Should().BeTrue();
        ReportSpecRunner.About(output, ["delivered", "scheduled"]).Facts.Select(f => f.Label).Should().Equal("Above schedule", "Changes");
        ReportAbout.DrawnBy(["glucose|glucoseMgdl"], ["glucoseMgdl"]).Should().BeTrue();
    }

    [Fact]
    public async Task A_layer_of_iob_alone_carries_no_glucose_sentence_or_facts()
    {
        var (runner, context, _, _) = Arrange(Registry, Scope.GlucoseRead, Scope.TreatmentsRead);
        var spec = Spec(Layers(Layer("""{ "name": "glucose_series" }""", "line", """{ "y": ["iob"] }"""), reportExtras: """, "range": { "hours": 3 }"""));

        var panel = (await runner.RunAsync(spec, context, CancellationToken.None)).Panels.Single();

        var layer = panel.Tracks[0].Layers.Single();
        layer.Sentence.Should().BeNull();
        layer.Facts.Should().BeEmpty();
        panel.Sentence.Should().BeNull();
        panel.Title.Should().Be("Glucose readings", "an untitled chart takes its lead layer's dataset's name");
    }

    [Fact]
    public async Task An_untitled_chart_takes_its_lead_layers_title()
    {
        var (runner, context, _, _) = Arrange(Registry, Scope.GlucoseRead);
        var spec = Spec(Layers("""{ "title": "Sensor", "dataset": { "name": "glucose_series" }, "mark": "line" }"""));

        (await runner.RunAsync(spec, context, CancellationToken.None)).Panels.Single().Title.Should().Be("Sensor");
    }

    [Fact]
    public void An_untitled_markers_layer_names_only_the_kinds_it_drew()
    {
        var bindings = new ReportLayerBindings { X = "time", Y = ["insulin", "carbs", "bgCheck"] };

        ReportSpecRunner.MarkersTitle("Treatments", new TreatmentsDataset().Fields, bindings, [new ReportMarker(1, ReportMarkerKind.BgCheck, 120)])
            .Should().Be("Treatments · BG check");
        ReportSpecRunner.MarkersTitle("Treatments", new TreatmentsDataset().Fields, bindings, []).Should().Be("Treatments");
    }

    [Fact]
    public async Task A_glucose_layer_records_the_readings_and_series_it_serves_and_no_other_part_of_the_chart()
    {
        var (runner, context, readLog, _) = Arrange(Registry, Scope.GlucoseRead, Scope.TreatmentsRead, Scope.DevicesRead, Scope.TherapyRead);
        var spec = Spec(Layers(Layer("""{ "name": "glucose_series" }""", "line"), reportExtras: """, "range": { "hours": 3 }"""));

        await runner.RunAsync(spec, context, CancellationToken.None);

        readLog.Datasets.Should().BeEquivalentTo("glucose", "treatments");
        readLog.RecordCount.Should().Be(37 * 4, "37 readings and the IOB, COB and basal point at each");
    }

    [Fact]
    public async Task A_derived_layer_reads_its_source_once_with_a_visible_layer_of_the_same_query()
    {
        var (runner, context, _, chart) = Arrange(Registry, Scope.GlucoseRead, Scope.TreatmentsRead);
        var spec = Spec(Layers(Layer("""{ "name": "glucose_series" }""", "line") + ","
            + """{ "derived": { "op": "rolling_mean", "minutes": 60, "a": { "dataset": { "name": "glucose_series" }, "field": "glucose" } } }""",
            reportExtras: """, "range": { "hours": 6 }"""));

        var panel = (await runner.RunAsync(spec, context, CancellationToken.None)).Panels[0];

        context.Cache.Reads.Should().Be(1);
        var derived = panel.Tracks[0].Layers[1];
        derived.Status.Should().Be(ReportPanelStatus.Ok);
        derived.Title.Should().Be("Rolling mean of Glucose");
        derived.Rows.Should().HaveCount(72, "six hours on the default five-minute grid");
        derived.Fields.Select(f => f.Name).Should().Equal("x", "value");
        derived.Color.Should().BeNull("the first layer is coloured by glucose, the mean in its own field's colour");
        chart.Verify(c => c.GetDashboardChartDataAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_derived_layer_whose_source_the_token_cannot_read_is_not_permitted_and_reads_nothing()
    {
        var (runner, context, readLog, chart) = Arrange(Registry, Scope.GlucoseRead);
        var spec = Spec(Layers(
            """{ "derived": { "op": "scatter", "a": { "dataset": { "name": "glucose_series" }, "field": "iob" }, "b": { "dataset": { "name": "glucose_series" }, "field": "glucose" } } }""",
            ", \"grid\": 15"));

        var panel = (await runner.RunAsync(spec, context, CancellationToken.None)).Panels[0];

        var layer = panel.Tracks[0].Layers[0];
        layer.Status.Should().Be(ReportPanelStatus.NotPermitted);
        layer.Sentence.Should().Contain("treatments permission");
        readLog.Datasets.Should().BeEmpty();
        chart.Verify(c => c.GetDashboardChartDataAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Summaries_are_joined_under_the_panel_ids()
    {
        var (runner, context, _, _) = Arrange(Registry, Scope.GlucoseRead);
        var spec = Spec("""
            { "version": 1, "title": "Two panels", "range": { "hours": 2 }, "panels": [
              { "id": "a", "title": "Glucose", "kind": "chart", "tracks": [ { "layers": [ { "dataset": { "name": "glucose_series" } } ] } ] },
              { "id": "b", "kind": "table", "dataset": { "name": "glucose_series" } } ] }
            """);

        var result = await runner.RunAsync(spec, context, CancellationToken.None);

        result.Summary.Should().StartWith("Two panels\n\n[a] Glucose\n[Glucose readings · Glucose]\nGlucose from 2026-10-29 10:00")
            .And.Contain("\n\n[b] glucose_series\nGlucose from");
        var layer = result.Panels[0].Tracks[0].Layers[0];
        layer.Fields.Select(f => f.Name).Should().Equal("time", "glucose");
        layer.Fields.Single(f => f.Name == "glucose").Unit.Should().Be("mg/dL");
        result.Panels[0].Thresholds.Should().Be(new ReportThresholds(54, 70, 180, 250, 1));
    }

    [Fact]
    public async Task A_dataset_whose_scope_the_token_lacks_is_not_read()
    {
        var slow = new FakeDataset("slow_series", Scope.HeartRateRead, TimeSpan.Zero);
        var (runner, context, readLog, _) = Arrange(new ReportDatasetRegistry([slow]), Scope.GlucoseRead);

        var result = await runner.RunAsync(FakeSpec("slow_series"), context, CancellationToken.None);

        result.Panels.Single().Status.Should().Be(ReportPanelStatus.NotPermitted);
        result.Panels.Single().Summary.Should().Contain("'heartrate.read'");
        slow.Runs.Should().Be(0);
        readLog.Datasets.Should().BeEmpty();
    }

    [Fact]
    public async Task A_report_that_runs_out_of_time_returns_its_finished_panels_and_names_the_rest()
    {
        var fast = new FakeDataset("fast", Scope.GlucoseRead, TimeSpan.Zero);
        var stuck = new FakeDataset("stuck", Scope.GlucoseRead, Timeout.InfiniteTimeSpan);
        var (_, context, _, _) = Arrange(new ReportDatasetRegistry([fast, stuck]), Scope.GlucoseRead);
        var runner = new ReportSpecRunner(new ReportDatasetRegistry([fast, stuck]), TimeProvider.System) { Timeout = TimeSpan.FromMilliseconds(200) };

        var result = await runner.RunAsync(FakeSpec("fast", "stuck", "fast"), context, CancellationToken.None);

        result.Panels.Select(p => p.Status).Should().Equal(ReportPanelStatus.Ok, ReportPanelStatus.TimedOut, ReportPanelStatus.Ok);
        result.Panels[0].Rows.Should().HaveCount(1);
        result.Warnings.Should().ContainSingle().Which.Should().StartWith("Panel p2 did not finish");
        fast.Runs.Should().Be(1, "the third panel's query is the first's, read once");
    }

    [Fact]
    public async Task A_layer_that_fails_fails_only_itself_and_the_panel_names_it()
    {
        var fine = new FakeDataset("fine", Scope.GlucoseRead, TimeSpan.Zero);
        var broken = new FakeDataset("broken", Scope.GlucoseRead, TimeSpan.Zero, fails: true);
        var registry = new ReportDatasetRegistry([fine, broken]);
        var (runner, context, _, _) = Arrange(registry, Scope.GlucoseRead);
        var spec = new ReportSpec
        {
            Panels =
            [
                new ReportPanelSpec
                {
                    Id = "p1",
                    Kind = ReportPanelKind.Chart,
                    Range = new ReportRange { Hours = 1 },
                    Tracks =
                    [
                        new ReportTrackSpec
                        {
                            Id = "t1",
                            Height = 1,
                            Layers = [FakeLayer("a", "broken"), FakeLayer("b", "fine")],
                        },
                    ],
                },
            ],
        };

        var result = await runner.RunAsync(spec, context, CancellationToken.None);

        var panel = result.Panels.Single();
        panel.Status.Should().Be(ReportPanelStatus.Ok);
        panel.Tracks[0].Layers.Select(l => l.Status).Should().Equal(ReportPanelStatus.Failed, ReportPanelStatus.Ok);
        panel.Sentence.Should().Contain("Not shown: broken · Value (an error occurred when it was read).");
        result.Warnings.Should().ContainSingle().Which.Should().StartWith("Panel p1 (layer a): an error occurred");
    }

    [Fact]
    public async Task A_dataset_that_throws_fails_only_its_own_panel()
    {
        var fine = new FakeDataset("fine", Scope.GlucoseRead, TimeSpan.Zero);
        var broken = new FakeDataset("broken", Scope.GlucoseRead, TimeSpan.Zero, fails: true);
        var (runner, context, _, _) = Arrange(new ReportDatasetRegistry([fine, broken]), Scope.GlucoseRead);

        var result = await runner.RunAsync(FakeSpec("broken", "fine"), context, CancellationToken.None);

        result.Panels.Select(p => p.Status).Should().Equal(ReportPanelStatus.Failed, ReportPanelStatus.Ok);
        result.Panels[0].Rows.Should().BeEmpty();
        result.Panels[0].Summary.Should().Be(ReportSpecRunner.FailedNote).And.NotContain("a dataset bug");
        result.Panels[1].Rows.Should().HaveCount(1);
        result.Warnings.Should().ContainSingle().Which.Should().StartWith("Panel p1: an error occurred");
    }

    [Fact]
    public async Task A_caller_that_cancels_is_not_reported_as_a_timeout()
    {
        var stuck = new FakeDataset("stuck", Scope.GlucoseRead, Timeout.InfiniteTimeSpan);
        var (runner, context, _, _) = Arrange(new ReportDatasetRegistry([stuck]), Scope.GlucoseRead);
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var run = () => runner.RunAsync(FakeSpec("stuck"), context, cancelled.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
    }

    private static ReportLayerSpec FakeLayer(string id, string dataset) => new()
    {
        Id = id,
        Dataset = new FakeParameters(dataset),
        Mark = ReportMark.Line,
        Bindings = new ReportLayerBindings { X = "time", Y = ["value"] },
        XKind = ReportXKind.Time,
        HasAxis = true,
    };

    private static ReportSpec FakeSpec(params string[] datasets) => new()
    {
        Panels = [.. datasets.Select((name, i) => new ReportPanelSpec
        {
            Id = $"p{i + 1}",
            Kind = ReportPanelKind.Table,
            Dataset = new FakeParameters(name),
            Bindings = new ReportBindings { Columns = ["value"] },
            Range = new ReportRange { Hours = 1 },
        })],
    };

    private sealed record FakeParameters(string Name) : ReportDatasetParameters
    {
        public override string DatasetName => Name;
    }

    private sealed class FakeDataset(string name, string scope, TimeSpan delay, bool fails = false) : IReportDataset
    {
        public int Runs { get; private set; }

        public string Name => name;
        public string Label => name;
        public string Description => name;
        public Type ParametersType => typeof(FakeParameters);
        public string RequiredScope => scope;
        public IReadOnlyList<ReportField> Fields { get; } =
        [
            new("time", "Time", ReportFieldKind.Time, null, "A time."),
            new("value", "Value", ReportFieldKind.Number, null, "A value."),
        ];
        public IReadOnlyList<ReportPanelKind> PanelKinds { get; } = [ReportPanelKind.Table];
        public IReadOnlyList<ReportMark> Marks { get; } = [ReportMark.Line];
        public TimeSpan DefaultRange => TimeSpan.FromHours(1);
        public TimeSpan MaxRange => TimeSpan.FromDays(1);
        public int PointBudget => 1;
        public IEnumerable<(string Field, string Message)> Validate(ReportDatasetParameters parameters) => [];

        public async Task<ReportDatasetOutput> RunAsync(ReportDatasetRequest request, CancellationToken cancellationToken)
        {
            Runs++;
            await Task.Delay(delay, cancellationToken);
            if (fails)
                throw new ArgumentOutOfRangeException(nameof(request), "a dataset bug");
            return new ReportDatasetOutput { Rows = [new Dictionary<string, object?> { ["time"] = 1L, ["value"] = 1 }], Summary = name };
        }
    }
}
