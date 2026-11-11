using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Reports;
using Nocturne.API.Reports.Datasets;
using Nocturne.Core.Models;
using Nocturne.Core.Models.LoopAnalysis;
using Xunit;

namespace Nocturne.API.Tests.Reports;

/// <summary>The event groups a custom report adds: all others, the starts of lows and highs, and the conditions on the state at the event.</summary>
public partial class LoopAnalysisDatasetTests
{
    private static IReadOnlyList<ReportSpecError> CohortErrors(string cohorts, bool useMmol = false, int minutesBefore = 60) =>
        Validator.Validate(JsonDocument.Parse($$"""
            { "version": 1, "panels": [ { "kind": "table", "dataset": { "name": "event_response", "minutesBefore": {{minutesBefore}}, "cohorts": {{cohorts}} } } ] }
            """).RootElement, useMmol).Errors;

    [Fact]
    public void A_group_of_all_others_with_a_group_of_its_kind_validates()
    {
        CohortErrors($$"""[ { "anchor": "Meal", "foods": ["{{Pasta}}"] }, { "anchor": "Meal", "others": true } ]""").Should().BeEmpty();
    }

    [Theory]
    [InlineData("""[ { "anchor": "Meal", "others": true } ]""", "cohorts[0].others", "needs another group of the same event")]
    [InlineData("""[ { "anchor": "ExerciseStart" }, { "anchor": "Meal", "others": true } ]""", "cohorts[1].others", "needs another group of the same event")]
    [InlineData("""[ { "anchor": "Meal", "grams": { "min": 40 } }, { "anchor": "Meal", "others": true, "dayOfWeek": ["Mon"] } ]""", "cohorts[1].others", "takes no filter or condition")]
    [InlineData("""[ { "anchor": "Meal", "grams": { "min": 40 } }, { "anchor": "Meal", "others": true, "startGlucose": { "min": 100 } } ]""", "cohorts[1].others", "takes no filter or condition")]
    [InlineData("""[ { "anchor": "Meal" }, { "anchor": "Meal", "others": true }, { "anchor": "Meal", "others": true } ]""", "cohorts", "Only one group can be all others")]
    public void A_group_of_all_others_is_refused_where_it_could_not_be_the_rest_of_one_group(string cohorts, string path, string message)
    {
        CohortErrors(cohorts).Should().Contain(e => e.Path == $"panels[0].dataset.{path}" && e.Message.Contains(message));
    }

    [Theory]
    [InlineData("""{ "anchor": "ExerciseStart", "minutesSinceMeal": { "min": 0 } }""", "minutesSinceMeal", "Send minutes from 1 to 1440.")]
    [InlineData("""{ "anchor": "ExerciseStart", "minutesSinceMeal": { "max": 1441 } }""", "minutesSinceMeal", "Send minutes from 1 to 1440.")]
    [InlineData("""{ "anchor": "ExerciseStart", "minutesSinceMeal": { "min": 240, "max": 60 } }""", "minutesSinceMeal", "Send a lowest that is not above the highest.")]
    [InlineData("""{ "anchor": "ExerciseStart", "startGlucose": { "max": 601 } }""", "startGlucose", "Send glucose in mg/dL, from 0 to 600.")]
    [InlineData("""{ "anchor": "ExerciseStart", "startGlucose": { "min": 180, "max": 70 } }""", "startGlucose", "Send a lowest that is not above the highest.")]
    [InlineData("""{ "anchor": "ExerciseStart", "insulinOnBoard": { "min": -21 } }""", "insulinOnBoard", "Send units from -20 to 100.")]
    [InlineData("""{ "anchor": "ExerciseStart", "insulinOnBoard": { "min": 2, "max": 1 } }""", "insulinOnBoard", "Send a lowest that is not above the highest.")]
    public void A_condition_out_of_its_limits_names_them(string cohort, string field, string message)
    {
        CohortErrors($"[ {cohort} ]").Should().ContainSingle().Which.Should().Be(new ReportSpecError($"panels[0].dataset.cohorts[0].{field}", message));
    }

