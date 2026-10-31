using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Mcp.Reports;
using Nocturne.API.Mcp.Reports.Datasets;
using Nocturne.API.Mcp.Tools;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Xunit;
using static Nocturne.API.Tests.Mcp.RecordDatasetTests;

namespace Nocturne.API.Tests.Mcp;

public class PeriodComparisonTests
{
    private static readonly DateOnly Today = new(2026, 10, 29);

    private static PeriodComparisonParameters Parameters(string json) =>
        (PeriodComparisonParameters)Validator.Validate(JsonDocument.Parse($$"""
            { "version": 1, "panels": [ { "dataset": { "name": "period_comparison", {{json}} }, "primitive": "comparison" } ] }
            """).RootElement).Spec!.Panels[0].Dataset;

    private static ReportSpec Spec(string json) =>
        Validator.Validate(JsonDocument.Parse(json).RootElement).Spec ?? throw new InvalidOperationException("invalid spec");

    [Theory]
    [InlineData("last7-prior7", "Prior 7 days", "2026-10-16", "2026-10-22", "Last 7 days", "2026-10-23", "2026-10-29")]
    [InlineData("last14-prior14", "Prior 14 days", "2026-10-02", "2026-10-15", "Last 14 days", "2026-10-16", "2026-10-29")]
    [InlineData("last30-prior30", "Prior 30 days", "2026-08-31", "2026-09-29", "Last 30 days", "2026-09-30", "2026-10-29")]
    [InlineData("thisMonth-lastMonth", "Last month", "2026-09-01", "2026-09-30", "This month", "2026-10-01", "2026-10-29")]
    public void A_preset_names_the_periods_the_web_report_names(
        string preset, string aLabel, string aFrom, string aTo, string bLabel, string bFrom, string bTo)
    {
        var (a, b) = PeriodComparisonDataset.Periods(Parameters($"\"preset\": \"{preset}\""), Today);

        (a.Label, a.From.ToString("yyyy-MM-dd"), a.To.ToString("yyyy-MM-dd")).Should().Be((aLabel, aFrom, aTo));
        (b.Label, b.From.ToString("yyyy-MM-dd"), b.To.ToString("yyyy-MM-dd")).Should().Be((bLabel, bFrom, bTo));
    }

