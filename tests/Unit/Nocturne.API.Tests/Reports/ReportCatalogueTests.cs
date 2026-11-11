using System.Text.Json;
using System.Text.Json.Serialization;
using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Reports;
using Nocturne.API.Reports.Derived;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.LoopAnalysis;
using Xunit;
using static Nocturne.API.Tests.Reports.RecordDatasetTests;

namespace Nocturne.API.Tests.Reports;

/// <summary>
/// The report builder's catalogue against the registry the running API builds and the validator:
/// a dataset, mark, op or parameter shape added to one and not the other fails here.
/// </summary>
public class ReportCatalogueTests(AuthenticationTestFactory factory) : IClassFixture<AuthenticationTestFactory>
{
    private IReportDatasetRegistry Registry => factory.Services.GetRequiredService<IReportDatasetRegistry>();

    internal static CustomReportCatalogue Catalogue(bool useMmol = false, params string[] scopes) =>
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
    public void Every_dataset_is_listed_with_words_fields_kinds_and_marks()
    {
        var catalogue = Catalogue(false, Scope.GlucoseRead);

        catalogue.MaxPanels.Should().Be(ReportSpec.MaxPanels);
        catalogue.MaxTracks.Should().Be(4);
        catalogue.MaxLayers.Should().Be(8);
        catalogue.MaxUnitsPerTrack.Should().Be(2);
        catalogue.MaxReads.Should().Be(16);
        catalogue.MaxRangeHours.Should().Be(ReportSpec.MaxRangeHours);
        catalogue.PanelKinds.Select(k => k.Name).Should().Equal("chart", "stat_tiles", "table", "comparison");
        catalogue.Datasets.Select(d => d.Name).Should().BeEquivalentTo(ReportSpecValidatorTests.Registry.All.Select(d => d.Name));
        foreach (var dataset in catalogue.Datasets)
        {
            dataset.Label.Should().NotBeNullOrWhiteSpace(dataset.Name);
            dataset.Description.Should().NotBeNullOrWhiteSpace(dataset.Name);
            dataset.Fields.Select(f => f.Name).Should().OnlyHaveUniqueItems(dataset.Name);
            dataset.Fields.Should().OnlyContain(f => f.Label.Length > 0 && f.Description.Length > 0, dataset.Name);
            (dataset.Marks.Count + dataset.PanelKinds.Count).Should().BePositive(dataset.Name);
            dataset.Marks.Select(m => m.Name).Should().OnlyContain(m => ReportWire.Parse<ReportMark>(m) != null);
            (dataset.Marks.Count == 0 || dataset.XKinds.Count > 0).Should().BeTrue($"{dataset.Name} names the axes its layers lie along");
            dataset.RangeLimit.Should().StartWith("This data covers at most");
        }
    }

    [Fact]
    public void Every_number_field_a_chart_can_bind_says_how_it_folds()
    {
        foreach (var dataset in ReportSpecValidatorTests.Registry.All.Where(d => d.Marks.Count > 0))
            dataset.FieldsFor(ReportPanelKind.Chart).Where(f => f.IsNumber && !f.Companion)
                .Should().OnlyContain(f => f.Aggregate != null, dataset.Name);

        var fields = Catalogue().Datasets.ToDictionary(d => d.Name, d => d.Fields.ToDictionary(f => f.Name));
        fields["treatments"]["insulin"].Aggregate.Should().Be("sum");
        fields["treatments"]["insulin"].Marker.Should().Be("bolus");
        fields["treatments"]["bgCheck"].Marker.Should().Be("bg_check");
        fields["treatments"]["injectionUnits"].Marker.Should().Be("basal_injection");
        fields["basal_delivery"]["delivered"].Aggregate.Should().Be("last");
        fields["device_status"]["reservoir"].Aggregate.Should().Be("last");
        fields["glucose_series"]["glucose"].Aggregate.Should().Be("mean");
        fields["glucose_series"]["iob"].Aggregate.Should().Be("mean");
        fields["glucose_series"]["direction"].Aggregate.Should().BeNull();
        fields["daily_summary"]["day"].Kind.Should().Be("day");
    }

