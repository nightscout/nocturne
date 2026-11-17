using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Nocturne.Core.Models.Tests;

[Trait("Category", "Unit")]
public class ProfileApnsEnvironmentTests
{
    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void Upload_KeepsTheApnsEnvironment(string json, bool expected)
    {
        var profile = JsonSerializer.Deserialize<Profile>($$"""{"isAPNSProduction":{{json}}}""");

        profile!.IsAPNSProduction.Should().Be(expected);
        JsonSerializer.Serialize(profile).Should().Contain($"\"isAPNSProduction\":{json}");
    }

    [Fact]
    public void Upload_WithoutTheFlag_StaysWithoutItOnTheWayOut()
    {
        var profile = JsonSerializer.Deserialize<Profile>("""{"defaultProfile":"Default"}""");

        profile!.IsAPNSProduction.Should().BeNull();
        JsonSerializer.Serialize(profile).Should().NotContain("isAPNSProduction");
    }
}
