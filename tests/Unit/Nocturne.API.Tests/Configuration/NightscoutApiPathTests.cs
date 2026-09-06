using Microsoft.AspNetCore.Http;
using Nocturne.API.Configuration;

namespace Nocturne.API.Tests.Configuration;

/// <summary>
/// Pins which paths count as Nightscout-compatible, which decides both the error envelope shape
/// and — through <see cref="NightscoutJsonFilter"/> — the serializer every v1-v3 response uses.
/// </summary>
[Trait("Category", "Unit")]
public class NightscoutApiPathTests
{
    [Theory]
    [InlineData("/api/v1/x", 1)]
    [InlineData("/api/v2/x", 2)]
    [InlineData("/api/v3/x", 3)]
    [InlineData("/api/v3/", 3)]
    [InlineData("/API/V1/x", 1)]
    [InlineData("/api/v4/x", null)]
    [InlineData("/api/v10/", null)]
    [InlineData("/api/v1", null)]
    [InlineData("//api/v1/", null)]
    [InlineData("/api/v1x/y", null)]
    [InlineData("/scalar/v1/x", null)]
    [InlineData("/", null)]
    [InlineData("", null)]
    public void Version_ClassifiesThePath(string path, int? expected) =>
        NightscoutApiPath.Version(new PathString(path)).Should().Be(expected);

    [Fact]
    public void Version_PathWithNoValue_IsNotNightscout() =>
        NightscoutApiPath.Version(default).Should().BeNull();

    /// <summary>
    /// The comparison is ordinal, so a version segment padded with an ignorable character is not
    /// Nightscout. Endpoint routing compares segments ordinally too, so such a request never
    /// reaches MVC — but a culture-sensitive comparison here would have matched it.
    /// </summary>
    [Fact]
    public void Version_SoftHyphenInTheVersionSegment_IsNotNightscout() =>
        NightscoutApiPath.Version(new PathString("/api/v­1/x")).Should().BeNull();
}