    [Fact]
    public void Two_dates_each_name_their_own_periods_and_nothing_names_the_default()
    {
        var (a, b) = PeriodComparisonDataset.Periods(
            Parameters("\"a\": { \"from\": \"2026-08-01\", \"to\": \"2026-08-31\" }, \"b\": { \"from\": \"2026-10-01\", \"to\": \"2026-10-28\" }"), Today);
        (a.Label, a.From, a.To, a.Days).Should().Be(("Period A", new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31), 31));
        (b.Label, b.From, b.To).Should().Be(("Period B", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 28)));

        var (defaultA, defaultB) = PeriodComparisonDataset.Periods(Parameters("\"preset\": null"), Today);
        (defaultA.Label, defaultB.Label).Should().Be(("Prior 14 days", "Last 14 days"));
    }

    [Theory]
    [InlineData("\"preset\": \"last7-prior7\", \"a\": { \"from\": \"2026-10-01\", \"to\": \"2026-10-07\" }", "panels[0].dataset.preset")]
    [InlineData("\"a\": { \"from\": \"2026-10-01\", \"to\": \"2026-10-07\" }", "panels[0].dataset.b")]
    [InlineData("\"a\": { \"from\": \"2026-10-07\", \"to\": \"2026-10-01\" }, \"b\": { \"from\": \"2026-10-08\", \"to\": \"2026-10-09\" }", "panels[0].dataset.a.to")]
    [InlineData("\"a\": { \"from\": \"2026-01-01\", \"to\": \"2026-06-01\" }, \"b\": { \"from\": \"2026-10-08\", \"to\": \"2026-10-09\" }", "panels[0].dataset.a")]
    [InlineData("\"preset\": \"lastYear\"", "panels[0].dataset.preset")]
    [InlineData("\"a\": { \"from\": \"2026-10-32\", \"to\": \"2026-10-01\" }, \"b\": { \"from\": \"2026-10-08\", \"to\": \"2026-10-09\" }", "panels[0].dataset.a.from")]
    [InlineData("\"a\": { \"from\": \"2026-10-01\" }, \"b\": { \"from\": \"2026-10-08\", \"to\": \"2026-10-09\" }", "panels[0].dataset.a")]
    public void Malformed_periods_are_errors_naming_where(string json, string path)
    {
        var validation = Validator.Validate(JsonDocument.Parse($$"""
            { "version": 1, "panels": [ { "dataset": { "name": "period_comparison", {{json}} }, "primitive": "comparison" } ] }
            """).RootElement);

        validation.IsValid.Should().BeFalse();
        validation.Errors.Should().Contain(e => e.Path == path);
        validation.Errors.Should().OnlyContain(e => !e.Message.Contains("System.") && !e.Message.Contains("Nocturne."));
    }

    private static PeriodComparisonDataset.Side Side(double target, double mean, double? cv, InsulinDeliveryStatistics? insulin = null, int count = 2016) => new(
        new ExtendedGlucoseAnalytics
        {
            BasicStats = new BasicGlucoseStats { Count = count, Mean = mean, Median = mean - 5 },
            TimeInRange = new TimeInRangeMetrics
            {
                Percentages = new TimeInRangePercentages { VeryLow = 1, Low = 3, Target = target, High = 96 - target - 5, VeryHigh = 5 },
            },
            GlycemicVariability = cv is { } c ? new GlycemicVariability { CoefficientOfVariation = c, StandardDeviation = mean * c / 100 } : null,
            GMI = new GlucoseManagementIndicator { Value = 6.9 },
        },
        insulin);

    [Fact]
    public void Each_figure_is_a_row_with_both_values_the_change_and_a_verdict_only_where_one_way_is_better()
    {
        var rows = PeriodComparisonDataset.Rows(Side(60, 160, 38), Side(70, 150, 34), treatments: false, useMmol: false)
            .ToDictionary(r => (string)r["metric"]!);

        rows["inRange"].Should().Contain(new Dictionary<string, object?>
        {
            ["group"] = "Time in each range", ["a"] = 60.0, ["b"] = 70.0, ["delta"] = 10.0, ["changePercent"] = 16.7, ["unit"] = "%",
            ["verdict"] = "better", ["color"] = ChartColor.GlucoseInRange,
        });
        rows["high"]["verdict"].Should().Be("better", "less time high is better");
        rows["cv"]["verdict"].Should().Be("better");
        rows["gmi"]["verdict"].Should().BeNull("nothing changed");
        rows["mean"]["verdict"].Should().BeNull("neither a lower nor a higher mean is better in itself");
        rows["mean"]["delta"].Should().Be(-10.0);
        rows["mean"]["unit"].Should().Be("mg/dL");
        rows.Keys.Should().NotContain(["insulinPerDay", "basalPerDay", "basalPercent", "bolusesPerDay", "carbsPerDay", "icr"]);
        rows.Values.Where(r => r["color"] is not null).Select(r => r["metric"]).Should().Equal("veryLow", "low", "inRange", "high", "veryHigh");
    }

    [Fact]
    public void Glucose_figures_are_in_the_display_unit_and_a_side_without_readings_has_no_figures()
    {
        var rows = PeriodComparisonDataset.Rows(Side(60, 180, 38, count: 0), Side(70, 180, 34), treatments: true, useMmol: true)
            .ToDictionary(r => (string)r["metric"]!);

        rows["mean"]["b"].Should().Be(10.0);
        rows["mean"]["unit"].Should().Be("mmol/L");
        rows["mean"]["a"].Should().BeNull();
        rows["mean"]["delta"].Should().BeNull();
        rows["mean"]["verdict"].Should().BeNull();
        rows["insulinPerDay"]["a"].Should().BeNull("no insulin record was read on either side");
    }

    [Fact]
    public void Insulin_rows_need_treatments_and_count_only_a_period_with_insulin_records()
    {
        var insulin = new InsulinDeliveryStatistics { InsulinEventCount = 30, Tdd = 42.04, TotalBasalPerDay = 20, BasalPercent = 47.6, CarbCount = 0 };
        var rows = PeriodComparisonDataset.Rows(Side(60, 160, 38, insulin), Side(70, 150, 34, insulin), treatments: true, useMmol: false)
            .ToDictionary(r => (string)r["metric"]!);

        rows["insulinPerDay"]["a"].Should().Be(42.0);
        rows["insulinPerDay"]["verdict"].Should().BeNull();
        rows["carbsPerDay"]["a"].Should().BeNull("a period with no carb entries has nothing to average");
    }

    [Fact]
    public async Task Without_treatments_read_no_insulin_record_is_read_and_the_summary_says_so()
    {
        var harness = new Harness();
        var (runner, context, readLog) = harness.Arrange(Scope.GlucoseRead);

        var panel = (await runner.RunAsync(Spec("""
            { "version": 1, "panels": [ { "dataset": { "name": "period_comparison", "preset": "last7-prior7" }, "primitive": "comparison" } ] }
            """), context, CancellationToken.None)).Panels.Single();

        harness.VerifyNoInsulinRead();
        panel.Status.Should().Be(ReportPanelStatus.Ok);
        panel.Rows.Select(r => r["metric"]).Should().NotContain("insulinPerDay");
        panel.Summary.Should().Contain("Insulin and carb figures are not shown: they need the treatments.read scope, which this token does not hold.");
        panel.Summary.Should().StartWith(
            "Period comparison: A is Prior 7 days, 2026-10-16 to 2026-10-22 (7 days); B is Last 7 days, 2026-10-23 to 2026-10-29 (7 days), in the tenant's local days.");
        panel.Fields.Single(f => f.Name == "a").Label.Should().Be("Prior 7 days");
        panel.Fields.Single(f => f.Name == "b").Label.Should().Be("Last 7 days");
        panel.Context.Should().Be("Prior 7 days: 2026-10-16 to 2026-10-22. Last 7 days: 2026-10-23 to 2026-10-29. Local days.");
        panel.Window.Should().Be(new ReportWindow(
            new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()));
        readLog.Datasets.Should().Equal("glucose");
        panel.LinkQuery!.Select(p => $"{p.Key}={p.Value}").Should().Equal("preset=last7-prior7");
    }

    [Fact]
    public async Task With_treatments_read_the_insulin_rows_are_read_for_each_period()
    {
        var harness = new Harness();
        harness.WithBoluses((Now.AddDays(-2), 4), (Now.AddDays(-9), 5));
        var (runner, context, readLog) = harness.Arrange(Scope.GlucoseRead, Scope.TreatmentsRead);

        var panel = (await runner.RunAsync(Spec("""
            { "version": 1, "panels": [ { "dataset": { "name": "period_comparison", "preset": "last7-prior7" }, "primitive": "comparison" } ] }
            """), context, CancellationToken.None)).Panels.Single();

        panel.Rows.Select(r => r["metric"]).Should().Contain("insulinPerDay");
        panel.Summary.Should().NotContain("not shown");
        readLog.Datasets.Should().Equal("glucose", "treatments");
    }

    [Fact]
    public async Task A_clamped_caller_gets_no_comparison_and_the_reason()
    {
        var (runner, context, _) = new Harness().ArrangeClamped(Scope.GlucoseRead);

        var panel = (await runner.RunAsync(Spec("""
            { "version": 1, "panels": [ { "dataset": { "name": "period_comparison" }, "primitive": "comparison" } ] }
            """), context, CancellationToken.None)).Panels.Single();

        panel.Rows.Should().BeEmpty();
        panel.Summary.Should().Contain("Not compared: this token reads only the last 24 hours");
    }

    [Fact]
    public void Compare_periods_builds_a_valid_spec_from_a_preset_or_the_four_dates()
    {
        PresetTools.Normalise(Validator, PresetTools.ComparisonSpec(null, null, null, null, null), false)
            .Panels.Single().Dataset.Should().BeOfType<PeriodComparisonParameters>().Which.Preset.Should().Be(ReportComparisonPreset.Last14Prior14);
        var dated = PresetTools.Normalise(Validator, PresetTools.ComparisonSpec(null, "2026-09-01", "2026-09-14", "2026-10-01", "2026-10-14"), false)
            .Panels.Single();
        dated.Primitive.Should().Be(ReportPrimitive.Comparison);
        ((PeriodComparisonParameters)dated.Dataset).B.Should().Be(new ReportDayPeriod { From = new DateOnly(2026, 10, 1), To = new DateOnly(2026, 10, 14) });
        Validator.Validate(JsonSerializer.SerializeToElement(PresetTools.ComparisonSpec(null, "2026-09-01", "2026-09-14", null, null)))
            .Errors.Should().Contain(e => e.Path == "panels[0].dataset.b");
    }
}
