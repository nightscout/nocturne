using FluentAssertions;
using Moq;
using Nocturne.API.Reports;
using Nocturne.API.Reports.Derived;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Xunit;
using static Nocturne.API.Tests.Reports.RecordDatasetTests;

namespace Nocturne.API.Tests.Reports;

/// <summary>The specs the report builder offers as a start.</summary>
public class ReportTemplateTests
{
    [Fact]
    public void Every_template_is_a_valid_spec_with_its_own_id()
    {
        var templates = ReportTemplates.Normalised(Validator, useMmol: false);

        templates.Select(t => t.Template.Id).Should().Equal("glucose", "agp", "treatments", "comparison", "weekdays", "insulin-glucose");
        templates.Should().OnlyContain(t => t.Spec.Panels.Count > 0 && t.Template.Name.Length > 0 && t.Template.Description.Length > 0);
        ReportTemplates.Normalised(Validator, useMmol: true).Should().HaveCount(6, "a tenant in mmol/L gets the same templates");
    }

    [Fact]
    public void The_glucose_template_is_the_dashboard_chart_as_four_tracks()
    {
        var spec = ReportTemplates.Normalise(Validator, ReportTemplates.GlucoseSpec(6), false);

        var panel = spec.Panels.Should().ContainSingle().Subject;
        panel.Kind.Should().Be(ReportPanelKind.Chart);
        panel.Range.Should().Be(new ReportRange { Hours = 6 });
        panel.Tracks!.Select(t => t.Id).Should().Equal("basal", "glucose", "onboard", "states");
        panel.Tracks!.Select(t => t.Height).Should().Equal(0.5, 2, 0.6, ReportTrackSpec.LaneHeight);
        panel.Tracks![1].Layers.Select(l => l.Mark).Should().Equal(ReportMark.Band, ReportMark.Line, ReportMark.Markers);
        panel.Tracks![2].Layers.Select(l => l.Unit).Should().Equal("U", "g");
        panel.Tracks![3].Layers.Single().Mark.Should().Be(ReportMark.Spans);
    }

    [Fact]
    public void The_agp_template_is_laid_out_as_the_web_agp_report()
    {
        var spec = ReportTemplates.Normalise(Validator, ReportTemplates.AgpSpec(14), false);

        spec.Title.Should().Be("Ambulatory glucose profile, last 14 days");
        spec.Panels.Select(p => (p.Id, p.Kind)).Should().Equal(
            ("figures", ReportPanelKind.StatTiles), ("profile", ReportPanelKind.Chart), ("ranges", ReportPanelKind.Chart));
        spec.Panels[0].Dataset.Should().BeOfType<GlucoseStatisticsParameters>();
        spec.Panels[1].Tracks![0].Layers[0].Should().Match<ReportLayerSpec>(l => l.Mark == ReportMark.Band && l.XKind == ReportXKind.TimeOfDay);
        spec.Panels.Should().OnlyContain(p => p.Range!.Hours == 14 * 24);
        spec.Panels[2].Tracks![0].Layers[0].Bindings.Should().BeEquivalentTo(new ReportLayerBindings
        {
            X = "period", Y = ["veryLow", "low", "inRange", "high", "veryHigh"], Stacked = true, Orientation = ReportBarOrientation.Horizontal,
        }, "the web AGP report draws time in range as one horizontal stacked bar");
    }

    [Fact]
    public async Task A_glucose_only_agp_has_no_insulin_tiles_and_says_so()
    {
        var harness = new Harness();
        var (runner, context, readLog) = harness.Arrange(Scope.GlucoseRead);

        var result = await runner.RunAsync(ReportTemplates.Normalise(Validator, ReportTemplates.AgpSpec(14), false), context, CancellationToken.None);

        result.Panels.Should().OnlyContain(p => p.Status == ReportPanelStatus.Ok);
        var figures = result.Panels[0];
        figures.Fields.Select(f => f.Name).Should().Equal("timeInRangePercent", "mean", "gmi", "cv", "timeBelow", "timeAbove");
        figures.Omitted.Select(o => o.Field).Should().Equal("tdd", "basalPercent", "carbsPerDay");
        result.Summary.Should().Contain("[figures] Key figures").And.Contain("Fields omitted: tdd, basalPercent, carbsPerDay (scope not granted).");
        harness.VerifyNoInsulinRead();
        harness.GlucoseReads.Should().Be(1, "the three panels share one read of the same range");
        readLog.Datasets.Should().Equal("glucose");
        readLog.RecordCount.Should().Be(14 * 288);
    }