    [Fact]
    public void Every_mark_lists_its_slots_axes_and_whether_it_takes_an_axis()
    {
        var marks = Catalogue().Marks.ToDictionary(m => m.Name);

        marks.Keys.Should().Equal("line", "area", "band", "bars", "points", "markers", "spans");
        marks["markers"].HasAxis.Should().BeFalse();
        marks["spans"].HasAxis.Should().BeFalse();
        marks["line"].HasAxis.Should().BeTrue();
        marks["band"].Slots.Single(s => s.Key == "edges").Counts.Should().Equal(2, 4);
        marks["spans"].Slots.Select(s => s.Key).Should().Equal("start", "end", "category");
        marks["markers"].XKinds.Should().Equal("time");
        marks["bars"].XKinds.Should().Contain("category");
        marks["points"].XKinds.Should().Contain(["value", "lag"]);
        marks["band"].XKinds.Should().NotContain("category");
    }

    [Fact]
    public void Every_op_is_listed_with_its_settings_rules_and_marks()
    {
        var ops = Catalogue().Ops.ToDictionary(o => o.Name);

        ops.Keys.Should().Equal("difference", "ratio", "scatter", "lag_correlation", "rolling_mean", "trend", "band_sd");
        ops["scatter"].Sources.Should().Be(2);
        ops["trend"].Sources.Should().Be(1);
        ops["scatter"].XRule.Should().Be("Values of source a.");
        ops["lag_correlation"].SourceXKinds.Should().Equal("time");
        ops["ratio"].UnitRule.Should().Contain("over the unit of b");
        ops["scatter"].Marks.Should().ContainSingle().Which.DefaultBindings.Should().BeEquivalentTo(
            new CustomReportLayerBindings("a", ["b"], null, null, "fit", null, null, null, null, null));
        ops["trend"].Marks.Select(m => m.Name).Should().Equal("line", "band");
        ops["rolling_mean"].Parameters.GetProperty("properties").GetProperty("minutes").GetProperty("minimum").GetDouble().Should().Be(15);
        ops["rolling_mean"].Parameters.GetProperty("properties").TryGetProperty("a", out _).Should().BeFalse("the sources are drawn as forms of their own");
        ops["trend"].Parameters.GetProperty("properties").GetProperty("confidence").GetProperty("default").GetBoolean().Should().BeFalse();
        ops.Values.Should().OnlyContain(o => o.SourceFieldKinds.SequenceEqual(new[] { "number", "percent" }));
    }

