using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Core.Models.Tests;

[Trait("Category", "Unit")]
public class TreatmentRelativeTests
{
    [Theory]
    [InlineData("Temp Basal")]
    [InlineData("Temporary Basal")]
    [InlineData("TempBasal")]
    [InlineData("temp basal")]
    public void TempBasal_DoesNotSerializeItsRateAsExtendedBolusInsulin(string eventType)
    {
        var treatment = new Treatment { EventType = eventType, Absolute = 1.25, Duration = 30 };

        treatment.Rate.Should().Be(1.25);
        treatment.Relative.Should().BeNull();
        var json = JsonSerializer.SerializeToElement(treatment);
        (json.TryGetProperty("relative", out var relative) && relative.ValueKind != JsonValueKind.Null)
            .Should().BeFalse();
        json.GetProperty("absolute").GetDouble().Should().Be(1.25);
    }

    [Fact]
    public void TempBasal_DoesNotReserializeAPollutedRelativeField()
    {
        var treatment = JsonSerializer.Deserialize<Treatment>(
            """{"eventType":"Temp Basal","absolute":5,"relative":5,"duration":30}""")!;

        treatment.Relative.Should().BeNull();
        treatment.Absolute.Should().Be(5);
        treatment.Duration.Should().Be(30);
    }

    [Fact]
    public void ComboBolus_KeepsItsExplicitRateAndRateFallback()
    {
        new Treatment { EventType = "Combo Bolus", Relative = 0.5, Rate = 1.25 }
            .Relative.Should().Be(0.5);
        new Treatment { EventType = "Combo Bolus", Rate = 0.5 }
            .Relative.Should().Be(0.5);
    }
}
