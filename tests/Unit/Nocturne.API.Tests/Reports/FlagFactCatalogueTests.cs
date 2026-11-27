using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Reports;
using Nocturne.Core.Models.Authorization;
using Xunit;
using static Nocturne.API.Tests.Reports.RecordDatasetTests;

namespace Nocturne.API.Tests.Reports;

/// <summary>The flag facts the reports catalogue lists, and the conditions the evaluator refuses before it reads anything.</summary>
[Trait("Category", "Unit")]
public class FlagFactCatalogueTests
{
    private static CustomReportFlagFact Fact(CustomReportCatalogue catalogue, string dataset, string field) =>
        catalogue.FlagFacts.Should().ContainSingle(f => f.Dataset == dataset && f.Field.Name == field, $"{dataset}.{field}").Subject;

    [Fact]
    public void The_catalogue_lists_each_flag_fact_with_its_label_unit_kind_range_family_and_scope()
    {
        var catalogue = ReportCatalogueTests.Catalogue(true, Scope.GlucoseRead);

        var below = Fact(catalogue, "glucose_statistics", "timeBelow");
        (below.Field.Label, below.Field.Unit, below.Kind, below.Range, below.MaxDays)
            .Should().Be(("Time below consensus range", "%", "period", "consensus", 90));
        below.Scopes.Should().Equal([Scope.GlucoseRead, Scope.ReportsRead], "a period fact needs reports.read, as the Review lens does");

        var ownBelow = Fact(catalogue, "glucose_statistics", "timeBelowOwnRange");
        (ownBelow.Field.Label, ownBelow.Kind, ownBelow.Range).Should().Be(("Time below own range", "period", "own"));
        Fact(catalogue, "glucose_statistics", "mean").Range.Should().BeNull("a figure not against a target range has no family");
        ownBelow.Field.Label.Should().NotBe(below.Field.Label, "a reader tells the consensus and own range facts apart");
        ownBelow.Scopes.Should().Equal([Scope.GlucoseRead, Scope.TherapyRead, Scope.ReportsRead], "the target range needs therapy.read");
        (ownBelow.Field.Scope, ownBelow.Field.Permitted).Should().Be((Scope.TherapyRead, false));

        Fact(catalogue, "glucose_statistics", "mean").Field.Unit.Should().Be("mg/dL", "a rule's glucose value is in mg/dL whatever the tenant shows");
        Fact(catalogue, "glucose_statistics", "cv").Field.Permitted.Should().BeTrue();

        var tdd = Fact(catalogue, "glucose_statistics", "tdd");
        (tdd.Field.Scope, tdd.Field.Permitted).Should().Be((Scope.TreatmentsRead, false));

        var reservoir = Fact(catalogue, "device_status", "reservoir");
        (reservoir.Field.Unit, reservoir.Kind, reservoir.MaxDays, reservoir.Field.Permitted)
            .Should().Be(("U", "live", (int?)null, false));
        reservoir.Scopes.Should().Equal([Scope.DevicesRead], "a live fact needs only its dataset's scope, like the fixed set");

        var sensor = Fact(catalogue, "current_status", "sensorHoursLeft");
        (sensor.Field.Label, sensor.Field.Unit, sensor.Kind, sensor.Field.Scope)
            .Should().Be(("Sensor hours left", "h", "live", Scope.AlertsRead));
        sensor.Scopes.Should().Equal([Scope.GlucoseRead, Scope.AlertsRead]);
        Fact(catalogue, "current_status", "siteHoursLeft").Kind.Should().Be("live");
        Fact(catalogue, "current_status", "hoursSinceReading").Kind.Should().Be("live");
        Fact(catalogue, "device_status", "sensorAgeHours").Kind.Should().Be("live");
        Fact(catalogue, "glucose_statistics", "sensorWornPercent").Kind.Should().Be("period");
    }

    [Fact]
    public void Sensor_worn_is_a_period_fact_with_no_coverage_so_a_low_figure_can_match()
    {
        var worn = FlagFacts.Of(Registry.Find("glucose_statistics")!).Single(f => f.Name == "sensorWornPercent");

        worn.FlagFact.Should().Be(ReportFlagFact.Period);
        FlagFacts.Of(Registry.Find("glucose_statistics")!).Single(f => f.Name == "timeBelow").FlagFact!.CoverageField
            .Should().Be("sensorWornPercent");
    }

