using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Mappers;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Mappers;

[Trait("Category", "Unit")]
public class GlookoV4TreatmentMapperManualInsulinTests
{
    private readonly GlookoV4TreatmentMapper _mapper;

    public GlookoV4TreatmentMapperManualInsulinTests()
    {
        var logger = Mock.Of<ILogger>();
        var config = new GlookoConnectorConfiguration();
        var timeMapper = new GlookoTimeMapper(config, logger);
        _mapper = new GlookoV4TreatmentMapper("glooko-connector", timeMapper, logger);
    }

    #region MapV3ManualInsulin — gkInsulinBasal

    [Fact]
    public void MapV3ManualInsulin_GkInsulinBasal_CreatesBasalInjection()
    {
        var graphData = BuildGraphData(gkInsulinBasal: [
            new GlookoV3InsulinDataPoint { X = 1714300000, Value = 22, Name = "Tresiba®U100" }
        ]);

        var (basalInjections, boluses) = _mapper.MapV3ManualInsulin(graphData);

        basalInjections.Should().HaveCount(1);
        basalInjections[0].Units.Should().Be(22);
        basalInjections[0].DataSource.Should().Be("glooko-connector");
        basalInjections[0].LegacyId.Should().StartWith("glooko_");
        boluses.Should().BeEmpty();
    }

    [Fact]
    public void MapV3ManualInsulin_GkInsulinBasal_SkipsZeroUnits()
    {
        var graphData = BuildGraphData(gkInsulinBasal: [
            new GlookoV3InsulinDataPoint { X = 1714300000, Value = 0, Name = "Tresiba®U100" }
        ]);

        var (basalInjections, _) = _mapper.MapV3ManualInsulin(graphData);

        basalInjections.Should().BeEmpty();
    }

    [Fact]
    public void MapV3ManualInsulin_GkInsulinBasal_ResolvesLongActingInsulinContext()
    {
        var graphData = BuildGraphData(gkInsulinBasal: [
            new GlookoV3InsulinDataPoint { X = 1714300000, Value = 18, Name = "Tresiba®U100" }
        ]);

        var (basalInjections, _) = _mapper.MapV3ManualInsulin(graphData);

        var context = basalInjections[0].InsulinContext;
        context.Should().NotBeNull();
        context.InsulinName.Should().Contain("Tresiba");
        context.Dia.Should().Be(42.0);
        context.Curve.Should().Be("bilinear");
        context.Concentration.Should().Be(100);
    }

    [Fact]
    public void MapV3ManualInsulin_GkInsulinBasal_DeterministicLegacyId()
    {
        var graphData = BuildGraphData(gkInsulinBasal: [
            new GlookoV3InsulinDataPoint { X = 1714300000, Value = 22, Name = "Tresiba®U100" }
        ]);

        var (first, _) = _mapper.MapV3ManualInsulin(graphData);
        var (second, _) = _mapper.MapV3ManualInsulin(graphData);

        first[0].LegacyId.Should().Be(second[0].LegacyId);
    }

    #endregion

    #region MapV3ManualInsulin — gkInsulinBolus

    [Fact]
    public void MapV3ManualInsulin_GkInsulinBolus_CreatesBolus()
    {
        var graphData = BuildGraphData(gkInsulinBolus: [
            new GlookoV3InsulinDataPoint { X = 1714300000, Value = 4.5, Name = "Admelog®" }
        ]);

        var (basalInjections, boluses) = _mapper.MapV3ManualInsulin(graphData);

        boluses.Should().HaveCount(1);
        boluses[0].Insulin.Should().Be(4.5);
        boluses[0].BolusType.Should().Be(BolusType.Normal);
        boluses[0].Automatic.Should().BeFalse();
        boluses[0].InsulinType.Should().Be("Admelog®");
        boluses[0].DataSource.Should().Be("glooko-connector");
        boluses[0].LegacyId.Should().StartWith("glooko_");
        basalInjections.Should().BeEmpty();
    }

    [Fact]
    public void MapV3ManualInsulin_GkInsulinBolus_ResolvesRapidActingInsulinContext()
    {
        var graphData = BuildGraphData(gkInsulinBolus: [
            new GlookoV3InsulinDataPoint { X = 1714300000, Value = 3, Name = "Admelog®" }
        ]);

        var (_, boluses) = _mapper.MapV3ManualInsulin(graphData);

        var context = boluses[0].InsulinContext;
        context.Should().NotBeNull();
        context!.InsulinName.Should().Contain("Admelog");
        context.Dia.Should().Be(4.0);
        context.Curve.Should().Be("rapid-acting");
        context.Concentration.Should().Be(100);
    }