    [Fact]
    public void A_start_glucose_is_whole_mg_dl_whatever_the_tenants_unit()
    {
        const string cohorts = """[ { "anchor": "LowStart", "startGlucose": { "min": 70, "max": 180 } } ]""";

        CohortErrors(cohorts, useMmol: false).Should().BeEmpty();
        CohortErrors(cohorts, useMmol: true).Should().BeEmpty();
        CohortErrors("""[ { "anchor": "LowStart", "startGlucose": { "min": 3.9 } } ]""", useMmol: true).Should().ContainSingle()
            .Which.Path.Should().Be("panels[0].dataset.cohorts[0].startGlucose.min");
    }

    [Fact]
    public void The_window_may_look_four_hours_back_and_no_further()
    {
        CohortErrors("""[ { "anchor": "LowStart" } ]""", minutesBefore: 240).Should().BeEmpty();
        CohortErrors("""[ { "anchor": "LowStart" } ]""", minutesBefore: 180).Should().BeEmpty();
        CohortErrors("""[ { "anchor": "LowStart" } ]""", minutesBefore: 300).Should().ContainSingle()
            .Which.Message.Should().Be("Send one of: 0, 30, 60, 90, 120, 180, 240.");
    }

    [Fact]
    public void An_unknown_anchor_lists_the_starts_of_lows_and_highs()
    {
        CohortErrors("""[ { "anchor": "Low" } ]""").Should().ContainSingle().Which.Message.Should().Contain("LowStart").And.Contain("HighStart");
    }

    [Fact]
    public void All_others_and_the_conditions_reach_the_definition_in_mg_dl()
    {
        var parameters = new EventResponseParameters
        {
            Cohorts =
            [
                new ReportEventCohort
                {
                    Anchor = EventAnchorKind.ExerciseStart,
                    MinutesSinceMeal = new ReportMinutesSinceMeal { Min = 240 },
                    StartGlucose = new ReportStartGlucose { Min = 70, Max = 180 },
                    InsulinOnBoard = new ReportInsulinOnBoard { Max = 0.5 },
                },
                new ReportEventCohort { Anchor = EventAnchorKind.ExerciseStart, Others = true },
            ],
        };

        var definition = parameters.ToDefinition(parameters.AppliedCohorts);

        definition.Cohorts[0].Should().BeEquivalentTo(new
        {
            Cohort = 1, Others = false, MinMinutesSinceMeal = 240, MaxMinutesSinceMeal = (int?)null,
            MinStartGlucose = 70.0, MaxStartGlucose = 180.0, MinIob = (double?)null, MaxIob = 0.5,
        });
        definition.Cohorts[1].Should().BeEquivalentTo(new { Cohort = 2, Others = true, MinStartGlucose = (double?)null });
    }

    [Fact]
    public void A_group_label_says_its_conditions_in_words()
    {
        string Label(EventCohortDefinition cohort, bool useMmol = false) =>
            EventResponseDataset.CohortLabel(cohort, new Dictionary<Guid, string>(), useMmol: useMmol);

        Label(new EventCohortDefinition { Anchor = EventAnchorKind.ExerciseStart, MinMinutesSinceMeal = 240 })
            .Should().Be("Exercise start: no carbs in the 4 hours before");
        Label(new EventCohortDefinition { Anchor = EventAnchorKind.ExerciseStart, MaxMinutesSinceMeal = 60 })
            .Should().Be("Exercise start: carbs in the hour before");
        Label(new EventCohortDefinition { Anchor = EventAnchorKind.Meal, MinMinutesSinceMeal = 90, MaxMinutesSinceMeal = 240 })
            .Should().Be("Meal: last carbs 90 minutes to 4 hours before");
        Label(new EventCohortDefinition { Anchor = EventAnchorKind.LowStart, MinStartGlucose = 70, MaxStartGlucose = 180 }, useMmol: true)
            .Should().Be("Low start: glucose at the event 3.9 to 10.0 mmol/L");
        Label(new EventCohortDefinition { Anchor = EventAnchorKind.HighStart, MinStartGlucose = 250 })
            .Should().Be("High start: glucose at the event at least 250 mg/dL");
        Label(new EventCohortDefinition { Anchor = EventAnchorKind.CorrectionBolus, MaxIob = 0.5, MinMinutesSinceMeal = 180 })
            .Should().Be("Correction bolus: no carbs in the 3 hours before, IOB at most 0.5 U");
        Label(new EventCohortDefinition { Anchor = EventAnchorKind.Meal, Others = true }).Should().Be("Meal: all others");
        Label(new EventCohortDefinition { Anchor = EventAnchorKind.LowStart }).Should().Be("Low start: all");
    }

