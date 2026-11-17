using FluentAssertions;
using Nocturne.Connectors.Glooko.Configurations;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Configurations;

/// <summary>
/// Every allowed region resolves to its own API host and web origin, never to the US fallback.
/// </summary>
public class GlookoConstantsRegionTests
{
    [Theory]
    [InlineData("CA", "https://ca.api.glooko.com", "https://ca.my.glooko.com")]
    [InlineData("DE-FR", "https://de-fr.api.glooko.com", "https://de-fr.my.glooko.com")]
    [InlineData("de-fr", "https://de-fr.api.glooko.com", "https://de-fr.my.glooko.com")]
    [InlineData("EU", "https://eu.api.glooko.com", "https://eu.my.glooko.com")]
    [InlineData("US", "https://api.glooko.com", "https://my.glooko.com")]
    public void Region_resolves_to_its_own_host(string region, string baseUrl, string webOrigin)
    {
        GlookoConstants.ResolveBaseUrl(region).Should().Be(baseUrl);
        GlookoConstants.ResolveWebOrigin(region).Should().Be(webOrigin);
    }

    [Fact]
    public void Every_allowed_region_has_both_mappings()
    {
        foreach (var region in GlookoConstants.AllowedRegions)
        {
            GlookoConstants.ServerMapping.Should().ContainKey(region);
            GlookoConstants.WebOriginMapping.Should().ContainKey(region);
        }
    }
}
