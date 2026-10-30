using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Nocturne.API.Mcp;
using Nocturne.API.Mcp.Reports;
using Nocturne.API.Mcp.Reports.Datasets;
using Nocturne.Core.Models.LoopAnalysis;
using Xunit;

namespace Nocturne.API.Tests.Mcp;

/// <summary>
/// The Loop Analysis datasets: the event cohorts as the endpoint's query, the validator's range_bands
/// bindings and nested parameter errors, the summaries, the refine patch and the web links.
/// </summary>
public partial class LoopAnalysisDatasetTests
{
    private static readonly ReportSpecValidator Validator = new(ReportSpecValidatorTests.Registry, TimeProvider.System);

    private static ReportSpecValidation Validate(string json) => Validator.Validate(JsonDocument.Parse(json).RootElement);

    private static readonly Guid Pasta = Guid.Parse("0190a3c4-0000-7000-8000-000000000001");

    [Fact]
    public void Cohorts_map_onto_the_endpoints_query_numbered_in_order()
    {
        var parameters = new EventResponseParameters
        {
            Cohorts =
            [
                new ReportEventCohort
                {
                    Anchor = EventAnchorKind.Meal,
                    TimeOfDay = new ReportTimeOfDay { FromMinute = 360, ToMinute = 600 },
                    Grams = new ReportGrams { Min = 80, Max = 20 },
                    DayOfWeek = [ReportWeekday.Sat, ReportWeekday.Mon],
                    Foods = [Pasta],
                    FoodCategory = " Pasta ",
                },
                new ReportEventCohort { Anchor = EventAnchorKind.UnannouncedRise, TimeOfDay = new ReportTimeOfDay { FromMinute = 1320, ToMinute = 120 } },
            ],
            MinutesBefore = 30,
            MinutesAfter = 240,
            IsolatedOnly = true,
        };

        var query = parameters.ToEventQuery(parameters.AppliedCohorts);
        query.Problem().Should().BeNull();
        var definition = query.ToDefinition();

        definition.MinutesBefore.Should().Be(30);
        definition.MinutesAfter.Should().Be(240);
        definition.IsolatedOnly.Should().BeTrue();
        definition.Cohorts.Should().HaveCount(2);
        var meal = definition.Cohorts[0];
        meal.Cohort.Should().Be(1);
        meal.Anchor.Should().Be(EventAnchorKind.Meal);
        (meal.FromMinute, meal.ToMinute).Should().Be((360, 600));
        (meal.MinGrams, meal.MaxGrams).Should().Be((20, 80), "grams the wrong way round are swapped, as on the endpoint");
        meal.DaysOfWeek.Should().Equal(DayOfWeek.Monday, DayOfWeek.Saturday);
        meal.FoodIds.Should().Equal(Pasta);
        meal.FoodCategory.Should().Be("Pasta");
        definition.Cohorts[1].Should().BeEquivalentTo(new { Cohort = 2, Anchor = EventAnchorKind.UnannouncedRise, FromMinute = 1320, ToMinute = 120 });
    }

    [Fact]
    public void No_cohorts_is_every_meal()
    {
        var parameters = new EventResponseParameters();
        var definition = parameters.ToEventQuery(parameters.AppliedCohorts).ToDefinition();

        definition.Cohorts.Should().ContainSingle().Which.Anchor.Should().Be(EventAnchorKind.Meal);
    }

    [Fact]
    public void An_event_response_panel_gets_range_bands_defaults_and_reads_its_cohorts()
    {
        var validation = Validate($$"""
            { "version": 1, "panels": [
              { "dataset": { "name": "event_response", "cohorts": [
                  { "anchor": "Meal", "foods": ["{{Pasta}}"], "dayOfWeek": ["Mon", "Tue"], "timeOfDay": { "fromMinute": 360, "toMinute": 600 } } ],
                "comparisonRange": { "from": "2026-09-01", "to": "2026-09-30" } },
                "primitive": "range_bands" },
              { "dataset": { "name": "event_response" }, "primitive": "table" } ] }
            """);

        validation.Errors.Should().BeEmpty();
        var bands = validation.Spec!.Panels[0];
        bands.Bindings.Should().BeEquivalentTo(new ReportBindings
        {
            X = "minute", Y = ["glucoseP50"], Band = ["glucoseP10", "glucoseP25", "glucoseP75", "glucoseP90"], Series = "series",
        });
        var parameters = bands.Dataset.Should().BeOfType<EventResponseParameters>().Subject;
        parameters.Cohorts![0].Foods.Should().Equal(Pasta);
        parameters.Cohorts[0].DayOfWeek.Should().Equal(ReportWeekday.Mon, ReportWeekday.Tue);
        parameters.ComparisonRange.Should().Be(new ReportDateRange { From = new DateOnly(2026, 9, 1), To = new DateOnly(2026, 9, 30) });
        bands.Range.Should().Be(new ReportRange { Hours = 30 * 24 });

        validation.Spec.Panels[1].Bindings!.Columns.Should().Contain(["at", "anchor", "summary"]).And.NotContain("glucoseP50");
    }

