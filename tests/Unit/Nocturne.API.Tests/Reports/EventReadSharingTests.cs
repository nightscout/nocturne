using System.Text.Json;
using FluentAssertions;
using Moq;
using Nocturne.API.Reports;
using Nocturne.API.Reports.Datasets;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.LoopAnalysis;
using Xunit;

namespace Nocturne.API.Tests.Reports;

/// <summary>The event datasets of one report over the same groups: one analysis, counted once, and each layer its own text.</summary>
public class EventReadSharingTests
{
    private static ReportSpecValidation Validate(string json)
    {
        var validation = RecordDatasetTests.Validator.Validate(JsonDocument.Parse(json).RootElement);
        validation.Errors.Should().BeEmpty();
        return validation;
    }

    private const string Groups = """ "cohorts": [ { "anchor": "Meal" }, { "anchor": "Meal", "others": true } ] """;

    private static readonly string Spec = $$"""
        { "version": 1, "range": { "hours": 336 }, "panels": [
          { "kind": "chart", "tracks": [ { "layers": [
            { "id": "curves", "dataset": { "name": "event_response", {{Groups}} }, "mark": "band" } ] } ] },
          { "kind": "chart", "tracks": [ { "layers": [
            { "id": "g1", "dataset": { "name": "event_outcomes", "group": 1, {{Groups}} }, "mark": "points" },
            { "id": "g2", "dataset": { "name": "event_outcomes", "group": 2, {{Groups}} }, "mark": "points" } ] } ] },
          { "kind": "chart", "tracks": [ { "layers": [
            { "id": "rise", "dataset": { "name": "event_outcomes", {{Groups}} }, "mark": "points" },
            { "id": "drop", "dataset": { "name": "event_outcomes", {{Groups}} }, "mark": "points",
              "bindings": { "x": "startGlucose", "y": ["drop"], "fit": "dropFit" } } ] } ] },
          { "kind": "chart", "tracks": [ { "layers": [
            { "id": "spread", "dataset": { "name": "event_outcome_spread", {{Groups}} }, "mark": "bars" } ] } ] },
          { "kind": "chart", "tracks": [ { "layers": [
            { "id": "hours", "dataset": { "name": "event_occurrence", {{Groups}} }, "mark": "bars" } ] } ] } ] }
        """;

    private static RecordDatasetTests.Harness Harness()
    {
        var harness = new RecordDatasetTests.Harness();
        harness.Loop.Setup(l => l.GetEventResponseAsync(It.IsAny<LoopAnalysisFilter>(), It.IsAny<EventResponseDefinition>(), It.IsAny<TimeZoneInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LoopAnalysisFilter _, EventResponseDefinition definition, TimeZoneInfo _, CancellationToken _) => new EventResponseReport
            {
                Primary = new EventResponseRange
                {
                    Cohorts = [.. definition.Cohorts.Select(c => new EventCohortResult { Cohort = c.Cohort, Events = 2, Response = EventResponseKind.Rise })],
                    Events =
                    [
                        new EventResponseEvent { Date = "2026-10-20", Time = "08:00", Anchor = EventAnchorKind.Meal, Cohorts = [1],
                            Metrics = new EventMetrics { StartGlucose = 100, PeakRise = 60, Drop = -40 } },
                        new EventResponseEvent { Date = "2026-10-21", Time = "12:00", Anchor = EventAnchorKind.Meal, Cohorts = [2],
                            Metrics = new EventMetrics { StartGlucose = 140, PeakRise = 30, Drop = -10 } },
                        new EventResponseEvent { Date = "2026-10-22", Time = "19:00", Anchor = EventAnchorKind.Meal, Cohorts = [2],
                            Metrics = new EventMetrics { StartGlucose = 180, PeakRise = 10, Drop = 20 } },
                    ],
                },
            });
        return harness;
    }