    [Fact]
    public void A_series_a_category_a_time_or_a_count_of_readings_is_not_a_fact()
    {
        var catalogue = ReportCatalogueTests.Catalogue(false, Scope.FullAccess);

        catalogue.FlagFacts.Should().NotContain(f => f.Dataset == "glucose_series");
        catalogue.FlagFacts.Should().NotContain(f => f.Dataset == "device_status" && f.Field.Name == "time");
        catalogue.FlagFacts.Should().NotContain(f => f.Field.Name == "readings" || f.Field.Name == "ownRangeVaries" || f.Field.Name == "targetLow");
        catalogue.FlagFacts.Should().OnlyContain(f => f.Field.Kind == "number" || f.Field.Kind == "percent");
    }

    [Fact]
    public void Every_flag_fact_is_a_field_of_its_datasets_figures_row()
    {
        foreach (var dataset in ReportSpecValidatorTests.Registry.All)
        {
            var tiles = dataset.PanelKinds.Contains(ReportPanelKind.StatTiles) ? dataset.FieldsFor(ReportPanelKind.StatTiles) : [];
            foreach (var field in dataset.FieldsFor(ReportPanelKind.Chart).Concat(dataset.FieldsFor(ReportPanelKind.Table)).Where(f => f.FlagFact is not null))
                tiles.Should().Contain(t => t.Name == field.Name && t.FlagFact != null, $"{dataset.Name}.{field.Name} is marked but no figures row offers it");
        }
    }

    private static readonly IReportDatasetRegistry Registry = ReportSpecValidatorTests.Registry;

    private static FlagCondition Condition(string dataset, string field, int? days, double value, string? unit) =>
        new(JsonDocument.Parse(dataset).RootElement, field, days, FlagComparison.Above, value, unit);

    private static ReportSpecError Refused(FlagCondition condition)
    {
        var (checkedCondition, errors) = FlagFactCondition.Check(condition, Validator, Registry);
        checkedCondition.Should().BeNull();
        return errors.Should().ContainSingle(string.Join("; ", errors)).Subject;
    }

    [Fact]
    public void A_condition_on_a_fact_over_up_to_90_days_is_accepted_with_its_unit_in_mg_dl()
    {
        var (condition, errors) = FlagFactCondition.Check(
            Condition("""{ "name": "glucose_statistics" }""", "mean", 90, 180, "mg/dL"), Validator, Registry);

        errors.Should().BeEmpty();
        condition!.Field.Unit.Should().Be("mg/dL");
        condition.Fact.Kind.Should().Be(ReportFlagFactKind.Period);
    }

    [Fact]
    public void A_range_over_90_days_is_refused() =>
        Refused(Condition("""{ "name": "glucose_statistics" }""", "timeBelow", 91, 4, "%")).Path.Should().Be("days");

    [Fact]
    public void A_field_that_is_not_a_fact_is_refused()
    {
        Refused(Condition("""{ "name": "glucose_statistics" }""", "readings", 14, 100, null)).Path.Should().Be("field");
        Refused(Condition("""{ "name": "glucose_statistics" }""", "nothing", 14, 100, null)).Path.Should().Be("field");
    }

    [Fact]
    public void A_dataset_with_no_facts_and_an_unknown_dataset_are_refused()
    {
        Refused(Condition("""{ "name": "glucose_series" }""", "glucose", 14, 100, "mg/dL")).Path.Should().Be("dataset.name");
        Refused(Condition("""{ "name": "nope" }""", "timeBelow", 14, 4, "%")).Path.Should().Be("dataset.name");
    }

    [Fact]
    public void A_glucose_fact_in_another_unit_than_mg_dl_is_refused()
    {
        var error = Refused(Condition("""{ "name": "glucose_statistics" }""", "mean", 14, 10, "mmol/L"));
        error.Path.Should().Be("unit");
        error.Message.Should().Contain("mg/dL");
    }

    [Fact]
    public void A_period_fact_needs_a_range_and_a_live_fact_takes_none()
    {
        Refused(Condition("""{ "name": "glucose_statistics" }""", "cv", null, 36, "%")).Path.Should().Be("days");
        Refused(Condition("""{ "name": "current_status" }""", "hoursSinceReading", 14, 1, "h")).Path.Should().Be("days");
    }
}