    [Fact]
    public void MapV3ManualInsulin_GkInsulinBolus_SkipsZeroUnits()
    {
        var graphData = BuildGraphData(gkInsulinBolus: [
            new GlookoV3InsulinDataPoint { X = 1714300000, Value = 0, Name = "Admelog®" }
        ]);

        var (_, boluses) = _mapper.MapV3ManualInsulin(graphData);

        boluses.Should().BeEmpty();
    }

    #endregion

    #region MapV3ManualInsulin — mixed

    [Fact]
    public void MapV3ManualInsulin_BothSeries_MapsIndependently()
    {
        var graphData = BuildGraphData(
            gkInsulinBasal: [
                new GlookoV3InsulinDataPoint { X = 1714300000, Value = 22, Name = "Tresiba®U100" }
            ],
            gkInsulinBolus: [
                new GlookoV3InsulinDataPoint { X = 1714301000, Value = 5, Name = "Admelog®" },
                new GlookoV3InsulinDataPoint { X = 1714302000, Value = 3, Name = "Admelog®" }
            ]);

        var (basalInjections, boluses) = _mapper.MapV3ManualInsulin(graphData);

        basalInjections.Should().HaveCount(1);
        boluses.Should().HaveCount(2);
    }

    [Fact]
    public void MapV3ManualInsulin_NullSeries_ReturnsEmpty()
    {
        var graphData = new GlookoV3GraphResponse { Series = null };

        var (basalInjections, boluses) = _mapper.MapV3ManualInsulin(graphData);

        basalInjections.Should().BeEmpty();
        boluses.Should().BeEmpty();
    }

    [Fact]
    public void MapV3ManualInsulin_EmptySeries_ReturnsEmpty()
    {
        var graphData = BuildGraphData();

        var (basalInjections, boluses) = _mapper.MapV3ManualInsulin(graphData);

        basalInjections.Should().BeEmpty();
        boluses.Should().BeEmpty();
    }

    #endregion

    #region ResolveInsulinContext — concentration variants

    [Fact]
    public void MapV3ManualInsulin_TresibaU200_MatchesU200Variant()
    {
        var graphData = BuildGraphData(gkInsulinBasal: [
            new GlookoV3InsulinDataPoint { X = 1714300000, Value = 30, Name = "Tresiba®U200" }
        ]);

        var (basalInjections, _) = _mapper.MapV3ManualInsulin(graphData);

        basalInjections[0].InsulinContext.Concentration.Should().Be(200);
        basalInjections[0].InsulinContext.InsulinName.Should().Contain("U200");
    }

    [Fact]
    public void MapV3ManualInsulin_TresibaU100_MatchesBaseVariant()
    {
        var graphData = BuildGraphData(gkInsulinBasal: [
            new GlookoV3InsulinDataPoint { X = 1714300000, Value = 30, Name = "Tresiba®U100" }
        ]);

        var (basalInjections, _) = _mapper.MapV3ManualInsulin(graphData);

        basalInjections[0].InsulinContext.Concentration.Should().Be(100);
        basalInjections[0].InsulinContext.InsulinName.Should().NotContain("U200");
    }

    [Fact]
    public void MapV3ManualInsulin_HumalogU200_MatchesU200Variant()
    {
        var graphData = BuildGraphData(gkInsulinBolus: [
            new GlookoV3InsulinDataPoint { X = 1714300000, Value = 5, Name = "Humalog®U200" }
        ]);

        var (_, boluses) = _mapper.MapV3ManualInsulin(graphData);

        boluses[0].InsulinContext!.Concentration.Should().Be(200);
    }

    [Fact]
    public void MapV3ManualInsulin_HumalogNoSuffix_MatchesBaseU100()
    {
        var graphData = BuildGraphData(gkInsulinBolus: [
            new GlookoV3InsulinDataPoint { X = 1714300000, Value = 5, Name = "Humalog®" }
        ]);

        var (_, boluses) = _mapper.MapV3ManualInsulin(graphData);

        boluses[0].InsulinContext!.Concentration.Should().Be(100);
    }

