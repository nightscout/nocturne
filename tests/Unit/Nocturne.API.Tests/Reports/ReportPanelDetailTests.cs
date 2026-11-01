using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Authorization;
using Nocturne.API.Reports;
using Nocturne.API.Reports;
using Nocturne.API.Reports.Datasets;
using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.LoopAnalysis;
using Xunit;
using static Nocturne.API.Tests.Reports.RecordDatasetTests;

namespace Nocturne.API.Tests.Reports;

/// <summary>What a token without a scope is told, and the edges the Phase 6 review found.</summary>
public class Phase6ReviewTests
{
    private static ReportSpec Spec(string json) =>
        Validator.Validate(JsonDocument.Parse(json).RootElement).Spec ?? throw new InvalidOperationException("invalid spec");

    private static readonly DateTimeOffset From = new(2026, 10, 29, 6, 0, 0, TimeSpan.Zero);

    private static DashboardChartData Chart() => new()
    {
        GlucoseData =
        [
            new GlucosePointDto { Time = From.ToUnixTimeMilliseconds(), Sgv = 100 },
            new GlucosePointDto { Time = From.AddMinutes(5).ToUnixTimeMilliseconds(), Sgv = 120 },
        ],
        Thresholds = new ChartThresholdsDto { Low = 70, High = 180, VeryLow = 54, VeryHigh = 250, GlucoseYMax = 300 },
    };

    [Fact]
    public void A_glucose_sentence_states_the_episodes()
    {
        var (facts, sentence, context) = GlucoseSummary.Facts(Chart(), new StatisticsService(TimeProvider.System), false, From, From.AddHours(1));

        sentence.Should().Be("0 episodes below range and 0 above it.");
        facts.Select(f => f.Label).Should().Contain("In range");
        context.Should().EndWith("2 readings.");
    }

    [Fact]
    public void Daily_profile_facts_without_treatments_read_carry_no_insulin_figure()
    {
        var response = new DailyProfileResponse
        {
            Primary = new DailyProfileRange
            {
                Slots = [new DailyProfileSlot { MinuteOfDay = 480, Glucose = new LoopAnalysisAggregate { P10 = 90, P50 = 130, P90 = 190, N = 12 } }],
                Summary = new DailyProfileSummary
                {
                    DaysWithData = 14,
                    Insulin = new DailyInsulinSummary { Total = new LoopAnalysisAggregate { P50 = 40, N = 14 } },
                    NetLoopCorrectionPerDay = 3.2,
                    DeliveredToScheduledRatio = 1.2,
                },
            },
        };

        var redacted = LoopAnalysisReadScopeGuard.Redact(response, new HashSet<string> { Scope.GlucoseRead });
        var facts = DailyProfileDataset.FactsOf(redacted, useMmol: false);

        facts.Select(f => f.Label).Should().NotContain(["Insulin per day", "Net loop correction"]);
        facts.Select(f => f.Label).Should().Contain("Days with data");
        DailyProfileDataset.SentenceOf(redacted).Should().NotContain("basal");
    }