    [Fact]
    public void A_nested_parameter_error_names_its_path_and_the_choices()
    {
        var errors = Validate("""
            { "version": 1, "panels": [ { "dataset": { "name": "event_response", "cohorts": [ { "anchor": "Lunch" } ] }, "primitive": "range_bands" } ] }
            """).Errors;

        var error = errors.Should().ContainSingle().Subject;
        error.Path.Should().Be("panels[0].dataset.cohorts[0].anchor");
        error.Message.Should().Contain("Meal").And.Contain("UnannouncedRise");
    }

    [Fact]
    public void A_time_series_may_lie_along_the_time_of_day()
    {
        var validation = Validate("""
            { "version": 1, "panels": [
              { "dataset": { "name": "settings_evidence", "setting": "isf" }, "primitive": "time_series",
                "bindings": { "x": "startMinute", "y": ["isfObserved", "isfProfile"] } },
              { "dataset": { "name": "daily_profile" }, "primitive": "time_series", "bindings": { "x": "minuteOfDay", "y": ["iob"] } } ] }
            """);

        validation.Errors.Should().BeEmpty();
    }

    [Fact]
    public void An_isf_field_is_written_in_the_display_unit_per_unit()
    {
        var isf = new SettingsEvidenceDataset().Fields.Single(f => f.Name == "isfObserved");

        isf.ResolveUnit(useMmol: true).Unit.Should().Be("mmol/L/U");
        isf.ResolveUnit(useMmol: false).Unit.Should().Be("mg/dL/U");
    }

    private static SettingsEvidenceReport Evidence() => new()
    {
        PrimaryRange = new LoopAnalysisResolvedRange { StartDate = new DateOnly(2026, 10, 1), EndDate = new DateOnly(2026, 10, 28) },
        Primary = new SettingsEvidenceRange
        {
            Basal =
            [
                new BasalEvidenceBlock
                {
                    StartHour = 6, EndHour = 8, Observed = 0.95, Profile = 0.8, N = 11,
                    Spread = new LoopAnalysisAggregate { P25 = 0.88, P75 = 1.02 },
                    Confidence = SettingsEvidenceConfidence.Moderate,
                    ConfidenceReasons = [SettingsEvidenceConfidenceReason.WideSpread], TowardsMoreInsulin = true,
                    Rejections = [new SettingsEvidenceRejection { Rule = SettingsEvidenceRule.CarbsBefore, Count = 6 }],
                },
                new BasalEvidenceBlock { StartHour = 8, EndHour = 10, Profile = 0.7, N = 2, UnavailableReason = SettingsEvidenceUnavailableReason.TooFew },
            ],
            Isf =
            [
                new IsfEvidenceBlock
                {
                    Block = SettingsEvidenceBlock.Midday, StartHour = 11, EndHour = 17, Observed = 36, Profile = 54, N = 7,
                    Spread = new LoopAnalysisAggregate { P25 = 30, P75 = 45 }, Confidence = SettingsEvidenceConfidence.Low,
                    ConfidenceReasons = [SettingsEvidenceConfidenceReason.FewEpisodes, SettingsEvidenceConfidenceReason.ManyWentLow],
                    TowardsLessInsulin = true,
                },
            ],
            CarbRatio =
            [
                new CarbRatioEvidenceBlock
                {
                    Block = SettingsEvidenceBlock.Morning, StartHour = 6, EndHour = 11, Observed = 8, Profile = 10, N = 12,
                    Spread = new LoopAnalysisAggregate { P25 = 7, P75 = 9 }, Confidence = SettingsEvidenceConfidence.High,
                },
            ],
        },
    };

