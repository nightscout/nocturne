using FluentAssertions;
using Nocturne.API.Services.Platform.Releases;
using Xunit;

namespace Nocturne.API.Tests.Services.Platform.Releases;

public class ReleaseVersionTests
{
    [Theory]
    [InlineData("0.2.7", "0.2.7")]
    [InlineData("v0.2.7", "0.2.7")]
    [InlineData("1.2.3-rc.1", "1.2.3-rc.1")]
    [InlineData("1.2.3+abc123", "1.2.3")]
    public void ParsesReleaseTagsAndAssemblyVersions(string text, string expected)
    {
        ReleaseVersion.TryParse(text, out var version).Should().BeTrue();
        version.ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("alerts-native")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    public void RejectsWhatIsNotAThreePartVersion(string? text)
    {
        ReleaseVersion.TryParse(text, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("0.2.8", "0.2.7")]
    [InlineData("0.10.0", "0.9.9")]
    [InlineData("1.0.0", "1.0.0-rc.2")]
    [InlineData("1.0.0-rc.2", "1.0.0-rc.1")]
    [InlineData("1.0.0-rc.10", "1.0.0-rc.2")]
    [InlineData("1.0.0-rc", "1.0.0-1")]
    [InlineData("1.0.0-rc.1.1", "1.0.0-rc.1")]
    public void OrdersByCoreThenPutsAPreReleaseBeforeItsRelease(string newer, string older)
    {
        ReleaseVersion.TryParse(newer, out var a).Should().BeTrue();
        ReleaseVersion.TryParse(older, out var b).Should().BeTrue();

        (a > b).Should().BeTrue();
        (b < a).Should().BeTrue();
    }
}
