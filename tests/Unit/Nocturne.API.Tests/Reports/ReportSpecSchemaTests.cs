using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Nocturne.API.Reports;
using Nocturne.API.Reports.Derived;
using Xunit;

namespace Nocturne.API.Tests.Reports;

/// <summary>The spec's polymorphic unions against the registries they name: one added to the one and not the other fails here.</summary>
public class ReportSpecSchemaTests
{
    [Fact]
    public void The_derived_union_names_every_registered_op_and_no_other()
    {
        var derived = typeof(ReportDerivedSpec).GetCustomAttributes<JsonDerivedTypeAttribute>()
            .ToDictionary(a => (string)a.TypeDiscriminator!, a => a.DerivedType);

        derived.Keys.Should().BeEquivalentTo(ReportDerivedOps.All.Select(o => o.Name));
        foreach (var op in ReportDerivedOps.All)
        {
            derived[op.Name].Should().Be(op.SpecType, op.Name);
            ((ReportDerivedSpec)Activator.CreateInstance(op.SpecType)!).OpName.Should().Be(op.Name);
            (op.SourceCount == 2).Should().Be(typeof(ReportPairedSpec).IsAssignableFrom(op.SpecType), op.Name);
        }
    }

    [Fact]
    public void A_normalised_derived_layer_round_trips_through_its_json()
    {
        var spec = new TrendSpec
        {
            Confidence = true,
            A = new ReportSourceSpec
            {
                Derived = new DifferenceSpec
                {
                    A = new ReportSourceSpec { Dataset = new GlucoseSeriesParameters(), Field = "glucose" },
                    B = new ReportSourceSpec { Dataset = new GlucoseSeriesParameters(), Field = "targetHigh" },
                },
                Field = "value",
            },
        };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

        var json = JsonSerializer.Serialize<ReportDerivedSpec>(spec, options);

        json.Should().Be("""{"op":"trend","confidence":true,"a":{"derived":{"op":"difference","b":{"dataset":{"name":"glucose_series"},"field":"targetHigh"},"a":{"dataset":{"name":"glucose_series"},"field":"glucose"}},"field":"value"}}""");
        JsonSerializer.Serialize(JsonSerializer.Deserialize<ReportDerivedSpec>(json, options), options).Should().Be(json);
    }
}
