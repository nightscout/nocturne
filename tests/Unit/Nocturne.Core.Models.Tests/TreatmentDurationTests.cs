using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Core.Models.Tests;

/// <summary>
/// AAPS parses treatment <c>duration</c> as a Long and throws
/// <c>NumberFormatException: Expected a long</c> on a fractional value, so the
/// serialized value must be a whole number of minutes. Rounding happens at
/// serialization, not in the getter — internal duration math (e.g. temp-basal
/// cutting in DDataService) relies on the exact sub-minute value.
/// </summary>
[Trait("Category", "Unit")]
public class TreatmentDurationTests
{
    [Fact]
    public void Duration_Getter_KeepsExactValue_ForInternalCalculations()
    {
        // Regression guard: DDataService.ProcessDurations cuts a temp basal to a fractional
        // sub-minute duration that is legitimately > 0 and filters on Duration > 0. Rounding
        // in the getter would collapse it to 0 and silently drop the treatment.
        new Treatment { Duration = 0.4833 }.Duration!.Value.Should().BeApproximately(0.4833, 1e-9);
        new Treatment { Insulin = 0.7492, Rate = 1.0 }.Duration!.Value.Should().BeApproximately(44.952, 1e-9);
    }

    [Fact]
    public void Duration_SerializesRoundedToWholeMinutes()
    {
        DurationInJson(new Treatment { Duration = 44.9666 }).Should().Be("45");
        DurationInJson(new Treatment { Insulin = 0.7492, Rate = 1.0 }).Should().Be("45");
    }

    [Fact]
    public void Duration_ComputedFractional_SerializesAsIntegerNotDouble()
    {
        var json = JsonSerializer.Serialize(new Treatment { Insulin = 0.7492, Rate = 1.0 });
        var duration = JsonSerializer.Deserialize<JsonElement>(json).GetProperty("duration");

        duration.GetRawText().Should().Be("45");
        duration.TryGetInt64(out _).Should().BeTrue();
    }

    [Fact]
    public void Duration_Unset_SerializesAsZero()
    {
        DurationInJson(new Treatment()).Should().Be("0");
    }

    private static string DurationInJson(Treatment treatment) =>
        JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(treatment))
            .GetProperty("duration")
            .GetRawText();
}