    [Fact]
    public void MapV3ManualInsulin_LyumjevU200_MatchesU200Variant()
    {
        var graphData = BuildGraphData(gkInsulinBolus: [
            new GlookoV3InsulinDataPoint { X = 1714300000, Value = 5, Name = "Lyumjev®U200" }
        ]);

        var (_, boluses) = _mapper.MapV3ManualInsulin(graphData);

        boluses[0].InsulinContext!.Concentration.Should().Be(200);
        boluses[0].InsulinContext!.Curve.Should().Be("ultra-rapid");
    }

    [Fact]
    public void MapV3ManualInsulin_HumulinRU500_MatchesU500Variant()
    {
        var graphData = BuildGraphData(gkInsulinBolus: [
            new GlookoV3InsulinDataPoint { X = 1714300000, Value = 10, Name = "Humulin®R U500" }
        ]);

        var (_, boluses) = _mapper.MapV3ManualInsulin(graphData);

        boluses[0].InsulinContext!.Concentration.Should().Be(500);
        boluses[0].InsulinContext!.Dia.Should().Be(5.0);
    }

    #endregion

    #region ResolveInsulinContext — unknown insulin fallback

    [Fact]
    public void MapV3ManualInsulin_UnknownInsulinName_FallsBackWithRawName()
    {
        var graphData = BuildGraphData(gkInsulinBasal: [
            new GlookoV3InsulinDataPoint { X = 1714300000, Value = 10, Name = "SomeNewInsulin" }
        ]);

        var (basalInjections, _) = _mapper.MapV3ManualInsulin(graphData);

        var context = basalInjections[0].InsulinContext;
        context.InsulinName.Should().Be("SomeNewInsulin");
        context.PatientInsulinId.Should().Be(Guid.Empty);
        // Should use long-acting defaults since primaryCategory is LongActing
        context.Curve.Should().Be("bilinear");
    }

    [Fact]
    public void MapV3ManualInsulin_NullInsulinName_FallsBackToUnknown()
    {
        var graphData = BuildGraphData(gkInsulinBasal: [
            new GlookoV3InsulinDataPoint { X = 1714300000, Value = 10, Name = null }
        ]);

        var (basalInjections, _) = _mapper.MapV3ManualInsulin(graphData);

        basalInjections[0].InsulinContext.InsulinName.Should().Be("Unknown");
    }

    #endregion

    #region ResolveInsulinContext — known insulins

    [Theory]
    [InlineData("Lantus®", "Lantus (Insulin Glargine)", 24.0, 100)]
    [InlineData("Levemir®", "Levemir (Insulin Detemir)", 18.0, 100)]
    [InlineData("Toujeo®", "Toujeo (Insulin Glargine)", 36.0, 300)]
    [InlineData("NovoRapid®", "NovoRapid (Insulin Aspart)", 4.0, 100)]
    [InlineData("Fiasp®", "Fiasp (Faster Aspart)", 3.5, 100)]
    public void MapV3ManualInsulin_KnownInsulins_MatchCorrectly(
        string glookoName, string expectedCatalogName, double expectedDia, int expectedConcentration)
    {
        // Use basal series for long-acting, bolus series for rapid-acting
        var isLongActing = expectedDia > 10;
        var graphData = isLongActing
            ? BuildGraphData(gkInsulinBasal: [new GlookoV3InsulinDataPoint { X = 1714300000, Value = 10, Name = glookoName }])
            : BuildGraphData(gkInsulinBolus: [new GlookoV3InsulinDataPoint { X = 1714300000, Value = 5, Name = glookoName }]);

        var (basalInjections, boluses) = _mapper.MapV3ManualInsulin(graphData);

        var context = isLongActing ? basalInjections[0].InsulinContext : boluses[0].InsulinContext!;
        context.InsulinName.Should().Be(expectedCatalogName);
        context.Dia.Should().Be(expectedDia);
        context.Concentration.Should().Be(expectedConcentration);
    }

    #endregion

    #region Helpers

    private static GlookoV3GraphResponse BuildGraphData(
        GlookoV3InsulinDataPoint[]? gkInsulinBasal = null,
        GlookoV3InsulinDataPoint[]? gkInsulinBolus = null)
    {
        return new GlookoV3GraphResponse
        {
            Series = new GlookoV3Series
            {
                GkInsulinBasal = gkInsulinBasal,
                GkInsulinBolus = gkInsulinBolus,
            }
        };
    }

    #endregion
}
