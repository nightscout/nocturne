using FluentAssertions;
using Nocturne.Core.Constants;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Core.Models.Tests.V4;

public class GlucoseUnitsTests
{
    [Theory]
    [InlineData("mmol", GlucoseUnit.Mmol)]
    [InlineData("mmol/l", GlucoseUnit.Mmol)]
    [InlineData("mmol/L", GlucoseUnit.Mmol)]
    [InlineData("MMOL", GlucoseUnit.Mmol)]
    [InlineData("MMOL/L", GlucoseUnit.Mmol)]
    [InlineData("mmoll", GlucoseUnit.Mmol)]
    [InlineData("mmol l", GlucoseUnit.Mmol)]
    [InlineData(" mmol/L ", GlucoseUnit.Mmol)]
    [InlineData("Mmol", GlucoseUnit.Mmol)]
    [InlineData("mg/dl", GlucoseUnit.MgDl)]
    [InlineData("mg/dL", GlucoseUnit.MgDl)]
    [InlineData("MG/DL", GlucoseUnit.MgDl)]
    [InlineData("mgdl", GlucoseUnit.MgDl)]
    [InlineData("MGDL", GlucoseUnit.MgDl)]
    [InlineData("mg", GlucoseUnit.MgDl)]
    [InlineData(" mg/dL ", GlucoseUnit.MgDl)]
    [InlineData("MgDl", GlucoseUnit.MgDl)]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData(" ", null)]
    [InlineData("\t", null)]
    [InlineData("g/L", null)]
    [InlineData("0", null)]
    [InlineData("1", null)]
    [InlineData("MgDl, Mmol", null)]
    [InlineData("mmol/mol", null)]
    [InlineData("mmol/liter", null)]
    [InlineData("mmol/litre", null)]
    [InlineData("mmol/dL", null)]
    [InlineData("mg/L", null)]
    [InlineData("mmol L-1", GlucoseUnit.Mmol)]
    [InlineData("mmol per litre", null)]
    [InlineData("percent", null)]
    public void Parse_ReadsTheLettersAndNothingElse(string? units, GlucoseUnit? expected)
    {
        GlucoseUnits.Parse(units).Should().Be(expected);
    }

    [Theory]
    [InlineData(5.0, GlucoseUnit.Mmol, 5.0 * GlucoseConstants.MgdlPerMmol)]
    [InlineData(0.0, GlucoseUnit.Mmol, 0.0)]
    [InlineData(99.4, GlucoseUnit.MgDl, 99.4)]
    [InlineData(5.5, GlucoseUnit.MgDl, 5.5)]
    public void ToMgdl_MultipliesMmolAndLeavesMgdlUnrounded(double value, GlucoseUnit unit, double expected)
    {
        GlucoseUnits.ToMgdl(value, unit).Should().Be(expected);
    }

    [Theory]
    [InlineData(99.4, GlucoseUnit.MgDl, 99)]
    [InlineData(99.6, GlucoseUnit.MgDl, 100)]
    [InlineData(99.5, GlucoseUnit.MgDl, 100)]
    [InlineData(100.5, GlucoseUnit.MgDl, 100)]
    [InlineData(90, GlucoseUnit.Mmol, 5.0)]
    [InlineData(113, GlucoseUnit.Mmol, 6.3)]
    [InlineData(63, GlucoseUnit.Mmol, 3.5)]
    [InlineData(180, GlucoseUnit.Mmol, 10.0)]
    public void FromMgdl_ShowsWholeMgdlOrMmolToOneDecimal(double mgdl, GlucoseUnit unit, double expected)
    {
        GlucoseUnits.FromMgdl(mgdl, unit).Should().Be(expected);
    }
}