    [Fact]
    public void The_comparison_window_spans_both_periods_whichever_comes_first()
    {
        var a = new PeriodComparisonDataset.Period("Period A", new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 26));
        var b = new PeriodComparisonDataset.Period("Period B", new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 7));

        var window = PeriodComparisonDataset.WindowOf(a, b, TimeZoneInfo.Utc);

        window.Should().Be(new ReportWindow(
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            new DateTimeOffset(2026, 10, 27, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()));
        (window.To > window.From).Should().BeTrue();
    }

    [Fact]
    public void A_preset_comparison_links_by_the_preset_name_only_and_named_dates_carry_their_labels()
    {
        var preset = new PeriodComparisonParameters { Preset = ReportComparisonPreset.Last30Prior30 };
        var (a, b) = PeriodComparisonDataset.Periods(preset, new DateOnly(2026, 10, 29));
        PeriodComparisonDataset.LinkQueryOf(preset, a, b).Select(p => $"{p.Key}={p.Value}").Should().Equal("preset=last30-prior30");

        var dated = new PeriodComparisonParameters
        {
            A = new ReportDayPeriod { From = new DateOnly(2026, 9, 1), To = new DateOnly(2026, 9, 7) },
            B = new ReportDayPeriod { From = new DateOnly(2026, 10, 1), To = new DateOnly(2026, 10, 7) },
        };
        var (da, db) = PeriodComparisonDataset.Periods(dated, new DateOnly(2026, 10, 29));
        PeriodComparisonDataset.LinkQueryOf(dated, da, db).Select(p => p.Key)
            .Should().Equal("preset", "aFrom", "aTo", "aLabel", "bFrom", "bTo", "bLabel");
    }

    [Fact]
    public void A_report_holds_at_most_two_comparison_panels()
    {
        var validation = Validator.Validate(JsonDocument.Parse("""
            { "version": 1, "panels": [
              { "dataset": { "name": "period_comparison" }, "kind": "comparison" },
              { "dataset": { "name": "period_comparison", "preset": "last7-prior7" }, "kind": "comparison" },
              { "dataset": { "name": "period_comparison", "preset": "last30-prior30" }, "kind": "table" } ] }
            """).RootElement);

        validation.Errors.Should().ContainSingle(e => e.Path == "panels")
            .Which.Message.Should().Contain("reads Period comparison at most 2 times; this one reads it 3 times");
    }

    [Fact]
    public void Each_comparison_row_says_which_way_it_moved()
    {
        var rows = PeriodComparisonDataset.Rows(Side(60, 160), Side(70, 160), treatments: false, useMmol: false)
            .ToDictionary(r => (string)r["metric"]!);

        rows["inRange"]["direction"].Should().Be("up");
        rows["mean"]["direction"].Should().Be("flat");
        rows["high"]["direction"].Should().Be("down");
    }

    private static PeriodComparisonDataset.Side Side(double target, double mean) => new(
        new ExtendedGlucoseAnalytics
        {
            BasicStats = new BasicGlucoseStats { Count = 100, Mean = mean, Median = mean },
            TimeInRange = new TimeInRangeMetrics
            {
                Percentages = new TimeInRangePercentages { VeryLow = 1, Low = 3, Target = target, High = 91 - target, VeryHigh = 5 },
            },
        },
        null);

    [Theory]
    [InlineData("""{ "y": ["glucoseP50"], "edges": ["glucoseP10", "glucoseP90"], "series": "sentence" }""", "panels[0].tracks[0].layers[0].bindings.series")]
    [InlineData("""{ "x": "cohort", "y": ["glucoseP50"], "edges": ["glucoseP10", "glucoseP90"] }""", "panels[0].tracks[0].layers[0].bindings.x")]
    [InlineData("""{ "y": ["glucoseP50"], "edges": ["glucoseP10", "range"] }""", "panels[0].tracks[0].layers[0].bindings.edges")]
    public void A_companion_field_cannot_be_bound(string bindings, string path)
    {
        var validation = Validator.Validate(JsonDocument.Parse(ReportSpecValidatorTests.Layers(
            $$"""{ "dataset": { "name": "event_response" }, "mark": "band", "bindings": {{bindings}} }""")).RootElement);

        validation.Errors.Should().Contain(e => e.Path == path && e.Message.Contains("comes with every row"));
    }

    [Fact]
    public void The_api_names_each_band_span_and_a_mixed_band_keeps_both_names()
    {
        var fields = new EventResponseDataset().FieldsFor(ReportPanelKind.Chart);
        ReportField F(string name) => fields.Single(f => f.Name == name);

        ReportBandSpan.Of([F("glucoseP10"), F("glucoseP25"), F("glucoseP75"), F("glucoseP90")]).Select(s => s.Label)
            .Should().Equal("Glucose P25–P75", "Glucose P10–P90");
        ReportBandSpan.Of([F("deltaP25"), F("glucoseP25"), F("glucoseP75"), F("deltaP75")]).Select(s => s.Label)
            .Should().Equal("Glucose P25–P75", "Change P25–P75");
    }
}
