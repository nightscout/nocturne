using FluentAssertions;
using Nocturne.API.Services.Monitoring;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Services.Monitoring;

[Trait("Category", "Unit")]
public class TrackerCoverageTests
{
    [Fact]
    public void Parse_SkipsNamesThatAreNotCapabilities()
    {
        TrackerCoverage.Parse("""["Site","Drive","reservoir","1","Site,Power"]""")
            .Should().BeEquivalentTo([PumpCapability.Site, PumpCapability.Reservoir]);
        TrackerCoverage.Parse("not json").Should().BeEmpty();
        TrackerCoverage.Parse(null).Should().BeEmpty();
    }

    [Fact]
    public void TryNormalise_DropsBlanksAndOrdersTheList()
    {
        TrackerCoverage.TryNormalise(["", "power", "Site", "Site"], out var covers, out var error).Should().BeTrue();

        error.Should().BeNull();
        covers.Should().Equal(PumpCapability.Site, PumpCapability.Power);
    }

    [Theory]
    [InlineData("Drive")]
    [InlineData("7")]
    [InlineData("Site,Power")]
    public void TryNormalise_RejectsWhatIsNotACapability(string name)
    {
        TrackerCoverage.TryNormalise([name], out _, out var error).Should().BeFalse();

        error.Should().Contain(name);
    }

    [Fact]
    public void TryNormalise_KeepsNullForNoList()
    {
        TrackerCoverage.TryNormalise(null, out var covers, out _).Should().BeTrue();

        covers.Should().BeNull();
    }

    [Fact]
    public void APodTrackerStatingNothing_CoversWhatAPodProvides()
    {
        TrackerCoverage.Of(new TrackerDefinitionEntity { Category = TrackerCategory.Pod })
            .Should().BeEquivalentTo(TrackerCoverage.PodCapabilities);
        TrackerCoverage.Of(new TrackerDefinitionEntity { Category = TrackerCategory.Pod, Covers = """["Site"]""" })
            .Should().BeEquivalentTo([PumpCapability.Site]);
        TrackerCoverage.Of(new TrackerDefinitionEntity { Category = TrackerCategory.Cannula }).Should().BeEmpty();
    }

    [Theory]
    [InlineData(TrackerCategory.Reservoir, "[]", true)]
    [InlineData(TrackerCategory.Pod, "[]", true)]
    [InlineData(TrackerCategory.Pod, """["Site"]""", false)]
    [InlineData(TrackerCategory.Consumable, """["Reservoir"]""", true)]
    [InlineData(TrackerCategory.Cannula, "[]", false)]
    public void HoldsInsulin_FollowsTheReservoirCapability(TrackerCategory category, string covers, bool holds)
    {
        TrackerCoverage.HoldsInsulin(new TrackerDefinitionEntity { Category = category, Covers = covers })
            .Should().Be(holds);
    }
}
