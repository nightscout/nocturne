using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Nocturne.API.Mcp.Reports;
using Nocturne.API.Mcp.Tools;
using Nocturne.API.Tests.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Mcp;

/// <summary>
/// <c>render_report</c>'s input schema against the registry the running API builds: a dataset or
/// primitive added to one and not the other fails here.
/// </summary>
public class ReportSpecSchemaTests(AuthenticationTestFactory factory) : IClassFixture<AuthenticationTestFactory>
{
    private JsonElement RegisteredSchema()
    {
        var tools = factory.Services.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection!;
        tools.TryGetPrimitive(ReportTools.RenderReportName, out var tool).Should().BeTrue();
        return tool!.ProtocolTool.InputSchema;
    }

    private IReportDatasetRegistry Registry => factory.Services.GetRequiredService<IReportDatasetRegistry>();

    private static JsonElement Panel(JsonElement schema) =>
        schema.GetProperty("properties").GetProperty("spec").GetProperty("properties")
            .GetProperty("panels").GetProperty("items").GetProperty("properties");

    [Fact]
    public void The_dataset_union_lists_every_registered_dataset_and_no_other()
    {
        var union = Panel(RegisteredSchema()).GetProperty("dataset").GetProperty("anyOf").EnumerateArray().ToList();

        union.Select(branch => branch.GetProperty("properties").GetProperty("name").GetProperty("const").GetString())
            .Should().BeEquivalentTo(Registry.All.Select(d => d.Name));
    }

    [Fact]
    public void Each_registered_dataset_binds_to_the_parameter_type_its_name_selects()
    {
        var derived = typeof(ReportDatasetParameters).GetCustomAttributes<JsonDerivedTypeAttribute>()
            .ToDictionary(a => (string)a.TypeDiscriminator!, a => a.DerivedType);

        derived.Keys.Should().BeEquivalentTo(Registry.All.Select(d => d.Name));
        foreach (var dataset in Registry.All)
        {
            derived[dataset.Name].Should().Be(dataset.ParametersType, dataset.Name);
            ((ReportDatasetParameters)Activator.CreateInstance(dataset.ParametersType)!).DatasetName.Should().Be(dataset.Name);
        }
    }

    [Fact]
    public void The_primitive_enum_lists_every_primitive()
    {
        Panel(RegisteredSchema()).GetProperty("primitive").GetProperty("enum").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(ReportPrimitives.All.Select(p => p.WireName()));
    }

    [Fact]
    public void The_schema_marks_what_the_validator_requires()
    {
        var spec = RegisteredSchema().GetProperty("properties").GetProperty("spec");
        spec.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo("version", "panels");
        spec.GetProperty("properties").GetProperty("panels").GetProperty("items").GetProperty("required")
            .EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo("dataset", "primitive");
    }

    [Fact]
    public void Every_object_in_the_spec_refuses_keys_it_does_not_list()
    {
        var open = new List<string>();
        void Walk(JsonElement node, string path)
        {
            if (node.ValueKind != JsonValueKind.Object)
                return;
            if (node.TryGetProperty("properties", out var properties))
            {
                if (!node.TryGetProperty("additionalProperties", out var additional) || additional.ValueKind != JsonValueKind.False)
                    open.Add(path);
                foreach (var property in properties.EnumerateObject())
                    Walk(property.Value, $"{path}.{property.Name}");
            }
            foreach (var key in new[] { "items", "anyOf", "oneOf" })
            {
                if (!node.TryGetProperty(key, out var child))
                    continue;
                if (child.ValueKind == JsonValueKind.Array)
                    foreach (var branch in child.EnumerateArray())
                        Walk(branch, path);
                else
                    Walk(child, path);
            }
        }

        Walk(RegisteredSchema().GetProperty("properties").GetProperty("spec"), "spec");

        open.Should().BeEmpty();
    }