    [Fact]
    public async Task Every_event_dataset_over_the_same_groups_shares_one_analysis_and_counts_it_once()
    {
        var harness = Harness();
        var (runner, context, log) = harness.Arrange(Scope.GlucoseRead, Scope.TreatmentsRead);
        var validation = Validate(Spec);

        await runner.RunAsync(validation.Spec!, context, CancellationToken.None);

        harness.Loop.Verify(l => l.GetEventResponseAsync(It.IsAny<LoopAnalysisFilter>(), It.IsAny<EventResponseDefinition>(),
            It.IsAny<TimeZoneInfo>(), It.IsAny<CancellationToken>()), Times.Once);
        validation.Reads.Should().Be(1, "the curves, each event, the spread and the hours read one analysis of the same groups");
        context.Cache.Reads.Should().Be(validation.Reads);
        log.RecordCount.Should().Be(3, "the three events are audited once");
    }

    [Fact]
    public async Task Two_each_event_layers_that_differ_only_in_the_figure_say_each_their_own_figure()
    {
        var (runner, context, _) = Harness().Arrange(Scope.GlucoseRead, Scope.TreatmentsRead);

        var result = await runner.RunAsync(Validate(Spec).Spec!, context, CancellationToken.None);

        var layers = result.Panels[2].Tracks![0].Layers;
        layers[0].Sentence.Should().StartWith("Peak rise against glucose at the event");
        layers[1].Sentence.Should().StartWith("Drop against glucose at the event");
    }

    [Fact]
    public async Task Without_the_event_list_each_event_layer_is_not_shown_rather_than_showing_no_events()
    {
        var (runner, context, _) = Harness().Arrange(Scope.GlucoseRead);

        var result = await runner.RunAsync(Validate(Spec).Spec!, context, CancellationToken.None);

        foreach (var layer in result.Panels.Skip(1).SelectMany(p => p.Tracks![0].Layers))
        {
            layer.Rows.Should().BeEmpty();
            layer.Facts.Should().BeEmpty("a count of 0 events would be false: the events are withheld, not absent");
            layer.Summary.Should().StartWith("Not shown:").And.Contain("treatments");
        }
    }

    private static async Task<(string Summary, EventResponseDefinition? Asked)> RunGroups(string cohorts, params string[] scopes)
    {
        var harness = Harness();
        EventResponseDefinition? asked = null;
        harness.Loop.Setup(l => l.GetEventResponseAsync(It.IsAny<LoopAnalysisFilter>(), It.IsAny<EventResponseDefinition>(), It.IsAny<TimeZoneInfo>(), It.IsAny<CancellationToken>()))
            .Callback((LoopAnalysisFilter _, EventResponseDefinition definition, TimeZoneInfo _, CancellationToken _) => asked = definition)
            .ReturnsAsync(new EventResponseReport());
        var (runner, context, _) = harness.Arrange(scopes);
        var result = await runner.RunAsync(Validate($$"""
            { "version": 1, "range": { "hours": 336 }, "panels": [ { "kind": "table", "dataset": { "name": "event_response", "cohorts": {{cohorts}} } } ] }
            """).Spec!, context, CancellationToken.None);
        return (result.Panels[0].Summary, asked);
    }

    [Fact]
    public async Task Sleep_starts_wake_ups_and_site_changes_are_left_out_without_the_sleep_and_devices_scopes()
    {
        var (summary, asked) = await RunGroups(
            """[ { "anchor": "SleepStart" }, { "anchor": "Wake" }, { "anchor": "SiteChange" }, { "anchor": "Meal" } ]""",
            Scope.GlucoseRead, Scope.TreatmentsRead);

        asked!.Cohorts.Select(c => c.Anchor).Should().Equal(EventAnchorKind.Meal);
        summary.Should().Contain("cohorts[0] (SleepStart)").And.Contain("cohorts[1] (Wake)").And.Contain("cohorts[2] (SiteChange)");

        var (_, all) = await RunGroups(
            """[ { "anchor": "SleepStart" }, { "anchor": "Wake" }, { "anchor": "SiteChange" } ]""",
            Scope.GlucoseRead, Scope.SleepRead, Scope.DevicesRead);
        all!.Cohorts.Should().HaveCount(3);
    }