    [Fact]
    public void The_summary_says_what_all_others_holds_and_counts_events_with_no_iob_apart()
    {
        var report = new EventResponseReport
        {
            PrimaryRange = new LoopAnalysisResolvedRange { StartDate = new DateOnly(2026, 10, 1), EndDate = new DateOnly(2026, 10, 28) },
            Definition = new EventResponseDefinition
            {
                Cohorts =
                [
                    new EventCohortDefinition { Cohort = 1, Anchor = EventAnchorKind.Meal, FoodIds = [Pasta], MaxIob = 1 },
                    new EventCohortDefinition { Cohort = 2, Anchor = EventAnchorKind.Meal, Others = true },
                ],
            },
            Primary = new EventResponseRange
            {
                Cohorts =
                [
                    new EventCohortResult { Cohort = 1, Response = EventResponseKind.Rise, Anchors = 3, Events = 3, MissingAnchorIob = 2 },
                    new EventCohortResult { Cohort = 2, Response = EventResponseKind.Rise, Anchors = 20, Events = 20 },
                ],
            },
        };
        var labels = new Dictionary<int, string> { [1] = "Meal: Pizza, IOB at most 1 U", [2] = "Meal: all others" };

        var summary = EventResponseDataset.Summarise(report, labels, new Dictionary<int, int> { [1] = 1, [2] = 2 }, [], useMmol: false);

        summary.Should().Contain("Cohort 1 (Meal: Pizza, IOB at most 1 U): 3 events.");
        summary.Should().Contain("Not counted: 2 without an IOB value at the time.");
        summary.Should().Contain("Cohort 2 (Meal: all others): 20 events.\n  Every meal event in the range that cohort 1 does not take in, meals with carbs only included.");
        Imperative().Matches(summary).Select(m => m.Value).Should().BeEmpty();
    }

    [Fact]
    public void A_low_starts_sentences_name_it()
    {
        var glucose = new LoopAnalysisAggregate { P25 = 90, P50 = 105, P75 = 130, N = 12 };
        var low = new EventResponseEvent
        {
            Date = "2026-10-12", Time = "03:10", Anchor = EventAnchorKind.LowStart, Response = EventResponseKind.Fall,
            Metrics = new EventMetrics { StartGlucose = 68, Drop = -40, Nadir = 55, MinutesToNadir = 20, WentBelowRange = true },
        };

        EventResponseDataset.SlotSentence("Low start: all", EventAnchorKind.LowStart, -120, comparison: false, glucose, useMmol: false)
            .Should().Be("Low start: all, 120 minutes before the low start: median 105 mg/dL, middle half 90 to 130 mg/dL, 12 events.");
        EventResponseDataset.EventSentence(low, useMmol: false).Should().Be(
            "Low start on 2026-10-12 at 03:10: started at 68 mg/dL, was 40 mg/dL higher at the end of the window, was lowest at 55 mg/dL after 20 minutes, and went below range.");
    }

    [Fact]
    public void A_fall_groups_fact_keeps_the_sign_of_a_drop_that_was_a_rise()
    {
        var report = new EventResponseReport
        {
            Definition = new EventResponseDefinition { Cohorts = [new EventCohortDefinition { Cohort = 1, Anchor = EventAnchorKind.LowStart }] },
            Primary = new EventResponseRange
            {
                Cohorts =
                [
                    new EventCohortResult
                    {
                        Cohort = 1, Response = EventResponseKind.Fall, Events = 6,
                        Metrics = new EventMetricsSummary { Drop = new LoopAnalysisAggregate { P50 = -68 } },
                    },
                ],
            },
        };

        EventResponseDataset.FactsOf(report, new Dictionary<int, string> { [1] = "Low start: all" }, useMmol: false)
            .Single().Note.Should().Be("median drop -68 mg/dL");
    }

