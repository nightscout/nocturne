using FluentAssertions;
using Nocturne.Core.Models;
using Nocturne.Widget.Contracts.Helpers;
using Xunit;

namespace Nocturne.Widget.Contracts.Tests.Helpers;

public class DirectionHelperTests
{
    [Theory]
    [InlineData("FortyFiveUp", "FORTYFIVEUP")]
    [InlineData("FORTY_FIVE_UP", "FORTYFIVEUP")]
    [InlineData("forty five up", "FORTYFIVEUP")]
    [InlineData("NONE", "NONE")]
    [InlineData("None", "NONE")]
    [InlineData("NOT COMPUTABLE", "NOTCOMPUTABLE")]
    [InlineData("NotComputable", "NOTCOMPUTABLE")]
    [InlineData("rate-out-of-range", "RATEOUTOFRANGE")]
    [InlineData("RATE OUT OF RANGE", "RATEOUTOFRANGE")]
    [InlineData("CGM ERROR", "CGMERROR")]
    [InlineData("CgmError", "CGMERROR")]
    public void Normalize_folds_casing_and_separator_variants(string direction, string expected)
    {
        DirectionHelper.Normalize(direction).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_yields_nothing_for_an_absent_direction(string? direction)
    {
        DirectionHelper.Normalize(direction).Should().BeEmpty();
    }

    [Theory]
    [InlineData("NONE")]
    [InlineData("None")]
    [InlineData("NOT COMPUTABLE")]
    [InlineData("NotComputable")]
    [InlineData("Sideways")]
    [InlineData("")]
    [InlineData(null)]
    public void A_direction_no_arrow_expresses_gets_the_unknown_mark(string? direction)
    {
        DirectionHelper.GetArrowText(direction).Should().Be(DirectionHelper.UnknownArrow);
        DirectionHelper.GetFluentGlyph(direction).Should().Be(DirectionHelper.FluentUnknownGlyph);
        DirectionHelper.GetFluentRotation(direction).Should().BeNull();
    }

    [Theory]
    [InlineData("RATE OUT OF RANGE")]
    [InlineData("RateOutOfRange")]
    [InlineData("CGM ERROR")]
    [InlineData("CgmError")]
    public void A_direction_the_CGM_reported_as_unusable_gets_the_warning_mark(string direction)
    {
        DirectionHelper.GetFluentGlyph(direction).Should().Be(DirectionHelper.FluentWarningGlyph);
        DirectionHelper.GetFluentRotation(direction).Should().BeNull();
        DirectionHelper.GetDirectionLabel(direction).Should().NotBe("Unknown");
    }

    [Theory]
    [InlineData("RATE OUT OF RANGE", "Rate out of range")]
    [InlineData("RateOutOfRange", "Rate out of range")]
    [InlineData("CGM ERROR", "Sensor error")]
    [InlineData("CgmError", "Sensor error")]
    [InlineData("NOT COMPUTABLE", "Not computable")]
    [InlineData("FORTY_FIVE_UP", "Rising slowly")]
    public void GetDirectionLabel_states_the_direction_the_CGM_reported(
        string direction,
        string expected)
    {
        DirectionHelper.GetDirectionLabel(direction).Should().Be(expected);
    }

    /// <summary>
    /// Every direction the backend can send must land on a mark chosen for it, in either spelling
    /// it arrives in. Only <see cref="Direction.Flat"/> may render as the stable arrow.
    /// </summary>
    [Fact]
    public void No_direction_but_Flat_renders_as_the_stable_one()
    {
        var directions = Enum.GetValues<Direction>();
        directions.Should().HaveCountGreaterThan(1);

        foreach (var direction in directions.Where(d => d != Direction.Flat))
        {
            foreach (var spelling in new[] { direction.ToString(), direction.ToWireString() })
            {
                DirectionHelper.GetArrowText(spelling)
                    .Should().NotBe(DirectionHelper.GetArrowText(nameof(Direction.Flat)), spelling);
                DirectionHelper.GetDirectionLabel(spelling)
                    .Should().NotBe(DirectionHelper.GetDirectionLabel(nameof(Direction.Flat)), spelling);
                DirectionHelper.GetFluentRotation(spelling)
                    .Should().NotBe(DirectionHelper.GetFluentRotation(nameof(Direction.Flat)), spelling);
            }
        }
    }
}
