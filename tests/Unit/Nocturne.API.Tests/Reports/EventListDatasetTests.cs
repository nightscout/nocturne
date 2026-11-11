using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Reports;
using Nocturne.API.Reports.Datasets;
using Nocturne.API.Reports.Derived;
using Nocturne.Core.Models;
using Nocturne.Core.Models.LoopAnalysis;
using Xunit;

namespace Nocturne.API.Tests.Reports;

/// <summary>The datasets that draw the Meals and events groups event by event: each event, the spread of a figure, and when events happen.</summary>
public class EventListDatasetTests
{
    private static readonly ReportSpecValidator Validator = new(ReportSpecValidatorTests.Registry, TimeProvider.System);

    private static ReportSpecValidation Layer(string dataset, string mark, string bindings = "") =>
        Validator.Validate(JsonDocument.Parse($$"""
            { "version": 1, "panels": [ { "kind": "chart", "tracks": [ { "layers": [
              { "dataset": {{dataset}}, "mark": "{{mark}}" {{bindings}} } ] } ] } ] }
            """).RootElement);

    [Fact]
    public void Each_event_is_drawn_as_points_of_the_peak_rise_against_the_glucose_at_the_event_by_default()
    {
        var validation = Layer("""{ "name": "event_outcomes", "cohorts": [ { "anchor": "Meal" }, { "anchor": "Meal", "others": true } ] }""", "points");

        validation.Errors.Should().BeEmpty();
        var layer = validation.Spec!.Panels[0].Tracks![0].Layers[0];
        layer.XKind.Should().Be(ReportXKind.Value);
        layer.Bindings.Should().BeEquivalentTo(new ReportLayerBindings { X = "startGlucose", Y = ["peakRise"], Fit = "peakRiseFit" });
        layer.Unit.Should().Be("mg/dL");
    }

    [Theory]
    [InlineData("minutesSinceMeal", "drop", "dropFit", "value")]
    [InlineData("timeOfDay", "nadir", "nadirFit", "time_of_day")]
    [InlineData("iobAtEvent", "minutesBackToStart", "minutesBackToStartFit", "value")]
    [InlineData("grams", "netInsulin", "netInsulinFit", "value")]
    public void A_figure_may_lie_against_any_value_at_the_event(string x, string y, string fit, string kind)
    {
        var validation = Layer("""{ "name": "event_outcomes", "group": 1 }""", "points", $$""", "bindings": { "x": "{{x}}", "y": ["{{y}}"], "fit": "{{fit}}" }""");

        validation.Errors.Should().BeEmpty();
        ReportWire.Name(validation.Spec!.Panels[0].Tracks![0].Layers[0].XKind).Should().Be(kind);
    }

    [Fact]
    public void A_line_of_another_figure_is_refused_for_its_unit()
    {
        Layer("""{ "name": "event_outcomes" }""", "points", """, "bindings": { "x": "startGlucose", "y": ["peakRise"], "fit": "minutesToPeakFit" }""")
            .Errors.Should().ContainSingle().Which.Path.Should().EndWith(".bindings.fit");
    }

    [Theory]
    [InlineData("event_outcomes")]
    [InlineData("event_outcome_spread")]
    [InlineData("event_occurrence")]
    public void An_event_list_dataset_takes_no_comparison_range_and_a_group_it_has(string name)
    {
        var errors = Layer($$"""
            { "name": "{{name}}", "group": 3, "cohorts": [ { "anchor": "Meal" }, { "anchor": "LowStart" } ],
              "comparisonRange": { "from": "2026-09-01", "to": "2026-09-30" } }
            """, name == "event_outcomes" ? "points" : "bars").Errors;

        errors.Select(e => e.Path.Split(".dataset.")[1]).Should().BeEquivalentTo(["comparisonRange", "group"]);
        errors.Single(e => e.Path.EndsWith("group")).Message.Should().Be("Send a group number from 1 to 2.");
    }

    [Fact]
    public void The_spread_and_the_occurrence_draw_a_bar_per_group_and_occurrence_also_by_weekday()
    {
        var spread = Layer("""{ "name": "event_outcome_spread", "outcome": "nadir", "cohorts": [ { "anchor": "LowStart" } ] }""", "bars");
        spread.Errors.Should().BeEmpty();
        spread.Spec!.Panels[0].Tracks![0].Layers[0].Bindings.Y.Should().Equal("events1", "events2", "events3");
        spread.Spec.Panels[0].Tracks![0].Layers[0].XKind.Should().Be(ReportXKind.Category);

        var hours = Layer("""{ "name": "event_occurrence" }""", "bars");
        hours.Errors.Should().BeEmpty();
        hours.Spec!.Panels[0].Tracks![0].Layers[0].XKind.Should().Be(ReportXKind.TimeOfDay);
        var days = Layer("""{ "name": "event_occurrence" }""", "bars", """, "bindings": { "x": "weekday", "y": ["events1"] }""");
        days.Errors.Should().BeEmpty();
        days.Spec!.Panels[0].Tracks![0].Layers[0].XKind.Should().Be(ReportXKind.Category);
    }