    [Fact]
    public void Every_default_binding_validates_and_names_only_the_fields_its_binding_may_name()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        foreach (var dataset in Catalogue().Datasets)
        {
            foreach (var mark in dataset.Marks)
            {
                var at = $"{dataset.Name} as {mark.Name}";
                var slots = mark.Bindings.ToDictionary(s => s.Key);
                var defaults = mark.DefaultBindings;
                void Within(string key, IEnumerable<string>? names)
                {
                    if (names is null)
                        return;
                    slots.Should().ContainKey(key, at);
                    names.Should().OnlyContain(n => slots[key].Fields.Contains(n), $"{at} {key}");
                }
                Within("x", defaults.X is null ? null : [defaults.X]);
                Within("y", defaults.Y);
                Within("edges", defaults.Edges);
                Within("series", defaults.Series is null ? null : [defaults.Series]);
                Within("start", defaults.Start is null ? null : [defaults.Start]);
                Within("end", defaults.End is null ? null : [defaults.End]);
                Within("category", defaults.Category is null ? null : [defaults.Category]);

                var spec = JsonSerializer.SerializeToElement(new
                {
                    version = 1,
                    panels = new[]
                    {
                        new
                        {
                            kind = "chart",
                            range = new { hours = (int)Math.Min(dataset.DefaultRangeHours, dataset.MaxRangeHours) },
                            tracks = new[] { new { layers = new[] { new { dataset = new { name = dataset.Name }, mark = mark.Name, bindings = defaults } } } },
                        },
                    },
                }, options);
                Validator.Validate(spec).Errors.Should().BeEmpty(at);
            }

            foreach (var kind in dataset.PanelKinds)
            {
                var spec = JsonSerializer.SerializeToElement(new
                {
                    version = 1,
                    panels = new[] { new { kind = kind.Name, dataset = new { name = dataset.Name }, bindings = new { columns = kind.Columns } } },
                }, options);
                Validator.Validate(spec).Errors.Should().BeEmpty($"{dataset.Name} as {kind.Name}");
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
    public void Each_dataset_says_how_long_a_range_it_covers()
    {
        var treatments = Catalogue().Datasets.Single(d => d.Name == "treatments");

        treatments.MaxRangeHours.Should().Be(14 * 24);
        treatments.RangeLimit.Should().Be("This data covers at most 14 days.");
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
        foreach (var op in Catalogue().Ops)
            Walk(op.Parameters, op.Name);

        problems.Should().BeEmpty();
    }

    [Fact]
    public void The_catalogue_emits_only_parameter_shapes_the_builder_form_draws()
    {
        var shapes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var parameters in Catalogue().Datasets.Select(d => d.Parameters).Concat(Catalogue().Ops.Select(o => o.Parameters)))
            if (parameters.TryGetProperty("properties", out var properties))
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

        Property("daily_profile", "dayOfWeek").GetProperty("maxItems").GetInt32().Should().Be(7);
        Property("glucose_distribution_by_hour", "dayOfWeek").GetProperty("items").GetProperty("enum").GetArrayLength().Should().Be(7);
        Property("state_spans", "kinds").GetProperty("items").GetProperty("x-enumLabels").EnumerateArray().Select(l => l.GetString())
            .Should().Equal("Pump modes", "Profiles", "Overrides", "Activities", "Temp basals");

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
        Property("event_response", "cohorts").GetProperty("maxItems").GetInt32().Should().Be(EventResponseParameters.MaxGroups);

        Property("period_comparison", "preset").GetProperty("x-enumLabels").EnumerateArray().Select(l => l.GetString())
            .Should().Contain("Last 14 days and the 14 days before");
        Property("period_comparison", "a").GetProperty("properties").GetProperty("from").GetProperty("format").GetString()
            .Should().Be("date");
        Property("settings_evidence", "setting").GetProperty("x-enumLabels").EnumerateArray().Select(l => l.GetString())
            .Should().Equal("Basal", "ISF", "Carb ratio");
    }

    [Fact]
    public void An_event_group_offers_all_others_and_its_conditions_with_the_glucose_in_mg_dl()
    {
        JsonElement Cohort(bool useMmol) => Catalogue(useMmol).Datasets.Single(d => d.Name == "event_response").Parameters
            .GetProperty("properties").GetProperty("cohorts").GetProperty("items").GetProperty("properties");

        var cohort = Cohort(useMmol: false);
        cohort.GetProperty("others").GetProperty("type").GetString().Should().Be("boolean");
        cohort.GetProperty("others").GetProperty("default").GetBoolean().Should().BeFalse();
        cohort.GetProperty("others").GetProperty("title").GetString().Should().Be("All others");
        var since = cohort.GetProperty("minutesSinceMeal");
        since.GetProperty("x-scope").GetString().Should().Be(Scope.TreatmentsRead);
        since.GetProperty("properties").GetProperty("min").GetProperty("type").GetString().Should().Be("integer");
        since.GetProperty("properties").GetProperty("min").GetProperty("maximum").GetDouble().Should().Be(1440);
        cohort.GetProperty("insulinOnBoard").GetProperty("x-scope").GetString().Should().Be(Scope.TreatmentsRead);
        cohort.GetProperty("insulinOnBoard").GetProperty("properties").GetProperty("min").GetProperty("minimum").GetDouble().Should().Be(-20);
        cohort.GetProperty("startGlucose").TryGetProperty("x-scope", out _).Should().BeFalse("it reads glucose, which the dataset needs anyway");

        var mgdl = cohort.GetProperty("startGlucose").GetProperty("properties").GetProperty("min");
        mgdl.GetProperty("type").GetString().Should().Be("integer");
        mgdl.GetProperty("description").GetString().Should().Be("Lowest glucose at the event, mg/dL, 0 to 600. Optional.");
        mgdl.GetProperty("maximum").GetDouble().Should().Be(600);
        Cohort(useMmol: true).GetProperty("startGlucose").GetRawText().Should().Be(cohort.GetProperty("startGlucose").GetRawText(),
            "the bound is mg/dL for every tenant");

        cohort.GetProperty("anchor").GetProperty("enum").EnumerateArray().Select(v => v.GetString()).Should().Contain(["LowStart", "HighStart"]);
        cohort.GetProperty("anchor").GetProperty("x-enumLabels").EnumerateArray().Select(v => v.GetString()).Should().Contain(["Low start", "High start"]);
    }

    [Fact]
    public void A_nullable_parameter_is_written_as_its_type_without_null()
    {
        var treatments = Catalogue().Datasets.Single(d => d.Name == "treatments").Parameters.GetProperty("properties");

        treatments.GetProperty("minCarbs").GetProperty("type").GetString().Should().Be("number");
        treatments.GetProperty("types").GetProperty("items").GetProperty("enum").EnumerateArray()
            .Should().OnlyContain(v => v.ValueKind == JsonValueKind.String);
    }

    [Theory]
    [MemberData(nameof(OpNames))]
    public void An_ops_first_mark_draws_it_with_its_default_settings(string name)
    {
        var op = Catalogue().Ops.Single(o => o.Name == name);
        var sources = op.Sources == 2
            ? new { a = new { dataset = new { name = "glucose_series" }, field = "glucoseMgdl" }, b = (object?)new { dataset = new { name = "glucose_series" }, field = "glucose" } }
            : new { a = new { dataset = new { name = "glucose_series" }, field = "glucose" }, b = (object?)null };
        var derived = new Dictionary<string, object?> { ["op"] = name, ["a"] = sources.a };
        if (sources.b is not null)
            derived["b"] = sources.b;
        var spec = JsonSerializer.SerializeToElement(new
        {
            version = 1,
            panels = new[]
            {
                new { kind = "chart", range = new { hours = 24 }, grid = 15, tracks = new[] { new { layers = new[] { new { derived, mark = op.Marks[0].Name } } } } },
            },
        });

        Validator.Validate(spec).Errors.Should().BeEmpty($"{name} drawn as {op.Marks[0].Name}");
    }

    [Fact]
    public void Each_op_names_the_axis_its_layer_lies_along_for_each_axis_of_its_sources()
    {
        var ops = Catalogue().Ops.ToDictionary(o => o.Name);

        ops["scatter"].OutputXKinds.Should().OnlyContain(k => k == "value");
        ops["lag_correlation"].OutputXKinds.Should().Equal("lag");
        ops["rolling_mean"].OutputXKinds.Should().Equal(ops["rolling_mean"].SourceXKinds);
    }

    public static TheoryData<string> OpNames => [.. ReportDerivedOps.All.Select(o => o.Name)];

    /// <summary>Words in a field's name, label or description that say it may be a setting, or worked out from one.</summary>
    private static readonly System.Text.RegularExpressions.Regex SettingWords = new(
        "profile|schedul|target|limit|isf|ratio", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Fields those words match that are data, each with why.</summary>
    private static readonly Dictionary<string, string> NotSettings = new()
    {
        ["glucose_statistics.timeInRangePercent"] = "The share of the readings inside the consensus range: a figure of the readings.",
        ["glucose_statistics.timeBelow"] = "The share of the readings below the consensus range: a figure of the readings.",
        ["glucose_statistics.timeAbove"] = "The share of the readings above the consensus range: a figure of the readings.",
        ["glucose_statistics.cv"] = "The readings' coefficient of variation; its description names the consensus target it is read against.",
        ["settings_evidence.isfObserved"] = "The ISF the readings and boluses showed, not the profile's; the profile's is isfProfile.",
        ["settings_evidence.carbRatioObserved"] = "The carb ratio the readings and meals showed, not the profile's; the profile's is carbRatioProfile.",
        ["treatments.durationMinutes"] = "How long a temp basal or override ran: \"ratio\" is only a part of \"duration\".",
    };

    [Fact]
    public void Every_field_that_names_a_setting_is_marked_as_one_unless_listed_as_data()
    {
        var unmarked = ReportSpecValidatorTests.Registry.All
            .SelectMany(d => d.Fields.Select(f => (Key: $"{d.Name}.{f.Name}", Field: f)))
            .Where(x => x.Field.Setting == ReportSetting.None && !x.Field.Companion && x.Field.IsNumber)
            .Where(x => SettingWords.IsMatch(x.Field.Name) || SettingWords.IsMatch(x.Field.Label) || SettingWords.IsMatch(x.Field.Description))
            .Select(x => x.Key)
            .Where(key => !NotSettings.ContainsKey(key))
            .ToList();

        unmarked.Should().BeEmpty("a field worked out from the profile's settings is never a derived source");
        NotSettings.Keys.Should().OnlyContain(key => ReportSpecValidatorTests.Registry.All.Any(d => key.StartsWith(d.Name + ".", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_setting_is_marked_in_the_catalogue()
    {
        var settings = Catalogue().Datasets.SelectMany(d => d.Fields.Where(f => f.Setting).Select(f => $"{d.Name}.{f.Name}")).ToList();

        settings.Should().Contain(["daily_profile.netLoopCorrection", "event_response.netLoopCorrectionP50", "glucose_series.targetLow"]);
    }

    [Fact]
    public void A_dataset_whose_rows_come_in_groups_names_its_group_field()
    {
        var datasets = Catalogue().Datasets;

        datasets.Single(d => d.Name == "event_response").SeriesField.Should().Be("cohort");
        datasets.Single(d => d.Name == "event_response").SeriesParameter.Should().Be("cohorts");
        datasets.Where(d => d.Name != "event_response").Should().OnlyContain(d => d.SeriesField == null);
    }
}
