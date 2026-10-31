using System.Text.Json;
using System.Text.Json.Serialization;
using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Reports;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.LoopAnalysis;
using Xunit;
using static Nocturne.API.Tests.Reports.RecordDatasetTests;

namespace Nocturne.API.Tests.Reports;

/// <summary>
/// The report builder's catalogue against the registry the running API builds and the validator:
/// a dataset, primitive or parameter shape added to one and not the other fails here.
/// </summary>
public class ReportCatalogueTests(AuthenticationTestFactory factory) : IClassFixture<AuthenticationTestFactory>
{
    private IReportDatasetRegistry Registry => factory.Services.GetRequiredService<IReportDatasetRegistry>();

    private static CustomReportCatalogue Catalogue(bool useMmol = false, params string[] scopes) =>
        new ReportCatalogue(ReportSpecValidatorTests.Registry, Validator, null!, null!, null!, null!)
            .Build(new HashSet<string>(scopes), useMmol);

    /// <summary>
    /// Every shape of parameter the catalogue emits. The builder's parameter form draws exactly these
    /// (<c>schema-form.test.ts</c> pins the same list); a new shape fails here until the form draws it.
    /// </summary>
    internal static readonly string[] FormShapes =
    [
        "array<object>",
        "array<string:enum>",
        "array<string:uuid>:foods",
        "boolean",
        "integer",
        "integer:enum",
        "integer:minute-of-day",
        "number",
        "object",
        "string",
        "string:date",
        "string:enum",
        "string:food-category",
    ];

    [Fact]
    public void The_running_api_registers_every_dataset_the_parameter_union_names_and_no_other()
    {
        var derived = typeof(ReportDatasetParameters).GetCustomAttributes<JsonDerivedTypeAttribute>()
            .ToDictionary(a => (string)a.TypeDiscriminator!, a => a.DerivedType);

        derived.Keys.Should().BeEquivalentTo(Registry.All.Select(d => d.Name));
        ReportSpecValidatorTests.Registry.All.Select(d => d.Name).Should().BeEquivalentTo(Registry.All.Select(d => d.Name));
        foreach (var dataset in Registry.All)
        {
            derived[dataset.Name].Should().Be(dataset.ParametersType, dataset.Name);
            ((ReportDatasetParameters)Activator.CreateInstance(dataset.ParametersType)!).DatasetName.Should().Be(dataset.Name);
        }
    }

    [Fact]
    public void Every_dataset_is_listed_with_words_fields_and_primitives()
    {
        var catalogue = Catalogue(false, Scope.GlucoseRead);

        catalogue.MaxPanels.Should().Be(ReportSpec.MaxPanels);
        catalogue.MaxRangeHours.Should().Be(ReportSpec.MaxRangeHours);
        catalogue.Datasets.Select(d => d.Name).Should().BeEquivalentTo(ReportSpecValidatorTests.Registry.All.Select(d => d.Name));
        foreach (var dataset in catalogue.Datasets)
        {
            dataset.Label.Should().NotBeNullOrWhiteSpace(dataset.Name);
            dataset.Description.Should().NotBeNullOrWhiteSpace(dataset.Name);
            dataset.Fields.Select(f => f.Name).Should().OnlyHaveUniqueItems(dataset.Name);
            dataset.Fields.Should().OnlyContain(f => f.Label.Length > 0 && f.Description.Length > 0, dataset.Name);
            dataset.Primitives.Should().NotBeEmpty(dataset.Name);
            dataset.Primitives.Select(p => p.Name).Should().OnlyContain(p => ReportPrimitives.Parse(p) != null);
        }
    }