    [Fact]
    public void Low_starts_are_audited_as_glucose_and_a_condition_as_treatments()
    {
        var report = new EventResponseReport
        {
            Definition = new EventResponseDefinition
            {
                Cohorts = [new EventCohortDefinition { Cohort = 1, Anchor = EventAnchorKind.LowStart, MinMinutesSinceMeal = 240 }],
            },
            Primary = new EventResponseRange { Events = [new EventResponseEvent { Anchor = EventAnchorKind.LowStart }] },
        };
        report.WithheldSeries.AddRange([LoopAnalysisSeries.EventIob, LoopAnalysisSeries.EventCob, LoopAnalysisSeries.EventNetInsulin]);
        var log = new ReportReadLog();

        EventResponseDataset.RecordReads(log, report, ran: true, readFoodNames: false);

        log.Datasets.Should().Equal("glucose", "treatments");
        log.RecordCount.Should().Be(0, "a low start is read off readings, which are not counted");
    }

    [Fact]
    public void A_panel_with_a_group_the_meals_and_events_page_cannot_show_has_no_link()
    {
        ReportPanelSpec Panel(ReportEventCohort cohort) => new()
        {
            Id = "p1",
            Kind = ReportPanelKind.Table,
            Dataset = new EventResponseParameters { Cohorts = [new ReportEventCohort { Anchor = EventAnchorKind.Meal }, cohort] },
        };
        var result = new ReportPanelResult { Id = "p1", Kind = ReportPanelKind.Table, Status = ReportPanelStatus.Ok, Summary = "", Window = new ReportWindow(0, 86_400_000) };

        ReportLinks.For("https://x", Panel(new ReportEventCohort { Anchor = EventAnchorKind.Meal, Others = true }), result, "UTC").Should().BeNull();
        ReportLinks.For("https://x", Panel(new ReportEventCohort { Anchor = EventAnchorKind.Meal, StartGlucose = new ReportStartGlucose { Min = 100 } }), result, "UTC")
            .Should().BeNull();
        ReportLinks.For("https://x", Panel(new ReportEventCohort { Anchor = EventAnchorKind.LowStart }), result, "UTC")
            .Should().Contain("anchor2=LowStart");
    }

    [Theory]
    [InlineData(EventAnchorKind.SiteChange, "Site change", "device_events")]
    [InlineData(EventAnchorKind.SleepStart, "Sleep start", "sleep_sessions")]
    [InlineData(EventAnchorKind.Wake, "Wake-up", "sleep_sessions")]
    [InlineData(EventAnchorKind.LowTreatment, "Low treatment", "treatments")]
    [InlineData(EventAnchorKind.AnyBolus, "Bolus", "treatments")]
    public void A_new_anchor_has_words_and_is_audited_as_the_category_it_is_read_from(EventAnchorKind anchor, string words, string category)
    {
        EventResponseDataset.AnchorWords(anchor).Should().Be(words);
        EventResponseDataset.AnchorCategory(anchor).Should().Be(category);
        CohortErrors($$"""[ { "anchor": "{{anchor}}" } ]""").Should().BeEmpty();
        EventResponseDataset.SlotSentence($"{words}: all", anchor, 30, false, null, false)
            .Should().Be($"{words}: all, 30 minutes after the {words.ToLowerInvariant()}: no readings.");
    }

    [Fact]
    public void A_report_asks_for_up_to_six_groups_each_read_as_the_endpoint_reads_one()
    {
        var six = string.Join(", ", Enumerable.Repeat("""{ "anchor": "Meal" }""", 5).Append("""{ "anchor": "Meal", "grams": { "min": 80, "max": 20 } }"""));
        CohortErrors($"[ {six} ]").Should().BeEmpty();
        CohortErrors($$"""[ {{six}}, { "anchor": "LowStart" } ]""").Should().ContainSingle()
            .Which.Message.Should().Be("Use 1 to 6 groups, or none for every meal.");

        var parameters = new EventResponseParameters
        {
            Cohorts = [.. Enumerable.Range(0, 5).Select(_ => new ReportEventCohort { Anchor = EventAnchorKind.LowStart }),
                new ReportEventCohort { Anchor = EventAnchorKind.Meal, Grams = new ReportGrams { Min = 80, Max = 20 } }],
        };
        var definition = parameters.ToDefinition(parameters.AppliedCohorts);

        definition.Cohorts.Select(c => c.Cohort).Should().Equal(1, 2, 3, 4, 5, 6);
        (definition.Cohorts[5].MinGrams, definition.Cohorts[5].MaxGrams).Should().Be((20, 80), "grams the wrong way round are swapped, as on the endpoint");
        parameters.HasReportOnlyCohorts.Should().BeTrue("the Meals and events page shows at most three groups");
    }
}