    [Theory]
    [InlineData("""[ { "anchor": "Meal", "foods": ["0190a3c4-0000-7000-8000-000000000001"] }, { "anchor": "Meal", "others": true } ]""", Scope.TreatmentsRead)]
    [InlineData("""[ { "anchor": "LowStart", "minutesSinceMeal": { "min": 240 } }, { "anchor": "LowStart", "others": true } ]""", Scope.DevicesRead)]
    public async Task All_others_is_left_out_with_its_sibling_rather_than_taking_in_the_siblings_events(string cohorts, string other)
    {
        var (summary, asked) = await RunGroups(cohorts, Scope.GlucoseRead, other);

        asked.Should().BeNull("no group is left to read");
        summary.Should().Contain("Cohort 2 is not shown: it holds the events no other group takes in, and cohort 1 is not shown.");
    }

    [Fact]
    public async Task A_layer_or_panel_of_one_event_group_is_named_by_the_groups_label()
    {
        var (runner, context, _) = Harness().Arrange(Scope.GlucoseRead, Scope.TreatmentsRead);

        var result = await runner.RunAsync(Validate($$"""
            { "version": 1, "range": { "hours": 336 }, "panels": [
              { "kind": "chart", "tracks": [ { "layers": [ { "dataset": { "name": "event_response", "cohorts": [ { "anchor": "LowStart" } ] }, "mark": "band" } ] } ] },
              { "kind": "chart", "tracks": [ { "layers": [
                { "dataset": { "name": "event_outcomes", "group": 2, {{Groups}} }, "mark": "points" },
                { "dataset": { "name": "event_outcomes", {{Groups}} }, "mark": "points" } ] } ] },
              { "kind": "table", "dataset": { "name": "event_response", "cohorts": [ { "anchor": "Meal", "minutesSinceMeal": { "min": 240 } } ] } } ] }
            """).Spec!, context, CancellationToken.None);

        result.Panels[0].Title.Should().Be("Low start: all");
        result.Panels[0].Tracks![0].Layers[0].Title.Should().Be("Low start: all");
        result.Panels[1].Tracks![0].Layers.Select(l => l.Title).Should().Equal("Meal: all others", "Each event · Peak rise");
        result.Panels[1].Title.Should().Be("Meal: all others");
        result.Panels[2].Title.Should().Be("Meal: no carbs in the 4 hours before");
    }

    [Fact]
    public async Task The_groups_left_out_say_why_as_data()
    {
        var (runner, context, _) = Harness().Arrange(Scope.GlucoseRead, Scope.TreatmentsRead);

        var result = await runner.RunAsync(Validate("""
            { "version": 1, "range": { "hours": 336 }, "panels": [ { "kind": "table", "dataset": { "name": "event_response", "cohorts": [
              { "anchor": "Meal", "foods": ["0190a3c4-0000-7000-8000-000000000001"] }, { "anchor": "Meal", "others": true },
              { "anchor": "SleepStart" }, { "anchor": "LowStart" } ] } } ] }
            """).Spec!, context, CancellationToken.None);

        result.Panels[0].Cohorts.Select(c => c.Omitted).Should().Equal(
            new ReportCohortOmission(ReportCohortOmission.ScopeReason, Scope.FoodRead),
            new ReportCohortOmission(ReportCohortOmission.SiblingReason, Group: 1),
            new ReportCohortOmission(ReportCohortOmission.ScopeReason, Scope.SleepRead),
            null);
        JsonSerializer.Serialize(result.Panels[0].Cohorts, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Should().Be(
            """[{"number":1,"label":"Meal: a food filter","omitted":{"reason":"scope","scope":"food.read"}},"""
            + """{"number":2,"label":"Meal: all others","omitted":{"reason":"sibling","group":1}},"""
            + """{"number":3,"label":"Sleep start: all","omitted":{"reason":"scope","scope":"sleep.read"}},"""
            + """{"number":4,"label":"Low start: all"}]""");
    }
}
