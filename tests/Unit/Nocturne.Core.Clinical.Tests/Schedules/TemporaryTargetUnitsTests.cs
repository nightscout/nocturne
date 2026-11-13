using Nocturne.Core.Clinical.Schedules;
using Nocturne.Core.Constants;

namespace Nocturne.Core.Clinical.Tests.Schedules;

public class TemporaryTargetUnitsTests
{
    [Fact]
    public void TheExactMmolLabelConvertsWhateverTheBounds()
    {
        TemporaryTargetUnits.IsMmol(140, 120, "mmol").Should().BeTrue();
    }

    [Theory]
    [InlineData("mmol/L")]
    [InlineData("MMOL")]
    [InlineData("mg/dl")]
    [InlineData(null)]
    public void AnyOtherLabelWithMgdlBoundsIsNotConverted(string? units)
    {
        TemporaryTargetUnits.IsMmol(140, 120, units).Should().BeFalse();
    }

    [Theory]
    [InlineData("mg/dl")]
    [InlineData("mmol/L")]
    [InlineData(null)]
    public void ABoundBelowTwentyIsMmolWhateverTheLabel(string? units)
    {
        TemporaryTargetUnits.IsMmol(8, 100, units).Should().BeTrue();
        TemporaryTargetUnits.IsMmol(120, 6, units).Should().BeTrue();
    }

    [Fact]
    public void TheCeilingIsExclusive()
    {
        TemporaryTargetUnits.IsMmol(20, 20, "mg/dl").Should().BeFalse();
        TemporaryTargetUnits.IsMmol(19.9, 20, "mg/dl").Should().BeTrue();
    }

    [Fact]
    public void ToMgdl_ConvertsAnMmolTargetAndLeavesMgdlAlone()
    {
        var (top, bottom) = TemporaryTargetUnits.ToMgdl(10, 5, "mmol");
        top.Should().Be(10 * GlucoseConstants.MgdlPerMmol);
        bottom.Should().Be(5 * GlucoseConstants.MgdlPerMmol);

        TemporaryTargetUnits.ToMgdl(140, 110, "mmol/L").Should().Be((140.0, 110.0));
    }
}
