using FluentAssertions;
using Nocturne.API.Mcp.Reports;
using Nocturne.API.Mcp.Reports.Datasets;
using Nocturne.API.Mcp.Tools;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Xunit;
using static Nocturne.API.Tests.Mcp.RecordDatasetTests;

namespace Nocturne.API.Tests.Mcp;

/// <summary>The presets <c>show_agp</c> and <c>show_treatments</c>, and the text tools' wording and size.</summary>
public class PresetToolTests
{
    // ---- presets ----

    [Fact]
    public void The_agp_preset_is_a_valid_spec_laid_out_as_the_web_agp_report()
    {
        var spec = PresetTools.Normalise(Validator, PresetTools.AgpSpec(14), false);

        spec.Title.Should().Be("Ambulatory glucose profile, last 14 days");
        spec.Panels.Select(p => (p.Id, p.Dataset.DatasetName, p.Primitive)).Should().Equal(
            ("figures", "glucose_statistics", ReportPrimitive.StatTiles),
            ("profile", "glucose_distribution_by_hour", ReportPrimitive.RangeBands),
            ("ranges", "glucose_distribution", ReportPrimitive.Bar));
        spec.Panels.Should().OnlyContain(p => p.Range!.Hours == 14 * 24);
        spec.Panels[2].Bindings.Should().BeEquivalentTo(new ReportBindings { X = "band", Y = ["percent"], Stacked = false });
    }

    [Fact]
    public async Task A_glucose_only_agp_has_no_insulin_tiles_and_says_so()
    {
        var harness = new Harness();
        var (runner, context, _) = harness.Arrange(Scope.GlucoseRead);

        var result = await runner.RunAsync(PresetTools.Normalise(Validator, PresetTools.AgpSpec(14), false), context, CancellationToken.None);

        result.Panels.Should().OnlyContain(p => p.Status == ReportPanelStatus.Ok);
        var figures = result.Panels[0];
        figures.Fields.Select(f => f.Name).Should().Equal("timeInRangePercent", "mean", "gmi", "cv", "timeBelow", "timeAbove");
        figures.Omitted.Select(o => o.Field).Should().Equal("tdd", "basalPercent", "carbsPerDay");
        result.Summary.Should().Contain("[figures] Key figures").And.Contain("Fields omitted: tdd, basalPercent, carbsPerDay (scope not granted).");
        harness.VerifyNoInsulinRead();
    }

    [Fact]
    public void The_treatments_preset_is_a_valid_spec_with_markers_on_the_line()
    {
        var spec = PresetTools.Normalise(Validator, PresetTools.TreatmentsSpec(24), false);

        spec.Panels[0].Dataset.Should().BeOfType<GlucoseSeriesParameters>().Which.Markers.Should().Equal(ReportMarkerKind.Bolus, ReportMarkerKind.Carbs);
        spec.Panels[1].Dataset.Should().BeOfType<TreatmentsParameters>();
        spec.Panels[1].Bindings!.Columns.Should().Contain(["time", "type", "insulin", "carbs"]);
    }

    // ---- text tools ----

    [Fact]
    public void A_text_tool_never_returns_more_than_8_kB()
    {
        SummaryTools.Cap(new string('x', 20_000)).Length.Should().Be(SummaryTools.MaxTextLength);
        SummaryTools.Cap("short").Should().Be("short");
    }

    [Fact]
    public void The_profile_summary_writes_each_schedule_in_the_display_unit_and_stays_short()
    {
        var entries = Enumerable.Range(0, 48).Select(i => new ScheduleEntry { Time = $"{i / 2:00}:{i % 2 * 30:00}", Value = 0.85 }).ToList();
        var profile = new ActiveProfileSchedules
        {
            ProfileName = "Default", Share = 1, Percentage = 120, TimeshiftHours = 0,
            Basal = entries, CarbRatio = entries, Sensitivity = entries.Select(e => new ScheduleEntry { Time = e.Time, Value = 54 }).ToList(),
            Targets = [new TargetRangeEntry { Time = "00:00", Low = 90, High = 140 }],
        };

        var text = SummaryTools.ProfileText([profile, profile, profile, profile], useMmol: true);

        text.Should().StartWith("Profile 'Default': in force for 100% of the last 24 hours, at 120%.\n"
            + "The values below are the schedule's own, without the percentage and shift.\nBasal (U/h): 00:00 0.85, 00:30 0.85");
        text.Should().Contain("Insulin sensitivity (mmol/L/U): 00:00 3.0").And.Contain("Target range (mmol/L): 00:00 5.0-7.8.")
            .And.EndWith("1 more profiles were in force for a shorter time.");
        text.Length.Should().BeLessThan(SummaryTools.MaxTextLength);
        SummaryTools.ProfileText([], false).Should().Be("No profile was in force over the last 24 hours.");
    }

    [Fact]
    public void The_loop_health_text_writes_only_the_figures_served()
    {
        var report = new Nocturne.Core.Models.LoopAnalysis.LoopHealthResponse
        {
            LoopGapMinutes = 15,
            Primary = new Nocturne.Core.Models.LoopAnalysis.LoopHealthRange
            {
                Days = [new() { Date = "2026-10-28" }, new() { Date = "2026-10-29" }],
                Activity = new() { AidActivePercent = 92.5, PumpUsePercent = 99, CgmActivePercent = 97.2 },
                Cadence = new() { LoopGaps = new() { Count = 3, TotalMinutes = 75, LongestMinutes = 40 } },
                SuggestedNotEnacted = new() { Percent = 4 },
            },
        };

        SummaryTools.LoopHealthText(report).Should().Be(
            "Days analyzed: 2.\n"
            + "Closed loop active: 92.5% of the time. Pump in use: 99%.\n"
            + "CGM active: 97.2% of the time.\n"
            + "Gaps between loop cycles longer than 15 minutes: 3, 75 minutes in all, the longest 40 minutes.\n"
            + "Suggestions not enacted: 4%.");
    }

    [Fact]
    public void The_recent_treatments_text_stays_under_8_kB_however_many_records_it_counts()
    {
        var records = Enumerable.Range(0, 10_000).Select(i => new TreatmentsDataset.Record(
            Now.AddMinutes(-i).ToUnixTimeMilliseconds(), (ReportTreatmentType)(i % 6), Insulin: 1, Carbs: 10, Detail: new string('n', 120))).ToList();

        var text = TreatmentsDataset.Summarise(records, Now.AddDays(-14), Now, TreatmentsDataset.Kinds(new TreatmentsParameters()),
            new StatisticsService(new FixedTime(Now)), TreatmentsDataset.Budget);

        text.Length.Should().BeLessThan(1_000);
    }
}