    [Fact]
    public void The_schema_carries_the_limits_the_validator_enforces()
    {
        var spec = RegisteredSchema().GetProperty("properties").GetProperty("spec");
        var properties = spec.GetProperty("properties");
        var panel = Panel(RegisteredSchema());

        properties.GetProperty("version").GetProperty("const").GetInt32().Should().Be(ReportSpec.CurrentVersion);
        properties.GetProperty("title").GetProperty("maxLength").GetInt32().Should().Be(ReportSpec.MaxTitleLength);
        properties.GetProperty("panels").GetProperty("minItems").GetInt32().Should().Be(1);
        properties.GetProperty("panels").GetProperty("maxItems").GetInt32().Should().Be(ReportSpec.MaxPanels);

        var hours = properties.GetProperty("range").GetProperty("properties").GetProperty("hours");
        hours.GetProperty("minimum").GetInt32().Should().Be(1);
        hours.GetProperty("maximum").GetInt32().Should().Be(ReportSpec.MaxRangeHours);
        ReportSpec.MaxRangeHours.Should().Be((int)Registry.All.SelectMany(d => d.AllowedPrimitives.Select(d.MaxRange)).Max().TotalHours,
            "the schema's ceiling is the longest any dataset allows; each dataset checks its own");

        panel.GetProperty("id").GetProperty("pattern").GetString().Should().Be(ReportSpec.IdPattern);
        panel.GetProperty("id").GetProperty("maxLength").GetInt32().Should().Be(ReportSpec.MaxIdLength);
        panel.GetProperty("title").GetProperty("maxLength").GetInt32().Should().Be(ReportSpec.MaxTitleLength);

        var series = panel.GetProperty("dataset").GetProperty("anyOf").EnumerateArray()
            .Single(b => b.GetProperty("properties").GetProperty("name").GetProperty("const").GetString() == "glucose_series");
        var interval = series.GetProperty("properties").GetProperty("intervalMinutes");
        interval.GetProperty("minimum").GetInt32().Should().Be(1);
        interval.GetProperty("maximum").GetInt32().Should().Be(60);

        var bindings = panel.GetProperty("bindings").GetProperty("properties");
        foreach (var list in new[] { "y", "band", "columns" })
            bindings.GetProperty(list).GetProperty("type").EnumerateArray().Select(t => t.GetString()).Should().NotContain("string", list);
        bindings.GetProperty("x").GetProperty("type").EnumerateArray().Select(t => t.GetString()).Should().NotContain("array");
    }

    [Fact]
    public void Every_dataset_allows_only_known_primitives_and_lists_its_fields_once()
    {
        foreach (var dataset in Registry.All)
        {
            dataset.AllowedPrimitives.Should().NotBeEmpty().And.OnlyContain(p => ReportPrimitives.All.Contains(p));
            dataset.Fields.Select(f => f.Name).Should().OnlyHaveUniqueItems(dataset.Name);
            dataset.Fields.Should().OnlyContain(f => f.Description.Length > 0 && f.Label.Length > 0);
            dataset.SampleRows.Should().OnlyContain(row => row.Keys.All(k => dataset.Fields.Any(f => f.Name == k)));
        }
    }

    [Fact]
    public void Every_property_of_the_spec_is_described()
    {
        var undescribed = new List<string>();
        void Walk(JsonElement node, string path)
        {
            if (node.ValueKind != JsonValueKind.Object)
                return;
            if (node.TryGetProperty("properties", out var properties))
            {
                foreach (var property in properties.EnumerateObject())
                {
                    if (property.Name != "name" && !property.Value.TryGetProperty("description", out _))
                        undescribed.Add($"{path}.{property.Name}");
                    Walk(property.Value, $"{path}.{property.Name}");
                }
            }
            foreach (var key in new[] { "items", "anyOf", "oneOf" })
            {
                if (!node.TryGetProperty(key, out var child))
                    continue;
                if (child.ValueKind == JsonValueKind.Array)
                    foreach (var branch in child.EnumerateArray())
                        Walk(branch, path);
                else
                    Walk(child, path);
            }
        }

        Walk(RegisteredSchema(), "$");

        undescribed.Should().BeEmpty();
    }

    [Fact]
    public void Each_dataset_parameter_type_is_described()
    {
        foreach (var dataset in Registry.All)
            dataset.ParametersType.GetCustomAttribute<DescriptionAttribute>().Should().NotBeNull(dataset.Name);
    }

    [Fact]
    public void Describe_dataset_returns_the_catalogue_entry_with_a_parameter_schema()
    {
        var description = DatasetTools.Describe(Registry.Find("glucose_series")!, useMmol: true);

        description.Fields.Single(f => f.Name == "glucose").Unit.Should().Be("mmol/L");
        description.Parameters.GetProperty("properties").GetProperty("intervalMinutes").GetProperty("type").GetString()
            .Should().Be("integer");
        description.Primitives.Should().Contain(new DatasetPrimitive(ReportPrimitive.GlucoseChart, 72));
        description.SampleRows.Should().HaveCount(3);
        description.SampleRows[0]["glucose"].Should().Be(6.2, "the sample is written in mg/dL and shown in the tenant's unit");
    }
}
