using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Nocturne.Core.Models.Tests.Entries;

public class EntryGlucoseMgdlTests
{
    [Fact]
    public void Mgdl_WinsOverSgv_WhenTheyDiffer()
    {
        new Entry { Mgdl = 113, Sgv = 90 }.GlucoseMgdl().Should().Be(113);
    }

    [Fact]
    public void Sgv_IsUsed_WhenMgdlIsUnset()
    {
        new Entry { Mgdl = 0, Sgv = 90 }.GlucoseMgdl().Should().Be(90);
    }

    [Fact]
    public void IsZero_WhenNeitherIsSet()
    {
        new Entry().GlucoseMgdl().Should().Be(0);
    }

    [Fact]
    public void IsNotSerialised()
    {
        var json = JsonSerializer.Serialize(new Entry { Mgdl = 113 });

        json.Should().NotContain("GlucoseMgdl");
    }
}