    [Fact]
    public void The_spread_cuts_the_figure_into_round_ranges_with_empty_ones_kept()
    {
        var outcome = EventOutcomeInfo.Of(EventOutcome.PeakRise);
        var values = new Dictionary<int, List<double>> { [1] = [12, 18, 47], [2] = [5, 21] };

        var rows = EventOutcomeSpreadDataset.Rows(values, outcome, "mg/dL", ["Meal: Pizza", "Meal: all others"], useMmol: false);

        EventOutcomeSpreadDataset.Width(5, 47).Should().Be(5);
        rows.Select(r => r["range"]).Should().Equal(
            "5 to 10 mg/dL", "10 to 15 mg/dL", "15 to 20 mg/dL", "20 to 25 mg/dL", "25 to 30 mg/dL",
            "30 to 35 mg/dL", "35 to 40 mg/dL", "40 to 45 mg/dL", "45 to 50 mg/dL");
        rows.Select(r => r["events1"]).Should().Equal(0, 1, 1, 0, 0, 0, 0, 0, 1);
        rows.Select(r => r["events2"]).Should().Equal(1, 0, 0, 1, 0, 0, 0, 0, 0);
        rows[3][ReportField.SentenceField].Should().Be("Peak rise from 20 up to 25 mg/dL: Meal: Pizza 0 events, Meal: all others 1 event.");
        rows.Should().OnlyContain(r => !r.ContainsKey("events3"), "a group not asked for has no bar");
    }

    [Theory]
    [InlineData(2.3, 14.8, 2)]
    [InlineData(-40, 210, 50)]
    [InlineData(0.05, 0.4, 0.05)]
    public void A_range_width_is_one_two_or_five_times_a_power_of_ten(double min, double max, double width)
    {
        EventOutcomeSpreadDataset.Width(min, max).Should().BeApproximately(width, 1e-9);
        ((max - min) / width).Should().BeLessThanOrEqualTo(EventOutcomeSpreadDataset.MaxBins);
    }

    [Fact]
    public void One_value_alone_still_gets_a_range()
    {
        var rows = EventOutcomeSpreadDataset.Rows(new Dictionary<int, List<double>> { [1] = [4.4] }, EventOutcomeInfo.Of(EventOutcome.Nadir), "mmol/L", ["Low start: all"], useMmol: true);

        rows.Should().ContainSingle().Which["events1"].Should().Be(1);
    }

    [Fact]
    public void The_line_sentence_states_r_slope_and_intercept_in_units_and_no_direction()
    {
        var facts = new List<ReportFact>();
        var x = new ReportField("iobAtEvent", "IOB at the event", ReportFieldKind.Value, "U", "");
        var line = DerivedMath.LeastSquares([(0, 100), (1, 80), (2, 61), (3, 39)])!;

        var sentence = EventOutcomesDataset.FitSentence(EventOutcomeInfo.Of(EventOutcome.PeakRise), x, line, -0.99, 4, useMmol: false, facts);

        sentence.Should().Be("Peak rise against IOB at the event: r = -0.99 over 4 events. The least-squares line has a slope of -20.2 mg/dL/U and an intercept of 100.3 mg/dL.");
        facts.Select(f => f.Label).Should().Equal("r", "Events with both", "Slope");
        EventOutcomesDataset.FitSentence(EventOutcomeInfo.Of(EventOutcome.Drop), x, null, null, 2, false, [])
            .Should().Be("Drop against IOB at the event: a line needs at least 3 events that vary; there are 2 events with both.");
    }

    [Fact]
    public void The_groups_drawn_are_the_shown_groups_by_their_number_as_asked()
    {
        var report = new EventResponseReport
        {
            Definition = new EventResponseDefinition { Cohorts = [new EventCohortDefinition { Cohort = 1 }, new EventCohortDefinition { Cohort = 2 }] },
            Primary = new EventResponseRange
            {
                Events =
                [
                    new EventResponseEvent { Time = "08:00", Cohorts = [1] },
                    new EventResponseEvent { Time = "12:00", Cohorts = [1, 2] },
                    new EventResponseEvent { Time = "19:00", Cohorts = [2] },
                ],
            },
        };
        // Group 2 as asked was left out, so the report's cohorts 1 and 2 are groups 1 and 3.
        var run = new EventResponseDataset.EventRun(report, new Dictionary<int, string>(), new Dictionary<int, int> { [1] = 1, [2] = 3 }, [], ["A", "B", "C"]);

        var all = EventListDataset.Shown(run, new EventOutcomesParameters());
        var third = EventListDataset.Shown(run, new EventOutcomesParameters { Group = 3 });

        all.Should().Equal(1, 3);
        EventListDataset.Events(run, all).Select(e => string.Join(",", e.Groups)).Should().Equal("1", "1,3", "3");
        EventListDataset.Events(run, third).Select(e => e.Event.Time).Should().Equal("12:00", "19:00");
    }

    [Fact]
    public void The_catalogue_describes_each_new_dataset_with_its_marks_and_axes()
    {
        var catalogue = ReportCatalogueTests.Catalogue();
        var each = catalogue.Datasets.Single(d => d.Name == "event_outcomes");
        each.Label.Should().Be("Each event");
        each.XKinds.Should().BeEquivalentTo(["value", "time_of_day"]);
        each.Fields.Single(f => f.Name == "minutesSinceMeal").Scope.Should().Be(Nocturne.Core.Models.Authorization.Scope.TreatmentsRead);
        catalogue.Datasets.Single(d => d.Name == "event_outcome_spread").Parameters.GetProperty("properties").GetProperty("outcome")
            .GetProperty("x-enumLabels").EnumerateArray().Select(l => l.GetString()).Should().Contain(["Peak rise", "Lowest reading"]);
        catalogue.Datasets.Single(d => d.Name == "event_occurrence").XKinds.Should().BeEquivalentTo(["time_of_day", "category"]);
    }
}
