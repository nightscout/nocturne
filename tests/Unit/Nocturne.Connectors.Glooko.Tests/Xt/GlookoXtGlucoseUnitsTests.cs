using FluentAssertions;
using Nocturne.Connectors.Glooko.Xt;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Xt;

public class GlookoXtGlucoseUnitsTests
{
    [Theory]
    [InlineData("mgdl", GlookoXtGlucoseUnits.Unit.MgDl)]
    [InlineData("MG/DL", GlookoXtGlucoseUnits.Unit.MgDl)]
    [InlineData("mmol", GlookoXtGlucoseUnits.Unit.Mmol)]
    [InlineData("mmol/L", GlookoXtGlucoseUnits.Unit.Mmol)]
    public void Resolve_HonoursAnExplicitSetting_WhateverTheValuesSay(string setting, GlookoXtGlucoseUnits.Unit expected)
    {
        GlookoXtGlucoseUnits.Resolve(setting, [6.7, 120]).Should().Be(expected);
    }

    [Fact]
    public void Resolve_Auto_CallsABatchWithAnyLargeValueMgdl()
    {
        GlookoXtGlucoseUnits.Resolve("Auto", [5.5, 6.7, 120]).Should().Be(GlookoXtGlucoseUnits.Unit.MgDl);
    }

    [Fact]
    public void Resolve_Auto_CallsABatchOfSmallValuesMmol()
    {
        GlookoXtGlucoseUnits.Resolve("Auto", [5.5, 6.7, 12.1]).Should().Be(GlookoXtGlucoseUnits.Unit.Mmol);
    }

    [Fact]
    public void Resolve_Auto_WithNothingToConvert_IsMgdl()
    {
        GlookoXtGlucoseUnits.Resolve(null, []).Should().Be(GlookoXtGlucoseUnits.Unit.MgDl);
    }

    [Fact]
    public void Effective_AutoTakesTheAccountUnit_ExplicitStands()
    {
        GlookoXtGlucoseUnits.Effective("Auto", GlookoXtGlucoseUnitSetting.Mmol).Should().Be("mmol");
        GlookoXtGlucoseUnits.Effective("mgdl", GlookoXtGlucoseUnitSetting.Mmol).Should().Be("mgdl");
        GlookoXtGlucoseUnits.Effective("Auto", null).Should().Be("Auto");
    }

    [Fact]
    public void ToMgdl_ConvertsMmolToWholeMgdl()
    {
        GlookoXtGlucoseUnits.ToMgdl(6.7, GlookoXtGlucoseUnits.Unit.Mmol).Should().Be(121);
        GlookoXtGlucoseUnits.ToMgdl(120.4, GlookoXtGlucoseUnits.Unit.MgDl).Should().Be(120);
    }
}