    [Fact]
    public async Task The_treatments_template_audits_the_glucose_and_the_treatments_it_read()
    {
        var harness = new Harness();
        harness.WithBoluses((Now.AddHours(-2), 4.5));
        var (runner, context, readLog) = harness.Arrange(Scope.GlucoseRead, Scope.TreatmentsRead);
        harness.Chart.Setup(c => c.GetDashboardChartDataAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DashboardChartData { GlucoseData = [new GlucosePointDto { Time = Now.AddHours(-1).ToUnixTimeMilliseconds(), Sgv = 120 }] });

        var result = await runner.RunAsync(ReportTemplates.Normalise(Validator, ReportTemplates.TreatmentsSpec(24), false), context, CancellationToken.None);

        readLog.Datasets.Should().Equal("glucose", "treatments");
        result.Panels[0].Tracks[0].Layers[1].Markers.Should().ContainSingle().Which.Should().Be(
            new ReportMarker(Now.AddHours(-2).ToUnixTimeMilliseconds(), ReportMarkerKind.Bolus, 4.5));
    }

    [Fact]
    public void The_treatments_template_draws_the_markers_on_the_line()
    {
        var spec = ReportTemplates.Normalise(Validator, ReportTemplates.TreatmentsSpec(24), false);

        var layers = spec.Panels[0].Tracks!.Single().Layers;
        layers.Select(l => l.Mark).Should().Equal(ReportMark.Line, ReportMark.Markers);
        layers[1].Bindings!.Y.Should().Equal("insulin", "carbs");
        spec.Panels[1].Dataset.Should().BeOfType<TreatmentsParameters>();
        spec.Panels[1].Bindings!.Columns.Should().Contain(["time", "type", "insulin", "carbs"]);
    }

    [Fact]
    public void The_comparison_template_compares_the_last_14_days_with_the_14_before()
    {
        var panel = ReportTemplates.Normalise(Validator, ReportTemplates.ComparisonSpec(), false).Panels.Single();

        panel.Kind.Should().Be(ReportPanelKind.Comparison);
        panel.Dataset.Should().BeOfType<PeriodComparisonParameters>().Which.Preset.Should().Be(ReportComparisonPreset.Last14Prior14);
    }

    [Fact]
    public void The_weekday_template_is_two_daily_profile_layers_over_the_day()
    {
        var layers = ReportTemplates.Normalise(Validator, ReportTemplates.WeekdaySpec(28), false).Panels.Single().Tracks!.Single().Layers;

        layers.Should().HaveCount(2).And.OnlyContain(l => l.XKind == ReportXKind.TimeOfDay && l.Mark == ReportMark.Band);
        layers.Select(l => ((DailyProfileParameters)l.Dataset!).DayOfWeek!.Count).Should().Equal(5, 2);
    }

    [Fact]
    public void The_insulin_and_glucose_template_is_a_scatter_and_a_lag_correlation_of_one_read()
    {
        var spec = ReportTemplates.Normalise(Validator, ReportTemplates.InsulinGlucoseSpec(14), false);

        spec.Panels.Select(p => p.Tracks![0].Layers[0].Derived!.OpName).Should().Equal(ScatterSpec.Name, LagCorrelationSpec.Name);
        spec.Panels.Select(p => p.Tracks![0].Layers[0].XKind).Should().Equal(ReportXKind.Value, ReportXKind.Lag);
        spec.Panels.Should().OnlyContain(p => p.Grid == 15);
    }
}
