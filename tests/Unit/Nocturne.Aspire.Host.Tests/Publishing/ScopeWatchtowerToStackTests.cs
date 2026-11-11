using FluentAssertions;
using Nocturne.Aspire.Host.Publishing;
using Xunit;
using static Nocturne.Aspire.Host.Tests.Publishing.ComposeYaml;

namespace Nocturne.Aspire.Host.Tests.Publishing;

public class ScopeWatchtowerToStackTests
{
    private const string Compose = """
        services:
          other:
            image: other:1
            configs:
            - source: other-config
              target: /etc/other.conf
          caddy:
            image: caddy:2
          nocturne-api:
            image: nocturne-api
            labels:
              existing: "kept"
          watchtower:
            image: watchtower
        configs:
          other-config:
            content: "x"
        """;

    [Fact]
    public void ServiceWithConfigsIsExcludedWhateverItIsNamed()
    {
        var result = PortainerComposePublisherExtensions.ScopeWatchtowerToStack(Compose);

        Label(result, "other").Should().Be("false");
    }

    [Fact]
    public void ServiceWithoutConfigsIsIncludedWhateverItIsNamed()
    {
        var result = PortainerComposePublisherExtensions.ScopeWatchtowerToStack(Compose);

        Label(result, "caddy").Should().Be("true");
    }

    [Fact]
    public void ServiceWithoutConfigsKeepsItsLabels()
    {
        var result = PortainerComposePublisherExtensions.ScopeWatchtowerToStack(Compose);

        Label(result, "nocturne-api").Should().Be("true");
        result.Should().Contain("existing: \"kept\"");
    }

    [Fact]
    public void WatchtowerStillUpdatesItself()
    {
        var result = PortainerComposePublisherExtensions.ScopeWatchtowerToStack(Compose);

        Label(result, "watchtower").Should().Be("true");
    }
}
