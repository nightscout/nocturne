using FluentAssertions;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Dexcom.Configurations;
using Xunit;

namespace Nocturne.Connectors.Dexcom.Tests.Services;

/// <summary>
///     Regression tests: a Dexcom config with Server = "EU" must resolve to
///     shareous1.dexcom.com, not share2.dexcom.com (the US server).
/// </summary>
public class DexcomServerResolutionTests
{
    private readonly ConnectorServerResolver<DexcomConnectorConfiguration> _resolver = new(
        new Dictionary<string, string>
        {
            ["US"] = DexcomConstants.Servers.Us,
            ["EU"] = DexcomConstants.Servers.Ous,
            ["OUS"] = DexcomConstants.Servers.Ous
        },
        config => ((DexcomConnectorConfiguration)config).Server,
        null);

    [Fact]
    public void Resolve_WithEuConfig_ReturnsEuServer()
    {
        var config = new DexcomConnectorConfiguration { Server = "EU" };

        var uri = _resolver.Resolve(config);

        uri.Should().NotBeNull();
        uri!.Host.Should().Be("shareous1.dexcom.com");
    }

    [Fact]
    public void Resolve_WithUsConfig_ReturnsUsServer()
    {
        var config = new DexcomConnectorConfiguration { Server = "US" };

        var uri = _resolver.Resolve(config);

        uri.Should().NotBeNull();
        uri!.Host.Should().Be("share2.dexcom.com");
    }

    [Fact]
    public void BuildUrl_WithEuConfig_ProducesAbsoluteEuUrl()
    {
        var config = new DexcomConnectorConfiguration { Server = "EU" };

        var url = _resolver.BuildUrl(config, "/ShareWebServices/Services/General/AuthenticatePublisherAccount");

        url.Should().StartWith("https://shareous1.dexcom.com/");
        url.Should().Contain("AuthenticatePublisherAccount");
    }
}
