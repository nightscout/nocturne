using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.Core.Models.Tests.Configuration;

[Trait("Category", "Unit")]
public class HaloDialConfigTests
{
    [Fact]
    public void Defaults_MatchDocumentedShape()
    {
        var config = new HaloDialConfig();

        config.SchemaVersion.Should().Be(1);
        config.ColorMode.Should().Be(HaloDialColorMode.Discrete);
        config.HistoryMinutes.Should().Be(15);
        config.PredictionMinutes.Should().Be(45);
        config.PredictionCurve.Should().Be(HaloDialPredictionCurve.Main);
        config.CenterSub.Should().Be(HaloDialCenterSubElement.MinutesAndDelta);
        config.InnerLeftArc.Should().Be(HaloDialArcElement.Cob);
        config.InnerRightArc.Should().Be(HaloDialArcElement.Iob);
        config.IobMaxUnits.Should().Be(8.0);
        config.CobMaxGrams.Should().Be(80.0);

        config.Corners.Tl.Should().BeEmpty();
        config.Corners.Tr.Should().Equal(HaloDialCornerElement.LoopDot);
        config.Corners.Bl.Should().BeEmpty();
        config.Corners.Br.Should().Equal(
            HaloDialCornerElement.Direction,
            HaloDialCornerElement.Eventual,
            HaloDialCornerElement.LoopLabel);

        config.ElementConfig.Should().BeEmpty();
    }

    [Fact]
    public void RoundTrip_PreservesEnumNamesAsStrings()
    {
        var original = new HaloDialConfig
        {
            ColorMode = HaloDialColorMode.Continuous,
            PredictionCurve = HaloDialPredictionCurve.ZeroTemp,
            CenterSub = HaloDialCenterSubElement.None,
        };

        var json = JsonSerializer.Serialize(original);

        json.Should().Contain("\"Continuous\"");
        json.Should().Contain("\"ZeroTemp\"");
        json.Should().Contain("\"None\"");

        var roundTripped = JsonSerializer.Deserialize<HaloDialConfig>(json)!;

        roundTripped.ColorMode.Should().Be(HaloDialColorMode.Continuous);
        roundTripped.PredictionCurve.Should().Be(HaloDialPredictionCurve.ZeroTemp);
        roundTripped.CenterSub.Should().Be(HaloDialCenterSubElement.None);
    }

    [Fact]
    public void RoundTrip_PreservesCornerStacks()
    {
        var original = new HaloDialConfig();
        original.Corners.Tl.AddRange(new[]
        {
            HaloDialCornerElement.SensorAge,
            HaloDialCornerElement.PumpSiteAge,
        });

        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<HaloDialConfig>(json)!;

        roundTripped.Corners.Tl.Should().Equal(
            HaloDialCornerElement.SensorAge,
            HaloDialCornerElement.PumpSiteAge);
        roundTripped.Corners.Tr.Should().Equal(HaloDialCornerElement.LoopDot);
    }
}
