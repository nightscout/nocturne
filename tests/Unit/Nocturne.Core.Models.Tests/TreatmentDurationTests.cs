using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Core.Models.Tests;

/// <summary>
/// AAPS parses treatment <c>duration</c> as a Long and throws
/// <c>NumberFormatException: Expected a long</c> on a fractional value, so the
/// getter must always resolve to a whole number of minutes.
/// </summary>
[Trait("Category", "Unit")]
public class TreatmentDurationTests
{
    [Fact]
    public void Duration_ComputedFromInsulinAndRate_IsRoundedToWholeMinutes()
    {
        // (0.75 / 1.0) * 60 = 45; but e.g. 0.7492 / 1.0 * 60 = 44.952 -> must round.
        var treatment = new Treatment { Insulin = 0.7492, Rate = 1.0 };

        treatment.Duration!.Value.Should().Be(45);
    }

    [Fact]
    public void Duration_ExplicitFractionalValue_IsRounded()
    {
        var treatment = new Treatment { Duration = 44.9666 };

        treatment.Duration!.Value.Should().Be(45);
    }

    [Fact]
    public void Duration_ComputedFractional_SerializesWithoutDecimalPoint()
    {
        var treatment = new Treatment { Insulin = 0.7492, Rate = 1.0 };

        var json = JsonSerializer.Serialize(treatment);
        var doc = JsonSerializer.Deserialize<JsonElement>(json);

        var duration = doc.GetProperty("duration");
        duration.GetRawText().Should().Be("45");
        duration.TryGetInt64(out _).Should().BeTrue();
    }

    [Fact]
    public void Duration_Unset_DefaultsToZero()
    {
        new Treatment().Duration!.Value.Should().Be(0);
    }
}
