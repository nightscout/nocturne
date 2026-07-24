using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Nocturne.Core.Models.Tests;

/// <summary>
/// AAPS's Gson models type these treatment fields as <c>Int</c>/<c>Long</c> and throw
/// <c>NumberFormatException</c> on a fractional value, crashing the sync loop (#522, #516).
/// Like <c>duration</c> (see <see cref="TreatmentDurationTests"/>), each must serialize as
/// a whole number while the in-memory value keeps its exact precision.
/// </summary>
[Trait("Category", "Unit")]
public class TreatmentAapsIntegerFieldTests
{
    public static TheoryData<string, Treatment> FractionalFieldCases =>
        new()
        {
            { "protein", new Treatment { Protein = 12.5 } },
            { "fat", new Treatment { Fat = 7.4 } },
            { "preBolus", new Treatment { PreBolus = 14.9666 } },
            { "splitNow", new Treatment { SplitNow = 33.3 } },
            { "splitExt", new Treatment { SplitExt = 66.7 } },
            { "percentage", new Treatment { Percentage = 109.5 } },
            { "timeshift", new Treatment { Timeshift = 1.5 } },
        };

    [Theory]
    [MemberData(nameof(FractionalFieldCases))]
    public void AapsIntegerField_Fractional_SerializesAsWholeNumber(
        string propertyName,
        Treatment treatment
    )
    {
        var json = JsonSerializer.Serialize(treatment);
        var property = JsonSerializer.Deserialize<JsonElement>(json).GetProperty(propertyName);

        property.TryGetInt64(out _).Should()
            .BeTrue($"AAPS parses {propertyName} as an integer type");
    }

    [Fact]
    public void AapsIntegerFields_InMemoryValues_KeepExactPrecision()
    {
        var treatment = new Treatment { Protein = 12.5, Timeshift = 1.5 };

        treatment.Protein.Should().Be(12.5);
        treatment.Timeshift.Should().Be(1.5);
    }
}
