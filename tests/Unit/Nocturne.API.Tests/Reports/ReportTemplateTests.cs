using FluentAssertions;
using Moq;
using Nocturne.API.Reports;
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

        templates.Select(t => t.Template.Id).Should().Equal("glucose", "agp", "treatments", "comparison");
        templates.Should().OnlyContain(t => t.Spec.Panels.Count > 0 && t.Template.Name.Length > 0 && t.Template.Description.Length > 0);
        ReportTemplates.Normalised(Validator, useMmol: true).Should().HaveCount(4, "a tenant in mmol/L gets the same templates");
    }

    [Fact]
    public void The_glucose_template_is_the_dashboard_chart_of_the_last_six_hours()
    {
        var spec = ReportTemplates.Normalise(Validator, ReportTemplates.GlucoseSpec(6), false);

        spec.Panels.Should().ContainSingle().Which.Primitive.Should().Be(ReportPrimitive.GlucoseChart);
        spec.Panels[0].Range.Should().Be(new ReportRange { Hours = 6 });
    }

    [Fact]
    public void The_agp_template_is_laid_out_as_the_web_agp_report()
    {
        var spec = ReportTemplates.Normalise(Validator, ReportTemplates.AgpSpec(14), false);

        spec.Title.Should().Be("Ambulatory glucose profile, last 14 days");
        spec.Panels.Select(p => (p.Id, p.Dataset.DatasetName, p.Primitive)).Should().Equal(
            ("figures", "glucose_statistics", ReportPrimitive.StatTiles),
            ("profile", "glucose_distribution_by_hour", ReportPrimitive.RangeBands),
            ("ranges", "glucose_distribution", ReportPrimitive.Bar));
        spec.Panels.Should().OnlyContain(p => p.Range!.Hours == 14 * 24);
        spec.Panels[2].Bindings.Should().BeEquivalentTo(new ReportBindings
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

        await runner.RunAsync(ReportTemplates.Normalise(Validator, ReportTemplates.TreatmentsSpec(24), false), context, CancellationToken.None);

        readLog.Datasets.Should().Equal("glucose", "treatments");
        readLog.RecordCount.Should().Be(2, "one reading and one bolus");
    }

    [Fact]
    public void The_treatments_template_draws_the_markers_on_the_line()
    {
        var spec = ReportTemplates.Normalise(Validator, ReportTemplates.TreatmentsSpec(24), false);

        spec.Panels[0].Dataset.Should().BeOfType<GlucoseSeriesParameters>().Which.Markers.Should().Equal(ReportMarkerKind.Bolus, ReportMarkerKind.Carbs);
        spec.Panels[1].Dataset.Should().BeOfType<TreatmentsParameters>();
        spec.Panels[1].Bindings!.Columns.Should().Contain(["time", "type", "insulin", "carbs"]);
    }

    [Fact]
    public void The_comparison_template_compares_the_last_14_days_with_the_14_before()
    {
        var panel = ReportTemplates.Normalise(Validator, ReportTemplates.ComparisonSpec(), false).Panels.Single();

        panel.Primitive.Should().Be(ReportPrimitive.Comparison);
        panel.Dataset.Should().BeOfType<PeriodComparisonParameters>().Which.Preset.Should().Be(ReportComparisonPreset.Last14Prior14);
    }
}