    [Fact]
    public void Every_default_binding_validates_and_names_only_the_fields_its_binding_may_name()
    {
        foreach (var dataset in Catalogue().Datasets)
        {
            foreach (var primitive in dataset.Primitives)
            {
                var at = $"{dataset.Name} as {primitive.Name}";
                var slots = primitive.Bindings.ToDictionary(s => s.Key);
                var defaults = primitive.DefaultBindings;
                void Within(string key, IEnumerable<string>? names)
                {
                    if (names is null)
                        return;
                    slots.Should().ContainKey(key, at);
                    names.Should().OnlyContain(n => slots[key].Fields.Contains(n), $"{at} {key}");
                }
                Within("x", defaults.X is null ? null : [defaults.X]);
                Within("y", defaults.Y);
                Within("band", defaults.Band);
                Within("series", defaults.Series is null ? null : [defaults.Series]);
                Within("columns", defaults.Columns);

                var spec = JsonSerializer.SerializeToElement(new
                {
                    version = 1,
                    panels = new[]
                    {
                        new
                        {
                            dataset = new { name = dataset.Name },
                            primitive = primitive.Name,
                            range = new { hours = (int)Math.Min(dataset.DefaultRangeHours, primitive.MaxRangeHours) },
                            bindings = defaults,
                        },
                    },
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
                Validator.Validate(spec).Errors.Should().BeEmpty(at);
            }
        }
    }

    [Fact]
    public void A_glucose_only_caller_sees_every_dataset_and_which_it_may_read()
    {
        var catalogue = Catalogue(false, Scope.GlucoseRead);

        catalogue.Datasets.Single(d => d.Name == "glucose_statistics").Permitted.Should().BeTrue();
        catalogue.Datasets.Single(d => d.Name == "treatments").Permitted.Should().BeFalse();
        var statistics = catalogue.Datasets.Single(d => d.Name == "glucose_statistics");
        statistics.Fields.Single(f => f.Name == "tdd").Permitted.Should().BeFalse("insulin per day needs treatments.read");
        statistics.Fields.Single(f => f.Name == "mean").Permitted.Should().BeTrue();
        Catalogue(false).Datasets.Should().OnlyContain(d => !d.Permitted, "no scope reads no dataset");
    }

    [Fact]
    public void Glucose_fields_are_in_the_tenants_unit()
    {
        Catalogue(useMmol: true).Datasets.Single(d => d.Name == "glucose_series").Fields.Single(f => f.Name == "glucose").Unit
            .Should().Be("mmol/L");
        Catalogue(useMmol: false).Datasets.Single(d => d.Name == "glucose_series").Fields.Single(f => f.Name == "glucose").Unit
            .Should().Be("mg/dL");
    }

    [Fact]
    public void Each_primitive_says_how_long_a_range_it_covers()
    {
        var chart = Catalogue().Datasets.Single(d => d.Name == "glucose_series").Primitives.Single(p => p.Name == "glucose_chart");

        chart.MaxRangeHours.Should().Be(72);
        chart.RangeLimit.Should().Be("This panel covers at most 3 days.");
    }

    [Fact]
    public void Every_parameter_object_refuses_keys_it_does_not_list_and_every_property_has_words()
    {
        var problems = new List<string>();
        void Walk(JsonElement node, string path)
        {
            if (node.ValueKind != JsonValueKind.Object)
                return;
            if (node.TryGetProperty("$ref", out _))
                problems.Add($"{path}: $ref");
            if (node.TryGetProperty("properties", out var properties))
            {
                if (!node.TryGetProperty("additionalProperties", out var additional) || additional.ValueKind != JsonValueKind.False)
                    problems.Add($"{path}: open");
                foreach (var property in properties.EnumerateObject())
                {
                    if (!property.Value.TryGetProperty("title", out _))
                        problems.Add($"{path}.{property.Name}: no title");
                    if (!property.Value.TryGetProperty("description", out _))
                        problems.Add($"{path}.{property.Name}: no description");
                    Walk(property.Value, $"{path}.{property.Name}");
                }
            }
            if (node.TryGetProperty("items", out var items))
                Walk(items, $"{path}[]");
        }

        foreach (var dataset in Catalogue().Datasets)
        {
            dataset.Parameters.GetProperty("type").GetString().Should().Be("object", dataset.Name);
            Walk(dataset.Parameters, dataset.Name);
        }

        problems.Should().BeEmpty();
    }

    [Fact]
    public void The_catalogue_emits_only_parameter_shapes_the_builder_form_draws()
    {
        var shapes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var dataset in Catalogue().Datasets)
            if (dataset.Parameters.TryGetProperty("properties", out var properties))
                foreach (var property in properties.EnumerateObject())
                    Collect(property.Value, shapes);

        shapes.Should().BeEquivalentTo(FormShapes);
    }

    private static void Collect(JsonElement node, ISet<string> shapes)
    {
        var shape = Shape(node);
        shapes.Add(shape);
        if (node.TryGetProperty("properties", out var properties))
            foreach (var property in properties.EnumerateObject())
                Collect(property.Value, shapes);
        if (node.TryGetProperty("items", out var items) && items.TryGetProperty("properties", out var itemProperties))
            foreach (var property in itemProperties.EnumerateObject())
                Collect(property.Value, shapes);
    }

    /// <summary>A node as the form tells it apart: its type, then enum, any format, and input kind.</summary>
    private static string Shape(JsonElement node)
    {
        var type = node.GetProperty("type").GetString()!;
        if (type == "array")
            return $"array<{Shape(node.GetProperty("items"))}>"
                + (node.TryGetProperty("x-input", out var listInput) ? $":{listInput.GetString()}" : "");
        var parts = new List<string> { type };
        if (node.TryGetProperty("enum", out _))
            parts.Add("enum");
        if (node.TryGetProperty("format", out var format))
            parts.Add(format.GetString()!);
        if (node.TryGetProperty("x-input", out var input))
            parts.Add(input.GetString()!);
        return string.Join(':', parts);
    }

    [Fact]
    public void The_schema_carries_the_limits_and_defaults_the_validator_uses()
    {
        var datasets = Catalogue().Datasets.ToDictionary(d => d.Name);
        JsonElement Property(string dataset, string name) =>
            datasets[dataset].Parameters.GetProperty("properties").GetProperty(name);

        var interval = Property("glucose_series", "intervalMinutes");
        interval.GetProperty("minimum").GetDouble().Should().Be(1);
        interval.GetProperty("maximum").GetDouble().Should().Be(60);
        interval.GetProperty("default").GetInt32().Should().Be(GlucoseSeriesParameters.DefaultIntervalMinutes);

        Property("event_response", "minutesBefore").GetProperty("enum").EnumerateArray().Select(v => v.GetInt32())
            .Should().Equal(EventResponseDefinition.MinutesBeforeChoices);
        Property("event_response", "minutesAfter").GetProperty("enum").EnumerateArray().Select(v => v.GetInt32())
            .Should().Equal(EventResponseDefinition.MinutesAfterChoices);
        Property("event_response", "excludeIllness").GetProperty("default").GetBoolean().Should().BeTrue();
        Property("event_response", "excludeOverrides").GetProperty("default").GetBoolean().Should().BeFalse();

        Property("event_response", "cohorts").GetProperty("items").GetProperty("title").GetString().Should().Be("Event group");
        var cohort = Property("event_response", "cohorts").GetProperty("items").GetProperty("properties");
        cohort.GetProperty("anchor").TryGetProperty("default", out _).Should().BeFalse("the event is required: a new group picks it");
        var from = cohort.GetProperty("timeOfDay").GetProperty("properties").GetProperty("fromMinute");
        from.GetProperty("x-input").GetString().Should().Be(ReportInputAttribute.MinuteOfDay);
        from.TryGetProperty("default", out _).Should().BeFalse("a required member's zero is no choice: 00:00 to 00:00 holds nothing");
        var overrides = Property("event_response", "excludeOverrides");
        overrides.GetProperty("x-scope").GetString().Should().Be(Scope.TreatmentsRead);
        overrides.GetProperty("description").GetString().Should().Be("Leave out time an override ran. Needs the treatments permission.");
        Property("event_response", "excludeOpenLoop").GetProperty("description").GetString().Should().EndWith("Needs the devices permission.");
        cohort.GetProperty("foods").GetProperty("x-input").GetString().Should().Be(ReportInputAttribute.Foods);
        cohort.GetProperty("foodCategory").GetProperty("x-input").GetString().Should().Be(ReportInputAttribute.FoodCategory);
        cohort.GetProperty("anchor").GetProperty("x-enumLabels").EnumerateArray().Select(l => l.GetString())
            .Should().Contain("Correction bolus");
        cohort.GetProperty("dayOfWeek").GetProperty("maxItems").GetInt32().Should().Be(7);
        Property("event_response", "cohorts").GetProperty("maxItems").GetInt32().Should().Be(EventResponseDefinition.MaxCohorts);

        Property("period_comparison", "preset").GetProperty("x-enumLabels").EnumerateArray().Select(l => l.GetString())
            .Should().Contain("Last 14 days and the 14 days before");
        Property("period_comparison", "a").GetProperty("properties").GetProperty("from").GetProperty("format").GetString()
            .Should().Be("date");
        Property("settings_evidence", "setting").GetProperty("x-enumLabels").EnumerateArray().Select(l => l.GetString())
            .Should().Equal("Basal", "ISF", "Carb ratio");
    }

    [Fact]
    public void A_nullable_parameter_is_written_as_its_type_without_null()
    {
        var treatments = Catalogue().Datasets.Single(d => d.Name == "treatments").Parameters.GetProperty("properties");

        treatments.GetProperty("minCarbs").GetProperty("type").GetString().Should().Be("number");
        treatments.GetProperty("types").GetProperty("items").GetProperty("enum").EnumerateArray()
            .Should().OnlyContain(v => v.ValueKind == JsonValueKind.String);
    }
}