    [GeneratedRegex(@"\b(increase|increasing|decrease|decreasing|lower|lowering|raise|raising|reduce|reducing|should|must|recommend\w*|consider|try|adjust\w*|change)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Imperative();

    [Fact]
    public void The_settings_evidence_summary_states_observations_and_tells_no_one_what_to_set()
    {
        var report = Evidence();
        var rows = SettingsEvidenceRowsFor(report, useMmol: false);
        var summary = SettingsEvidenceDataset.Summarise(report, rows, useMmol: false);

        summary.Should().Contain("Basal 06:00-08:00: observed 0.95 U/h next to the profile's 0.8 U/h");
        summary.Should().Contain("ISF 11:00-17:00: observed 36 mg/dL/U next to the profile's 54 mg/dL/U");
        summary.Should().Contain("Carb ratio 06:00-11:00: observed 8 g/U next to the profile's 10 g/U");
        summary.Should().Contain("Basal 08:00-10:00: no observed value (too few");
        summary.Should().Contain("not instructions");
        Imperative().Matches(summary).Select(m => m.Value).Should().BeEmpty();
        Imperative().Matches(string.Join(' ', rows.SelectMany(r => r.Values).OfType<string>())).Select(m => m.Value).Should().BeEmpty();
    }

    [Fact]
    public void The_settings_evidence_summary_in_mmol_converts_the_isf()
    {
        var report = Evidence();
        var summary = SettingsEvidenceDataset.Summarise(report, SettingsEvidenceRowsFor(report, useMmol: true), useMmol: true);

        summary.Should().Contain("observed 2 mmol/L/U next to the profile's 3 mmol/L/U");
    }

    [Theory]
    [InlineData(SettingsEvidenceWithheldReason.ReadScopes, "therapy.read")]
    [InlineData(SettingsEvidenceWithheldReason.HistoryClamped, "last 24 hours")]
    public void Withheld_settings_evidence_says_why(SettingsEvidenceWithheldReason reason, string text)
    {
        SettingsEvidenceDataset.Summarise(new SettingsEvidenceReport { WithheldReason = reason }, [], false)
            .Should().StartWith("Settings evidence is not shown").And.Contain(text);
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> SettingsEvidenceRowsFor(SettingsEvidenceReport report, bool useMmol) =>
        (IReadOnlyList<IReadOnlyDictionary<string, object?>>)typeof(SettingsEvidenceDataset)
            .GetMethod("Rows", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [report.Primary!, null, useMmol])!;

    private static EventResponseReport Meals(bool foodFilter) => new()
    {
        PrimaryRange = new LoopAnalysisResolvedRange { StartDate = new DateOnly(2026, 10, 1), EndDate = new DateOnly(2026, 10, 28) },
        Definition = new EventResponseDefinition
        {
            Cohorts = [new EventCohortDefinition { Cohort = 1, Anchor = EventAnchorKind.Meal, FoodIds = foodFilter ? [Pasta] : null }],
        },
        Primary = new EventResponseRange
        {
            Cohorts =
            [
                new EventCohortResult
                {
                    Cohort = 1, Response = EventResponseKind.Rise, Anchors = 4, Events = 4, NotIsolated = 0, MissingAnchorGlucose = 1,
                    MealsWithFoodRecord = foodFilter ? 6 : 0, MealsWithoutFoodRecord = foodFilter ? 9 : 0,
                    Metrics = new EventMetricsSummary
                    {
                        PeakRise = new LoopAnalysisAggregate { P25 = 40, P50 = 68, P75 = 90 },
                        MinutesToPeak = new LoopAnalysisAggregate { P50 = 55 },
                        MinutesBackToStart = new LoopAnalysisAggregate { P50 = 150, N = 3 },
                    },
                },
            ],
        },
    };

    [Fact]
    public void The_event_summary_counts_carbs_only_meals_a_food_filter_could_not_match()
    {
        var report = Meals(foodFilter: true);
        var labels = new Dictionary<int, string> { [1] = "Cohort 1: Meal, foods Penne" };
        var summary = EventResponseDataset.Summarise(report, labels, [], useMmol: false);

        summary.Should().Contain("Cohort 1: Meal, foods Penne: 4 events.");
        summary.Should().Contain("Not counted: 1 without a reading at the time.");
        summary.Should().Contain("Food filter: 6 meals carried a food record, and 4 of them matched it. 9 meals had only carbs, with no food record, so the filter could not apply to them and they are not counted.");
        summary.Should().Contain("Median peak rise +68 mg/dL, 55 minutes after the event (middle half +40 to +90 mg/dL).");
        summary.Should().Contain("Back near the start within the window: 3 of 4 (75%).");
    }

    [Fact]
    public void A_sensor_start_cohort_is_audited_as_device_events_not_treatments()
    {
        var report = new EventResponseReport
        {
            Definition = new EventResponseDefinition { Cohorts = [new EventCohortDefinition { Cohort = 1, Anchor = EventAnchorKind.SensorStart }] },
            Primary = new EventResponseRange { Events = [new EventResponseEvent { Anchor = EventAnchorKind.SensorStart }] },
        };
        report.WithheldSeries.AddRange([LoopAnalysisSeries.EventIob, LoopAnalysisSeries.EventCob, LoopAnalysisSeries.EventNetInsulin]);
        var log = new McpToolReadLog();

        EventResponseDataset.RecordReads(log, report, ran: true, readFoodNames: false);

        log.Datasets.Should().Equal("glucose", "device_events");
        log.RecordCount.Should().Be(1, "one sensor start was read; readings are not counted");
    }

    [Fact]
    public void A_food_filtered_meal_cohort_is_audited_with_foods_and_its_events()
    {
        var report = new EventResponseReport
        {
            Definition = new EventResponseDefinition { Cohorts = [new EventCohortDefinition { Cohort = 1, Anchor = EventAnchorKind.Meal, FoodIds = [Pasta] }] },
            Primary = new EventResponseRange { Events = [new EventResponseEvent { Anchor = EventAnchorKind.Meal }, new EventResponseEvent { Anchor = EventAnchorKind.Meal }] },
        };
        var log = new McpToolReadLog();

        EventResponseDataset.RecordReads(log, report, ran: true, readFoodNames: true);

        log.Datasets.Should().BeEquivalentTo(["foods", "glucose", "treatments"]);
        log.RecordCount.Should().Be(2);
    }

    [Fact]
    public void An_event_panel_that_read_nothing_audits_nothing()
    {
        var log = new McpToolReadLog();

        EventResponseDataset.RecordReads(log, new EventResponseReport(), ran: false, readFoodNames: false);

        log.Datasets.Should().BeEmpty();
    }

    [Fact]
    public void Without_a_food_filter_the_event_summary_says_nothing_of_food()
    {
        var summary = EventResponseDataset.Summarise(
            Meals(foodFilter: false), new Dictionary<int, string> { [1] = "Cohort 1: Meal" }, [], useMmol: false);

        summary.Should().NotContain("food");
    }

    [Fact]
    public void An_events_sentence_states_its_figures_in_the_display_unit()
    {
        var meal = new EventResponseEvent
        {
            Date = "2026-10-12", Time = "08:05", Anchor = EventAnchorKind.Meal, Response = EventResponseKind.Rise, Grams = 45,
            Metrics = new EventMetrics { StartGlucose = 110, PeakRise = 72, MinutesToPeak = 55, MinutesBackToStart = 175, NetInsulin = 4.12 },
        };
        var correction = new EventResponseEvent
        {
            Date = "2026-10-13", Time = "22:40", Anchor = EventAnchorKind.CorrectionBolus, Response = EventResponseKind.Fall,
            Metrics = new EventMetrics { StartGlucose = 250, Drop = 90, Nadir = 150, MinutesToNadir = 200, WentBelowRange = false },
        };

        EventResponseDataset.EventSentence(meal, useMmol: false).Should().Be(
            "Meal of 45 g on 2026-10-12 at 08:05: started at 110 mg/dL, rose 72 mg/dL to its peak 55 minutes later, and was back near the start after 175 minutes. Insulin above the basal schedule: 4.12 U.");
        EventResponseDataset.EventSentence(correction, useMmol: true).Should().Be(
            "Correction bolus on 2026-10-13 at 22:40: started at 13.9 mmol/L, fell 5.0 mmol/L by the end of the window, and was lowest at 8.3 mmol/L after 200 minutes.");
        Imperative().IsMatch(EventResponseDataset.EventSentence(meal, false)).Should().BeFalse();
    }

    [Fact]
    public void A_cohort_label_names_its_filters()
    {
        var label = EventResponseDataset.CohortLabel(
            new EventCohortDefinition
            {
                Cohort = 1, Anchor = EventAnchorKind.Meal, FromMinute = 360, ToMinute = 600, MinGrams = 20, MaxGrams = 80,
                DaysOfWeek = [DayOfWeek.Monday], FoodIds = [Pasta], FoodCategory = "Pasta",
            },
            2,
            new Dictionary<Guid, string> { [Pasta] = "Penne" });

        label.Should().Be("Cohort 2: Meal, 06:00-10:00, 20-80 g, Mon, foods Penne, food category Pasta");
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void A_patch_sets_every_panels_range_or_one_panels_parameters()
    {
        var spec = Json("""
            { "version": 1, "range": { "hours": 6 }, "panels": [
              { "id": "a", "dataset": { "name": "event_response", "cohorts": [ { "anchor": "Meal" }, { "anchor": "CorrectionBolus" } ] },
                "primitive": "range_bands", "range": { "hours": 720, "from": null, "to": null } },
              { "id": "b", "dataset": { "name": "glucose_statistics" }, "primitive": "table", "range": { "hours": 6 } } ] }
            """);

        var (ranged, _) = ReportSpecPatches.Apply(spec, Json("""{ "range": { "hours": 48 } }"""));
        ranged!.Value.GetProperty("panels").EnumerateArray().Select(p => p.GetProperty("range").GetProperty("hours").GetInt32())
            .Should().Equal(48, 48);

        var (toggled, errors) = ReportSpecPatches.Apply(spec, Json("""
            { "panels": [ { "id": "a", "dataset": { "cohorts": [ { "anchor": "Meal" } ] }, "range": { "from": "2026-09-01T00:00:00Z", "to": "2026-10-01T00:00:00Z" } } ] }
            """));
        errors.Should().BeEmpty();
        var validation = Validator.Validate(toggled!.Value);
        validation.Errors.Should().BeEmpty();
        validation.Spec!.Panels[0].Dataset.Should().BeOfType<EventResponseParameters>().Which.Cohorts.Should().ContainSingle();
        validation.Spec.Panels[0].Range!.From.Should().Be(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("""{ "panels": [ { "id": "zz" } ] }""", "patch.panels[0].id")]
    [InlineData("""{ "panels": [ { "id": "a", "dataset": { "name": "daily_profile" } } ] }""", "patch.panels[0].dataset.name")]
    [InlineData("""{ "colour": "red" }""", "patch.colour")]
    [InlineData("""{ "panels": "all" }""", "patch.panels")]
    public void A_patch_that_reaches_outside_the_spec_is_refused(string patch, string path)
    {
        var spec = Json("""{ "version": 1, "panels": [ { "id": "a", "dataset": { "name": "event_response" }, "primitive": "table" } ] }""");

        var (applied, errors) = ReportSpecPatches.Apply(spec, Json(patch));

        applied.Should().BeNull();
        errors.Select(e => e.Path).Should().Contain(path);
    }

    [Fact]
    public void An_event_panel_links_to_the_meals_and_events_report_with_its_cohorts()
    {
        var panel = new ReportPanelSpec
        {
            Id = "p1",
            Primitive = ReportPrimitive.RangeBands,
            Dataset = new EventResponseParameters
            {
                Cohorts = [new ReportEventCohort { Anchor = EventAnchorKind.Meal, Foods = [Pasta], DayOfWeek = [ReportWeekday.Mon] }],
                ExcludeOverrides = true,
            },
        };
        var result = new ReportPanelResult
        {
            Id = "p1", Dataset = "event_response", Primitive = ReportPrimitive.RangeBands, Bindings = new ReportBindings(),
            Status = ReportPanelStatus.Ok, Summary = "",
            Window = new ReportWindow(
                new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                new DateTimeOffset(2026, 10, 29, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()),
        };

        var link = ReportLinks.For("https://dev.nocturne.localhost", panel, result, "UTC");

        link.Should().StartWith("https://dev.nocturne.localhost/reports/loop-analysis/meals-and-events?from=2026-10-01&to=2026-10-28");
        link.Should().Contain("excludeOverrides=true").And.Contain("anchor1=Meal").And.Contain($"foodIds1={Pasta}").And.Contain("dayOfWeek1=1");
    }

    [Theory]
    [InlineData(24, false, "so the analysis covers that window and has no comparison.")]
    [InlineData(6, false, "only the part of the range in that window, 2026-10-30 18:00 to 2026-10-31 00:00 UTC, and has no comparison.")]
    [InlineData(0, true, "No data: the range ends before the last 24 hours")]
    public void A_clamped_summary_names_the_window_it_served(int hours, bool outside, string expected)
    {
        var to = new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc);
        var response = new LoopAnalysisResponse
        {
            HistoryClamped = true,
            RangeOutsideHistory = outside,
            PrimaryRange = new LoopAnalysisResolvedRange
            {
                StartDate = new DateOnly(2026, 10, 30), EndDate = new DateOnly(2026, 10, 30),
                From = to.AddHours(-hours), To = to,
            },
        };

        var lines = LoopAnalysisDatasetRun.Context(response, "Meals & events").ToList();

        lines.Should().ContainSingle(l => l.Contains(expected));
        lines.Any(l => l.StartsWith("Meals & events over local days")).Should().Be(!outside);
    }
}
