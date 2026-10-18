using FluentAssertions;
using Nocturne.Connectors.Glooko.Xt;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Xt;

public class GlookoXtProvenanceTests
{
    [Fact]
    public void PlainText_IsNotProvenance()
    {
        GlookoXtProvenance.TryParse("after walk").Should().BeNull();
        GlookoXtProvenance.TryParse("{not json").Should().BeNull();
        GlookoXtProvenance.TryParse("""{"note":"no source key"}""").Should().BeNull();
        GlookoXtProvenance.TryParse(null).Should().BeNull();
    }

    [Fact]
    public void SourceBlob_ParsesSourceAndAlarm()
    {
        var p = GlookoXtProvenance.TryParse("""{"source":"CamAPS Sync","detail":{"raw_alarm":"alarm_occlusion"}}""")!;

        p.Source.Should().Be("CamAPS Sync");
        p.IsClosedLoop.Should().BeTrue();
        p.RawAlarm.Should().Be("alarm_occlusion");
        p.Wizard.Should().BeNull();
    }

    [Fact]
    public void WizardBlob_ParsesTheFigures()
    {
        var p = GlookoXtProvenance.TryParse("""
            {"source":"CamAPS Sync","detail":{"total_value":3,"suggestion_overridden":"yes"},"type":"wizard","recommended":{"carb":2.5,"net":3,"correction":0.5},"bgInput":"180","carbInput":25}
            """)!;

        p.Type.Should().Be("wizard");
        var w = p.Wizard!;
        w.Should().NotBeNull();
        w.Total.Should().Be(3);
        w.RecommendedCarb.Should().Be(2.5);
        w.RecommendedNet.Should().Be(3);
        w.RecommendedCorrection.Should().Be(0.5);
        w.BgInput.Should().Be(180);
        w.CarbInput.Should().Be(25);
        w.Overridden.Should().BeTrue();
    }
}
