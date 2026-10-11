using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Core.Models.Tests;

/// <summary>Pins the derivation <see cref="Treatment.Insulin"/> must not make.</summary>
[Trait("Category", "Unit")]
public class TreatmentInsulinTests
{
    [Fact]
    public void Insulin_IsNotDerivedFromRateAndDuration()
    {
        var tempBasal = new Treatment { EventType = "Temp Basal", Rate = 1.25, Absolute = 1.25, Duration = 30 };

        tempBasal.Insulin.Should().BeNull();
        tempBasal.Amount.Should().BeNull();
        var serialized = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(tempBasal));
        (serialized.TryGetProperty("insulin", out var insulin) && insulin.ValueKind != JsonValueKind.Null)
            .Should().BeFalse();
    }

    [Fact]
    public void Insulin_StillFallsBackToAmount()
    {
        new Treatment { Amount = 2.5 }.Insulin.Should().Be(2.5);
        new Treatment { Insulin = 1.5, Amount = 2.5 }.Insulin.Should().Be(1.5);
    }

    [Fact]
    public void Duration_IsStillDerivedFromInsulinAndRate()
    {
        new Treatment { Insulin = 0.5, Rate = 1.0 }.Duration.Should().Be(30);
    }
}
